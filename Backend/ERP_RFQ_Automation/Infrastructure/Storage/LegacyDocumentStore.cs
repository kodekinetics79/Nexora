using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace ERP_RFQ_Automation.Infrastructure.Storage;

/// <summary>
/// Recognises the two shapes a stored-document pointer can take in <c>Attachments.FilePath</c>,
/// <c>EmailIngests.RawEmailPath</c> and <c>ExtractionJobs.StoragePath</c>: an object-store URI
/// (<c>s3://bucket/Evidence/tenants/7/legacy/sha256/ab/&lt;sha&gt;.pdf</c>, or a test scheme) or a
/// filesystem path (relative under the storage root, or absolute). Every reader must accept both
/// for as long as rows of both shapes exist — 24 production rows point at bytes that were lost
/// with an earlier host and can never be migrated (docs/design/evidence-object-store-cutover.md).
/// </summary>
public static partial class EvidenceObjectUris
{
    public static bool IsObjectUri(string? pathOrUri)
        => !string.IsNullOrWhiteSpace(pathOrUri) && pathOrUri.Contains("://", StringComparison.Ordinal);

    /// <summary>
    /// The digest embedded in a content-addressed key. Recovering it from the key is what lets a
    /// row with no digest column (<c>RawEmailPath</c>) still be read through a verifying read.
    /// </summary>
    public static bool TryParseDigest(string? pathOrUri, out string sha256)
    {
        sha256 = string.Empty;
        if (string.IsNullOrWhiteSpace(pathOrUri)) return false;
        var match = KeyPattern().Match(pathOrUri.Replace('\\', '/'));
        if (!match.Success) return false;
        sha256 = match.Groups["sha"].Value;
        return true;
    }

    /// <summary>The zone segment of a content-addressed key, when the path is one.</summary>
    public static bool TryParseZone(string? pathOrUri, out string zone)
    {
        zone = string.Empty;
        if (string.IsNullOrWhiteSpace(pathOrUri)) return false;
        var match = KeyPattern().Match(pathOrUri.Replace('\\', '/'));
        if (!match.Success) return false;
        zone = match.Groups["zone"].Value;
        return true;
    }

    [GeneratedRegex(@"Evidence/tenants/\d+/(?<zone>[a-z-]+)/sha256/[0-9a-f]{2}/(?<sha>[0-9a-f]{64})(\.[A-Za-z0-9]{1,11})?(\?|$)")]
    private static partial Regex KeyPattern();
}

/// <summary>The disk folders the pre-ledger writers used. Names are load-bearing: they are the
/// prefixes recorded in production rows and swept by the tenant purge.</summary>
public static class LegacyDocumentFolders
{
    public const string EmailAttachments = "RFQ_Attachments";
    public const string ManualAttachments = "Manual_Attachments";
    public const string WatchedFolderAttachments = "Leads_Folder_Attachments";
    public const string RawEmails = "Raw_Emails";
}

/// <summary>What one legacy write produced, in the shape the row records it.</summary>
public sealed record StoredLegacyDocument(string FilePath, string ContentSha256, long ByteSize, bool InObjectStore);

/// <summary>
/// The ONE writer and reader for the four legacy document doors (email attachments, manual
/// uploads, watched-folder attachments, raw inbound mail). Where the bytes go is a single
/// deployment-wide switch — <see cref="LegacyDocumentStore.RouteToObjectStoreKey"/> — so the
/// estate can be on disk or in the object store but never split three ways by writer.
/// </summary>
public interface ILegacyDocumentStore
{
    bool RoutesToObjectStore { get; }

    /// <summary>
    /// Stores a lead attachment. Disk mode: <c>{root}/{legacyFolder}/{fileName}</c>, recorded as
    /// <c>Uploads/{legacyFolder}/{fileName}</c> exactly as the writers always did. Object mode:
    /// content-addressed under zone <c>legacy</c>, recorded as the object URI. The digest is
    /// returned in both modes.
    /// </summary>
    Task<StoredLegacyDocument> StoreAttachmentAsync(
        long businessUnitId, string legacyFolder, string fileName, ReadOnlyMemory<byte> content, CancellationToken ct = default);

    /// <summary>Stores the raw <c>.eml</c> compatibility copy. Disk mode: an absolute path under
    /// <c>Raw_Emails/</c>; object mode: zone <c>raw-mail</c>, the same key
    /// <c>EmailInquiryCaptureService</c> writes for the same bytes.</summary>
    Task<StoredLegacyDocument> StoreRawMailAsync(long businessUnitId, ReadOnlyMemory<byte> eml, CancellationToken ct = default);

    /// <summary>Whether the pointer can be opened. For an object URI this answers from the key
    /// (durable store, digest embedded); the verifying read is what proves the bytes.</summary>
    Task<bool> ExistsAsync(string? pathOrUri, CancellationToken ct = default);

    /// <summary>Opens the bytes behind a pointer of either shape. Object URIs are read through
    /// <see cref="IEvidenceObjectStorage.OpenVerifiedReadAsync"/> against
    /// <paramref name="expectedSha256"/> or, when the row has none, the digest in the key.</summary>
    Task<Stream> OpenAsync(string pathOrUri, string? expectedSha256 = null, CancellationToken ct = default);
}

