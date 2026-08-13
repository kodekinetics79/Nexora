using System.Net;
using System.Security.Cryptography;
using Amazon.Runtime;
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

/// <summary>
/// Outcome of purging ONE stored evidence object. <paramref name="Deleted"/> is false when
/// the object was already absent — the normal, non-error case for a re-run and for the
/// production documents whose bytes were lost to ephemeral storage before the persistent
/// disk existed. <paramref name="BytesFreed"/> is measured, never assumed, so the tenant's
/// "space reclaimed" figure is a fact rather than an estimate.
/// </summary>
public readonly record struct EvidenceObjectPurgeResult(bool Deleted, long BytesFreed)
{
    public static readonly EvidenceObjectPurgeResult Absent = new(false, 0);
}

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

    /// <summary>
    /// Permanently removes the BYTES of one evidence object. The evidence RECORD is never
    /// touched here — <c>source_documents</c> physically refuses DELETE
    /// (<c>trg_source_documents_no_delete</c>) and freezes its hash, filename and size
    /// against modification, which is what makes "we deleted the file" auditable rather
    /// than indistinguishable from "we lost the file".
    ///
    /// <para>
    /// Addressed by the stored (bucket, key, version) triple rather than by a URI so each
    /// provider deletes the exact object it wrote: on a versioned S3 bucket a delete
    /// WITHOUT the version id only writes a delete marker and reclaims nothing at all,
    /// which would make the reclaimed-bytes figure a lie.
    /// </para>
    ///
    /// <para>Default implementation refuses rather than silently reporting success: a
    /// provider that cannot delete must surface as an error the purge records, never as a
    /// purge that quietly freed nothing.</para>
    /// </summary>
    Task<EvidenceObjectPurgeResult> TryDeletePurgedObjectAsync(
        string bucket,
        string key,
        string version,
        CancellationToken ct = default)
        => throw new NotSupportedException(
            $"{GetType().Name} does not support purging stored evidence bytes.");

    /// <summary>
    /// Size of a stored object, or null when it is not there. Exists so a dry run can quote
    /// the bytes it WOULD free by measuring the same objects the real run would delete,
    /// rather than by multiplying a recorded size and hoping. The ~15 production documents
    /// whose bytes vanished with ephemeral storage must not inflate the promised figure.
    /// </summary>
    Task<long?> TryMeasureObjectAsync(
        string bucket,
        string key,
        string version,
        CancellationToken ct = default)
        => Task.FromResult<long?>(null);
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

    public Task<EvidenceObjectPurgeResult> TryDeletePurgedObjectAsync(
        string bucket,
        string key,
        string version,
        CancellationToken ct = default)
    {
        _ = version;
        if (string.IsNullOrWhiteSpace(key))
            throw new ArgumentException("An evidence object key is required.", nameof(key));
        if (!string.IsNullOrWhiteSpace(bucket)
            && !string.Equals(bucket, "local", StringComparison.Ordinal))
            throw new InvalidDataException(
                "The evidence object was not written by local storage and cannot be purged by it.");

        // Size is read before the delete because afterwards there is nothing to measure.
        // Missing file => Absent, which the purge treats as success, not as an error.
        var path = _files.ResolvePath(key);
        var bytes = File.Exists(path) ? new FileInfo(path).Length : 0L;
        return DeleteAsync(key, bytes, ct);
    }

    private async Task<EvidenceObjectPurgeResult> DeleteAsync(string key, long bytes, CancellationToken ct)
    {
        var deleted = await _files.TryDeleteAsync(key, ct);
        return deleted ? new EvidenceObjectPurgeResult(true, bytes) : EvidenceObjectPurgeResult.Absent;
    }

    public Task<long?> TryMeasureObjectAsync(
        string bucket,
        string key,
        string version,
        CancellationToken ct = default)
    {
        _ = version;
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(key))
            return Task.FromResult<long?>(null);
        if (!string.IsNullOrWhiteSpace(bucket)
            && !string.Equals(bucket, "local", StringComparison.Ordinal))
            return Task.FromResult<long?>(null);

        // ResolvePath still applies: a dry run must not be a way to stat arbitrary files.
        var path = _files.ResolvePath(key);
        return Task.FromResult<long?>(File.Exists(path) ? new FileInfo(path).Length : null);
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
        ValidateServiceEndpoint(_options.ServiceUrl);

        _client = new AmazonS3Client(
            _options.AccessKeyId,
            _options.SecretAccessKey,
            new AmazonS3Config
            {
                ServiceURL = _options.ServiceUrl,
                AuthenticationRegion = string.IsNullOrWhiteSpace(_options.Region) ? "auto" : _options.Region,
                ForcePathStyle = _options.ForcePathStyle,

                // AWS SDK v4 attaches a CRC32 integrity checksum to every upload by default,
                // which means an x-amz-sdk-checksum-algorithm header on requests that do not
                // require one. AWS implements it; S3-COMPATIBLE stores largely do not, and
                // Backblaze B2 answers the whole request with 501 "A header you provided
                // implies functionality that is not implemented".
                //
                // That failed the evidence write, which fails ingestion, which meant every
                // uploaded RFQ was refused — reported to the operator as a per-file queueing
                // fault they were told to retry. The endpoint was configured correctly; only
                // the header was wrong. Requesting checksums only where the operation
                // genuinely requires them keeps AWS behaviour intact and stops assuming every
                // S3 endpoint is AWS.
                //
                // Deliberately set here rather than left to AWS_REQUEST_CHECKSUM_CALCULATION /
                // AWS_RESPONSE_CHECKSUM_VALIDATION: an unset environment variable would
                // silently restore the failure on a fresh deployment.
                RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
                ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED
            });
    }

    public bool IsDurable => true;

    public async Task ProbeAsync(CancellationToken ct = default)
    {
        ValidateServiceEndpoint(_options.ServiceUrl!);
        await VerifyBucketVersioningAsync(ct);

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
        EnsureConfiguredBucket(bucket, _options.Bucket!);
        using var response = await _client.GetObjectAsync(new GetObjectRequest
        {
            BucketName = bucket,
            Key = key,
            VersionId = versionId
        }, ct);
        return await LocalEvidenceObjectStorage.CopyAndVerifyAsync(response.ResponseStream, expectedSha256, ct);
    }

    public async Task<EvidenceObjectPurgeResult> TryDeletePurgedObjectAsync(
        string bucket,
        string key,
        string version,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(key))
            throw new ArgumentException("An evidence object key is required.", nameof(key));
        EnsureConfiguredBucket(bucket, _options.Bucket!);

        // The version id is load-bearing, not decoration. Evidence buckets are versioned
        // (ProbeAsync refuses to be ready otherwise), and a DeleteObject WITHOUT a version
        // id on a versioned bucket only adds a delete marker: the bytes stay, storage cost
        // stays, and the tenant is told space was reclaimed when none was. Deleting the
        // specific version is the only form that actually frees anything.
        var versionId = NormalizeVersionId(version);
        var existing = await TryHeadAsync(key, versionId, ct);
        if (existing is null)
            return EvidenceObjectPurgeResult.Absent;

        await _client.DeleteObjectAsync(new DeleteObjectRequest
        {
            BucketName = _options.Bucket,
            Key = key,
            VersionId = versionId
        }, ct);
        return new EvidenceObjectPurgeResult(true, existing.ContentLength);
    }

    public async Task<long?> TryMeasureObjectAsync(
        string bucket,
        string key,
        string version,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(key))
            return null;
        EnsureConfiguredBucket(bucket, _options.Bucket!);
        var metadata = await TryHeadAsync(key, NormalizeVersionId(version), ct);
        return metadata?.ContentLength;
    }

    /// <summary>
    /// Local-written objects record the content hash where S3 records a version id (see
    /// <see cref="LocalEvidenceObjectStorage.WriteImmutableAsync"/>), and a bucket with
    /// versioning suspended returns none at all. A 64-hex value is therefore a hash, not a
    /// version, and must not be sent as one — S3 would 400 on it.
    /// </summary>
    internal static string? NormalizeVersionId(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
            return null;
        var trimmed = version.Trim();
        if (trimmed.Length == 64 && trimmed.All(Uri.IsHexDigit))
            return null;
        return trimmed;
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

    private async Task<GetObjectMetadataResponse?> TryHeadAsync(
        string key, string? versionId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(versionId))
            return await TryHeadAsync(key, ct);
        try
        {
            return await _client.GetObjectMetadataAsync(new GetObjectMetadataRequest
            {
                BucketName = _options.Bucket,
                Key = key,
                VersionId = versionId
            }, ct);
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode is HttpStatusCode.NotFound
                                           or HttpStatusCode.MethodNotAllowed)
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

    private async Task VerifyBucketVersioningAsync(CancellationToken ct)
    {
        GetBucketVersioningResponse response;
        try
        {
            response = await _client.GetBucketVersioningAsync(new GetBucketVersioningRequest
            {
                BucketName = _options.Bucket
            }, ct);
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode is HttpStatusCode.MethodNotAllowed
                                           or HttpStatusCode.NotImplemented)
        {
            // Some S3-compatible stores do not expose the versioning API. The immutable
            // conditional-write/read verification below remains the enforceable control.
            return;
        }

        EnsureVersioningEnabled(response.VersioningConfig?.Status);
    }

    internal static void ValidateServiceEndpoint(string serviceUrl)
    {
        if (!Uri.TryCreate(serviceUrl, UriKind.Absolute, out var endpoint)
            || endpoint.Scheme is not ("http" or "https"))
            throw new InvalidOperationException("S3 evidence storage requires a valid HTTP(S) service URL.");

        if (endpoint.Scheme == Uri.UriSchemeHttps || endpoint.IsLoopback)
            return;

        throw new InvalidOperationException(
            "S3 evidence storage requires HTTPS for non-local service endpoints.");
    }

    internal static void EnsureConfiguredBucket(string uriBucket, string configuredBucket)
    {
        if (!string.Equals(uriBucket, configuredBucket, StringComparison.Ordinal))
            throw new InvalidDataException(
                "The evidence object URI does not belong to the configured storage bucket.");
    }

    internal static void EnsureVersioningEnabled(VersionStatus? status)
    {
        if (status != VersionStatus.Enabled)
            throw new InvalidOperationException(
                "S3 evidence storage bucket versioning must be enabled before the service is ready.");
    }

    public void Dispose() => _client.Dispose();
}
