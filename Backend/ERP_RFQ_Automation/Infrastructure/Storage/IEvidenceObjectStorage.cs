using System.Net;
using System.Security.Cryptography;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Options;

namespace ERP_RFQ_Automation.Infrastructure.Storage;

public sealed record EvidenceObject(
    string StorageUri,
    string Bucket,
    string Key,
    string Version,
    string? ETag,
    long ByteSize);

public interface IEvidenceObjectStorage
{
    bool IsDurable { get; }

    Task ProbeAsync(CancellationToken ct = default);

    Task<EvidenceObject> WriteImmutableAsync(
        long businessUnitId,
        string zone,
        string sha256,
        string extension,
        ReadOnlyMemory<byte> content,
        CancellationToken ct = default);

    Task<Stream> OpenVerifiedReadAsync(
        string storageUri,
        string expectedSha256,
        CancellationToken ct = default);
}

public sealed class LocalEvidenceObjectStorage : IEvidenceObjectStorage
{
    private readonly IFileStorage _files;

    public LocalEvidenceObjectStorage(IFileStorage files) => _files = files;

    public bool IsDurable => false;

    public Task ProbeAsync(CancellationToken ct = default) => Task.CompletedTask;

    public async Task<EvidenceObject> WriteImmutableAsync(
        long businessUnitId,
        string zone,
        string sha256,
        string extension,
        ReadOnlyMemory<byte> content,
        CancellationToken ct = default)
    {
        ValidateIdentity(businessUnitId, zone, sha256);
        var relative = BuildKey(businessUnitId, zone, sha256, extension);
        var path = await _files.WriteImmutableAsync(relative, content, ct);
        await using var stored = await _files.OpenReadAsync(path, ct);
        await VerifyAsync(stored, sha256, content.Length, ct);
        return new EvidenceObject(path, "local", relative.Replace('\\', '/'), sha256, sha256, content.Length);
    }

    public async Task<Stream> OpenVerifiedReadAsync(
        string storageUri,
        string expectedSha256,
        CancellationToken ct = default)
    {
        var source = await _files.OpenReadAsync(storageUri, ct);
        try
        {
            return await CopyAndVerifyAsync(source, expectedSha256, ct);
        }
        finally
        {
            await source.DisposeAsync();
        }
    }

    internal static string BuildKey(long businessUnitId, string zone, string sha256, string extension)
    {
        var ext = NormalizeExtension(extension);
        return Path.Combine("Evidence", "tenants", businessUnitId.ToString(), zone, "sha256", sha256[..2], sha256 + ext);
    }

    internal static void ValidateIdentity(long businessUnitId, string zone, string sha256)
    {
        if (businessUnitId <= 0)
            throw new ArgumentOutOfRangeException(nameof(businessUnitId));
        if (zone is not ("quarantine" or "cleared"))
            throw new ArgumentException("Evidence zone must be quarantine or cleared.", nameof(zone));
        if (sha256.Length != 64 || sha256.Any(c => !Uri.IsHexDigit(c)) || sha256 != sha256.ToLowerInvariant())
            throw new ArgumentException("A lowercase SHA-256 digest is required.", nameof(sha256));
    }

    internal static string NormalizeExtension(string extension)
    {
        if (string.IsNullOrWhiteSpace(extension))
            return string.Empty;
        var ext = extension.StartsWith('.') ? extension : "." + extension;
        if (ext.Length > 12 || ext.Skip(1).Any(c => !char.IsAsciiLetterOrDigit(c)))
            throw new ArgumentException("The evidence extension is invalid.", nameof(extension));
        return ext.ToLowerInvariant();
    }

    internal static async Task VerifyAsync(Stream stream, string expectedSha256, long expectedLength, CancellationToken ct)
    {
        if (stream.CanSeek && stream.Length != expectedLength)
            throw new InvalidDataException("Stored evidence length does not match the immutable object identity.");
        var actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct)).ToLowerInvariant();
        if (!CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(actual), Convert.FromHexString(expectedSha256)))
            throw new InvalidDataException("Stored evidence hash does not match its immutable object identity.");
    }

    internal static async Task<MemoryStream> CopyAndVerifyAsync(Stream source, string expectedSha256, CancellationToken ct)
    {
        var copy = new MemoryStream();
        await source.CopyToAsync(copy, ct);
        copy.Position = 0;
        await VerifyAsync(copy, expectedSha256, copy.Length, ct);
        copy.Position = 0;
        return copy;
    }
}

public sealed class S3EvidenceStorageOptions
{
    public const string SectionName = "EvidenceStorage";
    public string Provider { get; set; } = "Local";
    public string? ServiceUrl { get; set; }
    public string? Region { get; set; }
    public string? AccessKeyId { get; set; }
    public string? SecretAccessKey { get; set; }
    public string? Bucket { get; set; }
    public bool ForcePathStyle { get; set; } = true;
}

public sealed class S3EvidenceObjectStorage : IEvidenceObjectStorage, IDisposable
{
    private readonly S3EvidenceStorageOptions _options;
    private readonly AmazonS3Client _client;

