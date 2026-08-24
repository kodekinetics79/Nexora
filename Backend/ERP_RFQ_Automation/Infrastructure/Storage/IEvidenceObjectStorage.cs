using System.Net;
using System.Security.Cryptography;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Options;

namespace ERP_RFQ_Automation.Infrastructure.Storage;

/// <summary>
/// Durable evidence storage itself is unavailable or misconfigured — the bucket does not
/// exist, credentials are refused, or the provider cannot be reached. NOT a verdict about
/// the document: every file in flight would fail identically, so the caller must refuse the
/// whole batch rather than tell N operators to retry N files.
///
/// <para>Raised for the 2026-08-12 incident, where evidence storage was repointed at a
/// misspelled bucket and four uploads were each answered with "upload this file again" —
/// advice that could not work, while the readiness probe already read Unhealthy with the
/// cause.</para>
///
/// <para>The provider's own account (bucket, endpoint, error code) lives in
/// <see cref="Exception.InnerException"/> for the SERVER LOG only.
/// <see cref="Exception.Message"/> is operator-safe and carries no infrastructure
/// detail, so it can be put straight into a response body.</para>
/// </summary>
public sealed class EvidenceStorageUnavailableException : Exception
{
    /// <summary>
    /// The single machine code for "we cannot durably store documents right now", already
    /// emitted by <c>SecurityScanRecoveryService</c> for the read side. One code, both sides.
    /// </summary>
    public const string ErrorCode = "evidence_storage_unavailable";

    public EvidenceStorageUnavailableException(bool isConfigurationFault, Exception? inner = null)
        : base(isConfigurationFault
            ? "Document storage is not configured, so uploads are paused."
            : "Document storage is unavailable, so uploads are paused.", inner)
        => IsConfigurationFault = isConfigurationFault;

    /// <summary>
    /// True when the deployment is misconfigured (no bucket, refused credentials, wrong
    /// endpoint) — waiting cannot fix it, so no retry may be offered. False when the
    /// provider is merely unreachable and may come back on its own.
    /// </summary>
    public bool IsConfigurationFault { get; }

    /// <summary>
    /// What the reader should do next. The two faults demand OPPOSITE actions — a misspelled
    /// bucket needs a human to change a setting and will never clear by waiting, while an
    /// unreachable provider genuinely can come back — so they must never share one sentence.
    /// Telling someone to wait out a typo is the 2026-08-12 defect; telling someone a
    /// thirty-second blip needs an administrator is the same defect inverted.
    /// </summary>
    public string OperatorNextAction => IsConfigurationFault
        ? "Retrying will not help until an administrator corrects the document storage settings."
        : "This can clear on its own — try again shortly, and tell an administrator if it persists.";

    /// <summary>
    /// The whole operator-facing sentence, cause then next action. Deliberately says nothing
    /// about what a caller did or did not accept: only the caller knows whether work was
    /// already stored before the store failed, and a shared sentence that asserted "nothing
    /// was accepted" would be a lie on every door that ingests more than one document.
    /// </summary>
    public string OperatorDetail => $"{Message} {OperatorNextAction}";
}

/// <summary>
/// Splits a failed evidence write into "this document" and "the store".
///
/// <para>Allow-list, not deny-list: ONLY the faults named in
/// <see cref="IsPerDocumentFault"/> are verdicts about the document in hand. Anything else
/// out of a write is the store failing, and answering it per file — "upload this file
/// again" — is the useless advice the 2026-08-12 incident produced four times. A deny-list
/// would silently misfile the next unknown provider error code back into "retry this
/// file", which is the bug being fixed.</para>
/// </summary>
internal static class EvidenceStorageFaults
{
    /// <summary>The race resolution in <see cref="S3EvidenceObjectStorage.WriteImmutableAsync"/>.</summary>
    internal const string UnresolvableRaceMessage = "Immutable evidence object raced but cannot be resolved.";

    internal static bool IsPerDocumentFault(Exception exception) =>
        exception is ArgumentException or InvalidDataException
        || exception is InvalidOperationException { Message: UnresolvableRaceMessage };