public sealed class LegacyDocumentStore : ILegacyDocumentStore
{
    public const string RouteToObjectStoreKey = "EvidenceStorage:RouteLegacyWritersToObjectStore";

    /// <summary>Compatibility copies written by the pre-ledger doors. NOT inspected — they never
    /// were — so neither <c>cleared</c> (a lie) nor <c>quarantine</c> (the retention purge swaps
    /// it with <c>cleared</c> and deletes both). A zone of its own matches neither swap arm.</summary>
    public const string LegacyZone = "legacy";

    private readonly IFileStorage _files;
    private readonly IEvidenceObjectStorage _evidence;

    public LegacyDocumentStore(IFileStorage files, IEvidenceObjectStorage evidence, IConfiguration configuration)
        : this(files, evidence, configuration.GetValue(RouteToObjectStoreKey, false))
    {
    }

    public LegacyDocumentStore(IFileStorage files, IEvidenceObjectStorage evidence, bool routeToObjectStore)
    {
        _files = files;
        _evidence = evidence;
        RoutesToObjectStore = routeToObjectStore;
    }

    public bool RoutesToObjectStore { get; }

    public async Task<StoredLegacyDocument> StoreAttachmentAsync(
        long businessUnitId, string legacyFolder, string fileName, ReadOnlyMemory<byte> content, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(legacyFolder) || legacyFolder.IndexOfAny(['/', '\\']) >= 0)
            throw new ArgumentException("A single legacy folder name is required.", nameof(legacyFolder));
        if (string.IsNullOrWhiteSpace(fileName) || fileName.IndexOfAny(['/', '\\']) >= 0)
            throw new ArgumentException("A bare file name is required.", nameof(fileName));

        var sha = Digest(content.Span);
        if (RoutesToObjectStore)
        {
            var stored = await _evidence.WriteImmutableAsync(
                businessUnitId, LegacyZone, sha, Path.GetExtension(fileName), content, ct);
            return new StoredLegacyDocument(stored.StorageUri, sha, stored.ByteSize, true);
        }

        var physical = Path.Combine(_files.GetPath(legacyFolder), fileName);
        await WriteDiskAsync(physical, content, ct);
        return new StoredLegacyDocument(Path.Combine("Uploads", legacyFolder, fileName), sha, content.Length, false);
    }

    public async Task<StoredLegacyDocument> StoreRawMailAsync(long businessUnitId, ReadOnlyMemory<byte> eml, CancellationToken ct = default)
    {
        var sha = Digest(eml.Span);
        if (RoutesToObjectStore)
        {
            var stored = await _evidence.WriteImmutableAsync(businessUnitId, "raw-mail", sha, ".eml", eml, ct);
            return new StoredLegacyDocument(stored.StorageUri, sha, stored.ByteSize, true);
        }

        var physical = Path.Combine(_files.GetPath(LegacyDocumentFolders.RawEmails), $"{Guid.NewGuid()}.eml");
        await WriteDiskAsync(physical, eml, ct);
        return new StoredLegacyDocument(physical, sha, eml.Length, false);
    }

    public Task<bool> ExistsAsync(string? pathOrUri, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(pathOrUri)) return Task.FromResult(false);
        if (EvidenceObjectUris.IsObjectUri(pathOrUri))
            return Task.FromResult(EvidenceObjectUris.TryParseDigest(pathOrUri, out _));
        return Task.FromResult(File.Exists(ResolveDisk(pathOrUri)));
    }

    public async Task<Stream> OpenAsync(string pathOrUri, string? expectedSha256 = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(pathOrUri))
            throw new FileNotFoundException("The document has no stored location.");
        if (EvidenceObjectUris.IsObjectUri(pathOrUri))
        {
            var sha = expectedSha256;
            if (string.IsNullOrWhiteSpace(sha) && !EvidenceObjectUris.TryParseDigest(pathOrUri, out sha))
                throw new InvalidDataException("The stored object cannot be verified: no digest is recorded and none is embedded in its key.");
            return await _evidence.OpenVerifiedReadAsync(pathOrUri, sha, ct);
        }

        var path = ResolveDisk(pathOrUri);
        if (!File.Exists(path)) throw new FileNotFoundException("The stored file is not present.", path);
        return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
    }

    private string ResolveDisk(string pathOrUri)
        // Absolute legacy paths (raw mail was always recorded absolute) are opened as recorded —
        // the same behaviour the readers had — while relative ones go through the storage root's
        // containment. A relative path outside the root throws UnauthorizedAccessException there.
        => Path.IsPathRooted(pathOrUri) ? Path.GetFullPath(pathOrUri) : _files.ResolvePath(pathOrUri);

    private static async Task WriteDiskAsync(string physical, ReadOnlyMemory<byte> content, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(physical)!);
        try
        {
            await using var stream = new FileStream(physical, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);
            await stream.WriteAsync(content, ct);
            await stream.FlushAsync(ct);
        }
        catch
        {
            try { if (File.Exists(physical)) File.Delete(physical); } catch { /* best-effort cleanup of a partial write */ }
            throw;
        }
    }

    internal static string Digest(ReadOnlySpan<byte> bytes)
        => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