    public S3EvidenceObjectStorage(IOptions<S3EvidenceStorageOptions> options)
    {
        _options = options.Value;
        if (string.IsNullOrWhiteSpace(_options.ServiceUrl)
            || string.IsNullOrWhiteSpace(_options.AccessKeyId)
            || string.IsNullOrWhiteSpace(_options.SecretAccessKey)
            || string.IsNullOrWhiteSpace(_options.Bucket))
            throw new InvalidOperationException("S3 evidence storage requires service URL, credentials, and bucket.");

        _client = new AmazonS3Client(
            _options.AccessKeyId,
            _options.SecretAccessKey,
            new AmazonS3Config
            {
                ServiceURL = _options.ServiceUrl,
                AuthenticationRegion = string.IsNullOrWhiteSpace(_options.Region) ? "auto" : _options.Region,
                ForcePathStyle = _options.ForcePathStyle
            });
    }

    public bool IsDurable => true;

    public async Task ProbeAsync(CancellationToken ct = default)
    {
        var payload = RandomNumberGenerator.GetBytes(32);
        var digest = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
        var key = $"_readiness/{Guid.NewGuid():N}.probe";
        try
        {
            await using var input = new MemoryStream(payload, writable: false);
            var put = new PutObjectRequest
            {
                BucketName = _options.Bucket,
                Key = key,
                InputStream = input,
                AutoCloseStream = false,
                ContentType = "application/octet-stream",
                IfNoneMatch = "*"
            };
            put.Metadata["sha256"] = digest;
            await _client.PutObjectAsync(put, ct);

            using var stored = await _client.GetObjectAsync(new GetObjectRequest
            {
                BucketName = _options.Bucket,
                Key = key
            }, ct);
            await LocalEvidenceObjectStorage.VerifyAsync(stored.ResponseStream, digest, payload.Length, ct);
        }
        finally
        {
            await _client.DeleteObjectAsync(new DeleteObjectRequest
            {
                BucketName = _options.Bucket,
                Key = key
            }, ct);
        }
    }

    public async Task<EvidenceObject> WriteImmutableAsync(
        long businessUnitId,
        string zone,
        string sha256,
        string extension,
        ReadOnlyMemory<byte> content,
        CancellationToken ct = default)
    {
        LocalEvidenceObjectStorage.ValidateIdentity(businessUnitId, zone, sha256);
        var key = LocalEvidenceObjectStorage.BuildKey(businessUnitId, zone, sha256, extension).Replace('\\', '/');
        var existing = await TryHeadAsync(key, ct);
        if (existing is not null)
            return ValidateExisting(existing, key, sha256, content.Length);

        await using var body = new MemoryStream(content.ToArray(), writable: false);
        var request = new PutObjectRequest
        {
            BucketName = _options.Bucket,
            Key = key,
            InputStream = body,
            AutoCloseStream = false,
            ContentType = "application/octet-stream",
            IfNoneMatch = "*"
        };
        request.Metadata["sha256"] = sha256;

        try
        {
            var response = await _client.PutObjectAsync(request, ct);
            return new EvidenceObject(ToUri(key, response.VersionId), _options.Bucket!, key,
                response.VersionId ?? sha256, response.ETag, content.Length);
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode is HttpStatusCode.PreconditionFailed or HttpStatusCode.Conflict)
        {
            var raced = await TryHeadAsync(key, ct)
                ?? throw new InvalidOperationException("Immutable evidence object raced but cannot be resolved.", ex);
            return ValidateExisting(raced, key, sha256, content.Length);
        }
    }

    public async Task<Stream> OpenVerifiedReadAsync(
        string storageUri,
        string expectedSha256,
        CancellationToken ct = default)
    {
        var (bucket, key, versionId) = ParseUri(storageUri);
        using var response = await _client.GetObjectAsync(new GetObjectRequest
        {
            BucketName = bucket,
            Key = key,
            VersionId = versionId
        }, ct);
        return await LocalEvidenceObjectStorage.CopyAndVerifyAsync(response.ResponseStream, expectedSha256, ct);
    }

    private async Task<GetObjectMetadataResponse?> TryHeadAsync(string key, CancellationToken ct)
    {
        try
        {
            return await _client.GetObjectMetadataAsync(_options.Bucket, key, ct);
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    private EvidenceObject ValidateExisting(GetObjectMetadataResponse metadata, string key, string sha256, long length)
    {
        var storedHash = metadata.Metadata["x-amz-meta-sha256"];
        if (!string.Equals(storedHash, sha256, StringComparison.Ordinal)
            || metadata.ContentLength != length)
            throw new InvalidDataException("An existing evidence object conflicts with its content address.");
        return new EvidenceObject(ToUri(key, metadata.VersionId), _options.Bucket!, key,
            metadata.VersionId ?? sha256, metadata.ETag, metadata.ContentLength);
    }

    private string ToUri(string key, string? versionId)
    {
        var uri = $"s3://{_options.Bucket}/{Uri.EscapeDataString(key).Replace("%2F", "/", StringComparison.OrdinalIgnoreCase)}";
        return string.IsNullOrWhiteSpace(versionId) ? uri : uri + "?versionId=" + Uri.EscapeDataString(versionId);
    }

    private static (string Bucket, string Key, string? VersionId) ParseUri(string storageUri)
    {
        if (!Uri.TryCreate(storageUri, UriKind.Absolute, out var uri) || uri.Scheme != "s3")
            throw new ArgumentException("A valid s3:// evidence URI is required.", nameof(storageUri));
        var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(uri.Query);
        return (uri.Host, Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/')),
            query.TryGetValue("versionId", out var value) ? value.ToString() : null);
    }

    public void Dispose() => _client.Dispose();
}