    /// <summary>
    /// True when this failure means the store, not the document. A caller-driven cancellation
    /// is excluded deliberately: a client that hung up is not a storage outage and must keep
    /// propagating as the cancellation it is.
    /// </summary>
    internal static bool IsStoreUnavailable(Exception exception, CancellationToken ct) =>
        !IsPerDocumentFault(exception)
        && !(exception is OperationCanceledException && ct.IsCancellationRequested);

    /// <summary>
    /// Distinguishes "someone typed the bucket name wrong" from "the provider blinked". The
    /// operator's next action differs completely — edit configuration versus wait — so the
    /// two must not collapse into one message.
    /// </summary>
    internal static bool IsConfigurationFault(Exception exception) => exception switch
    {
        AmazonS3Exception s3 => s3.ErrorCode is "NoSuchBucket" or "AccessDenied"
            or "InvalidAccessKeyId" or "SignatureDoesNotMatch" or "InvalidSecurity"
            or "PermanentRedirect" or "AuthorizationHeaderMalformed",
        // S3EvidenceObjectStorage raises InvalidOperationException only for endpoint,
        // credential and versioning assertions; the one per-object race that shares the type
        // is filtered out by IsPerDocumentFault before this is ever consulted.
        InvalidOperationException => true,
        UnauthorizedAccessException => true,
        _ => false
    };

    internal static EvidenceStorageUnavailableException Unavailable(Exception exception) =>
        new(IsConfigurationFault(exception), exception);
}

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

    /// <summary>
    /// Every object currently stored under <paramref name="keyPrefix"/>, with its size.
    ///
    /// <para>Exists for one job: finding stored objects that NO database row points at. 170 of
    /// this deployment's 273 evidence objects have no pointer — quarantine siblings nothing ever
    /// deleted, and raw <c>.eml</c> in a zone the row-driven purge cannot address — so a purge
    /// driven only by rows can never reach them however long it runs.</para>
    ///
    /// <para>The default REFUSES rather than returning an empty list. An empty list from a
    /// provider that simply cannot enumerate would read as "there is nothing unreferenced here",
    /// which is the one answer a sweep must never invent: it would silently report a clean store
    /// while the bytes stayed.</para>
    /// </summary>
    Task<IReadOnlyList<StoredEvidenceObject>> ListObjectsUnderPrefixAsync(
        string keyPrefix,
        CancellationToken ct = default)
        => throw new NotSupportedException(
            $"{GetType().Name} cannot list stored evidence objects.");
}

/// <summary>One object found in the store, addressed the same way a purge addresses it.
/// <paramref name="Version"/> is whatever the provider records — a version id on S3, the
/// content hash on local storage — so a sweep deletes exactly the object it listed.</summary>
public sealed record StoredEvidenceObject(string Bucket, string Key, string Version, long ByteSize);

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
        try
        {
            ValidateIdentity(businessUnitId, zone, sha256);
            var relative = BuildKey(businessUnitId, zone, sha256, extension);
            var path = await _files.WriteImmutableAsync(relative, content, ct);
            await using var stored = await _files.OpenReadAsync(path, ct);
            await VerifyAsync(stored, sha256, content.Length, ct);
            return new EvidenceObject(path, "local", relative.Replace('\\', '/'), sha256, sha256, content.Length);
        }
        catch (Exception exception) when (EvidenceStorageFaults.IsStoreUnavailable(exception, ct))
        {
            // A full or read-only disk is the local mirror of the misspelled bucket: every file
            // in the batch would fail the same way, so it is the STORE that is unavailable and
            // not the document. InvalidDataException from VerifyAsync stays out of here — that
            // one is a genuine per-document integrity verdict.
            throw EvidenceStorageFaults.Unavailable(exception);
        }
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

    public Task<IReadOnlyList<StoredEvidenceObject>> ListObjectsUnderPrefixAsync(
        string keyPrefix, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(keyPrefix))
            throw new ArgumentException("A key prefix is required.", nameof(keyPrefix));

        // ResolvePath applies its full containment check (root-prefix comparison plus
        // per-segment symlink rejection), so a crafted prefix cannot enumerate outside
        // evidence storage.
        var directory = _files.ResolvePath(keyPrefix);
        if (!Directory.Exists(directory))
            return Task.FromResult<IReadOnlyList<StoredEvidenceObject>>([]);

        var root = _files.RootPath;
        var found = new List<StoredEvidenceObject>();
        foreach (var path in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            var key = Path.GetRelativePath(root, path).Replace('\\', '/');
            found.Add(new StoredEvidenceObject("local", key, string.Empty, new FileInfo(path).Length));
        }
        return Task.FromResult<IReadOnlyList<StoredEvidenceObject>>(found);
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
        // "raw-mail" is a THIRD zone, deliberately outside the quarantine/cleared pair.
        //
        // Raw inbound messages were briefly written to "quarantine", which is legal and reads as
        // correct — an un-inspected message really is quarantined. But the retention purge
        // derives a sibling key by string-swapping /quarantine/ <-> /cleared/ and deletes BOTH
        // (EvidenceRetentionEligibility.ZoneKeysFor). Since .eml is on the document intake
        // allow-list, the same bytes can legitimately exist as a SourceDocument, and purging
        // that document would delete the authoritative raw message an EmailInquiryAssembly
        // points at — evidence loss with nothing to say it happened, because an assembly is not
        // a SourceDocument and none of the retention guards can see it.
        //
        // A separate zone matches neither swap arm, so ZoneKeysFor returns only the exact key
        // and the collision cannot occur. The whitelist is not weakened: this is one more named
        // constant, not an opening for arbitrary paths.
        if (zone is not ("quarantine" or "cleared" or "raw-mail"))
            throw new ArgumentException(
                "Evidence zone must be quarantine, cleared or raw-mail.", nameof(zone));
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

    /// <summary>
    /// Set once, the first time this endpoint answers a conditional write with 501. See
    /// <see cref="PutAsync"/>: the fallback is a property of the STORE, so it is remembered
    /// for the process rather than rediscovered — and paid for — on every upload.
    /// </summary>
    private volatile bool _conditionalWritesUnsupported;
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

    /// <summary>
    /// Puts an object, tolerating a store that does not implement conditional writes.
    ///
    /// <para><c>If-None-Match: *</c> — "create only if absent" — is an S3 feature AWS added in
    /// late 2024. Backblaze B2 and several other S3-compatible stores do not implement it, and
    /// answer the WHOLE request with 501 rather than ignoring the header. That took out the
    /// evidence write, which ingestion performs before it queues anything, so every uploaded
    /// RFQ was refused and the operator was told the file had failed while queueing and to try
    /// again. Nothing about the file was wrong.</para>
    ///
    /// <para>Dropping the precondition on such a store is safe HERE specifically, and would not
    /// be in general. The key is content-addressed — it contains the SHA-256 of the bytes, see
    /// <see cref="LocalEvidenceObjectStorage.BuildKey"/> — and the caller has already resolved
    /// an existing object through <c>TryHeadAsync</c>. The precondition only closes the narrow
    /// race between that HEAD and this PUT, and the loser of that race writes byte-identical
    /// content to a key derived from those same bytes. What is lost is belt over braces, not
    /// the guarantee itself.</para>
    /// </summary>
    private async Task<PutObjectResponse> PutAsync(PutObjectRequest request, CancellationToken ct)
    {
        if (_conditionalWritesUnsupported)
        {
            request.IfNoneMatch = null;
            return await _client.PutObjectAsync(request, ct);
        }

        try
        {
            return await _client.PutObjectAsync(request, ct);
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotImplemented)
        {
            _conditionalWritesUnsupported = true;
            request.IfNoneMatch = null;
            // The SDK read the stream to send the failed attempt; rewind or the retry stores
            // zero bytes under a key that claims their hash. Both call sites pass a seekable
            // MemoryStream with AutoCloseStream disabled, which is what makes this safe.
            if (request.InputStream is { CanSeek: true } stream)
                stream.Position = 0;
            return await _client.PutObjectAsync(request, ct);
        }
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
            await PutAsync(put, ct);

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

    /// <summary>
    /// Classification boundary for the 2026-08-12 incident: <c>Amazon.S3</c> exceptions stop
    /// HERE and never travel up as themselves. Controllers must not depend on the SDK, and
    /// keeping the provider type inside the storage layer is what makes it structurally
    /// impossible for a bucket name to reach a response body. The original is preserved as
    /// the inner exception, so the Render log still records
    /// "The specified bucket does not exist: …" verbatim.
    /// </summary>
    public async Task<EvidenceObject> WriteImmutableAsync(
        long businessUnitId,
        string zone,
        string sha256,
        string extension,
        ReadOnlyMemory<byte> content,
        CancellationToken ct = default)
    {
        try
        {
            return await WriteImmutableCoreAsync(businessUnitId, zone, sha256, extension, content, ct);
        }
        catch (Exception exception) when (EvidenceStorageFaults.IsStoreUnavailable(exception, ct))
        {
            throw EvidenceStorageFaults.Unavailable(exception);
        }
    }

    private async Task<EvidenceObject> WriteImmutableCoreAsync(
        long businessUnitId,
        string zone,
        string sha256,
        string extension,
        ReadOnlyMemory<byte> content,
        CancellationToken ct)
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
            var response = await PutAsync(request, ct);
            return new EvidenceObject(ToUri(key, response.VersionId), _options.Bucket!, key,
                response.VersionId ?? sha256, response.ETag, content.Length);
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode is HttpStatusCode.PreconditionFailed or HttpStatusCode.Conflict)
        {
            var raced = await TryHeadAsync(key, ct)
                ?? throw new InvalidOperationException(EvidenceStorageFaults.UnresolvableRaceMessage, ex);
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
    /// Lists every CURRENT object under the prefix, following the continuation token to the
    /// end. A truncated page silently dropped would understate what is stored, and a sweep
    /// that under-reports is a sweep that leaves bytes behind while claiming it did not.
    /// </summary>
    public async Task<IReadOnlyList<StoredEvidenceObject>> ListObjectsUnderPrefixAsync(
        string keyPrefix, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(keyPrefix))
            throw new ArgumentException("A key prefix is required.", nameof(keyPrefix));

        var found = new List<StoredEvidenceObject>();
        string? continuation = null;
        do
        {
            var page = await _client.ListObjectsV2Async(new ListObjectsV2Request
            {
                BucketName = _options.Bucket,
                Prefix = keyPrefix,
                ContinuationToken = continuation
            }, ct);
            foreach (var entry in page.S3Objects ?? [])
                found.Add(new StoredEvidenceObject(
                    _options.Bucket!, entry.Key, string.Empty, entry.Size ?? 0));
            continuation = page.IsTruncated == true ? page.NextContinuationToken : null;
        }
        while (continuation is not null);
        return found;
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

    /// <summary>
    /// Splits an <c>s3://bucket/key</c> evidence URI, PRESERVING THE BUCKET'S CASE.
    ///
    /// <para>This used to read the bucket from <c>Uri.Host</c>, and System.Uri lowercases the
    /// host — hostnames are case-insensitive, bucket names are not. AWS S3 forbids uppercase
    /// in bucket names so the defect was invisible there, but Backblaze B2 permits mixed case:
    /// against a bucket named <c>NexoraBucket</c>, every WRITE stored
    /// <c>s3://NexoraBucket/...</c> (built from the configured name, verbatim) and every READ
    /// parsed it back as <c>nexorabucket</c>. The ordinal comparison in
    /// <see cref="EnsureConfiguredBucket"/> then rejected the service's own objects, and the
    /// lowercased name was also the one handed to GetObject — so even past that check the
    /// request addressed a bucket that may not exist. Result: uploads succeed forever, every
    /// read fails, and the failure surfaces as "the stored bytes no longer match the hash
    /// recorded at intake" on documents that are perfectly intact.</para>
    /// </summary>
    private static (string Bucket, string Key, string? VersionId) ParseUri(string storageUri)
    {
        const string scheme = "s3://";
        if (string.IsNullOrWhiteSpace(storageUri)
            || !storageUri.StartsWith(scheme, StringComparison.Ordinal))
            throw new ArgumentException("A valid s3:// evidence URI is required.", nameof(storageUri));

        var remainder = storageUri[scheme.Length..];
        var separator = remainder.IndexOf('/');
        if (separator <= 0 || separator == remainder.Length - 1)
            throw new ArgumentException("A valid s3:// evidence URI is required.", nameof(storageUri));

        var bucket = remainder[..separator];
        var pathAndQuery = remainder[(separator + 1)..];
        string? versionId = null;
        var queryStart = pathAndQuery.IndexOf('?');
        if (queryStart >= 0)
        {
            var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(
                pathAndQuery[queryStart..]);
            versionId = query.TryGetValue("versionId", out var value) ? value.ToString() : null;
            pathAndQuery = pathAndQuery[..queryStart];
        }

        return (bucket, Uri.UnescapeDataString(pathAndQuery), versionId);
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

    /// <summary>
    /// Stable text identifying a bucket mismatch. InvalidDataException is sealed and is also
    /// what a genuine DIGEST mismatch raises, so the two cannot be told apart by type — and
    /// they mean opposite things: a digest mismatch says the bytes are present and WRONG,
    /// this says they are almost certainly present and RIGHT, in a bucket the service is no
    /// longer pointed at. Reported as one incident, an operator is told to re-upload
    /// documents that were never damaged. Consumers match on this constant, never on prose.
    /// </summary>
    public const string BucketMismatchMessage =
        "The evidence object URI does not belong to the configured storage bucket.";

    internal static void EnsureConfiguredBucket(string uriBucket, string configuredBucket)
    {
        if (!string.Equals(uriBucket, configuredBucket, StringComparison.Ordinal))
            throw new InvalidDataException(BucketMismatchMessage);
    }

    internal static void EnsureVersioningEnabled(VersionStatus? status)
    {
        if (status != VersionStatus.Enabled)
            throw new InvalidOperationException(
                "S3 evidence storage bucket versioning must be enabled before the service is ready.");
    }

    public void Dispose() => _client.Dispose();
}

/// <summary>
/// Stand-in for a durable provider that was ASKED FOR but could not be built — an S3 block
/// with no bucket, no credentials, or a rejected endpoint.
///
/// <para>Without it the failure lands in the DI factory, so resolving any controller that
/// touches evidence storage throws and every upload answers an unhandled 500 — strictly
/// worse than the incident that motivated this class, because a 500 says nothing at all.
/// Here the deployment gets the same honest refusal as an unreachable bucket: one
/// configuration outcome, logged once at boot with the real reason, and a readiness probe
/// that fails for as long as it is true.</para>
///
/// <para><see cref="IsDurable"/> reports true because the deployment CHOSE durable storage.
/// Reporting false would make the health check say "evidence storage is local and
/// ephemeral", which is a different and untrue diagnosis.</para>
/// </summary>
public sealed class UnconfiguredEvidenceObjectStorage : IEvidenceObjectStorage
{
    private readonly Exception _configurationFault;

    public UnconfiguredEvidenceObjectStorage(Exception configurationFault)
        => _configurationFault = configurationFault;

    public bool IsDurable => true;

    public Task ProbeAsync(CancellationToken ct = default) => throw Refuse();

    public Task<EvidenceObject> WriteImmutableAsync(
        long businessUnitId,
        string zone,
        string sha256,
        string extension,
        ReadOnlyMemory<byte> content,
        CancellationToken ct = default) => throw Refuse();

    public Task<Stream> OpenVerifiedReadAsync(
        string storageUri,
        string expectedSha256,
        CancellationToken ct = default) => throw Refuse();

    private EvidenceStorageUnavailableException Refuse() => new(true, _configurationFault);
}
