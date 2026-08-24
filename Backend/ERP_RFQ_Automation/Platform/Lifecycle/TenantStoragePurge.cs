using ERP_RFQ_Automation.Infrastructure.Storage;

namespace ERP_RFQ_Automation.Platform.Lifecycle;

/// <summary>What happened to one stored object.</summary>
public enum TenantStoragePurgeOutcome
{
    /// <summary>The bytes were there and are not any more.</summary>
    Deleted,

    /// <summary>Nothing was there. Success on a re-run, and success for the production documents
    /// whose bytes were lost to ephemeral storage before the persistent disk existed.</summary>
    AlreadyAbsent,

    /// <summary>The store would not delete it. NOT success, and never counted as such.</summary>
    Refused
}

/// <summary>One object, and what the sweep managed to do about it.</summary>
public sealed record TenantStoragePurgeEntry(
    string Bucket,
    string Key,
    string Version,
    long ByteSize,
    TenantStoragePurgeOutcome Outcome,
    string? Refusal = null);

/// <summary>
/// What a tenant had stored, captured BEFORE the rows naming it were destroyed.
///
/// <para>The ordering is the whole design. Object storage has no rollback, so byte deletion cannot
/// join the destructive transaction — a delete inside it would be permanent even when the
/// transaction was not. And it cannot simply follow the transaction either, because by then the
/// rows that name half these objects are gone and there is no way to reconstruct the list. So the
/// inventory is taken first, committed first, and the deletion is a recorded follow-up step
/// against it.</para>
/// </summary>
public sealed record TenantStoragePurgeInventory(
    long BusinessUnitId,
    IReadOnlyList<TenantStoredObject> Objects,
    /// <summary>
    /// Everything on the local volume that is the tenant's and is not an evidence object: the
    /// watched-folder staging tree at <c>Tenants/{bu}/</c>, plus the paths recorded in the tenant's
    /// ROWS that live in the legacy flat folders (<c>Raw_Emails</c>, <c>RFQ_Attachments</c>,
    /// <c>Manual_Attachments</c>) written before storage was tenant-partitioned.
    ///
    /// <para>The two halves are gathered differently on purpose. <c>Tenants/{bu}/</c> is a prefix,
    /// so it is enumerated and does not depend on any row surviving. The flat folders are not
    /// partitioned by anything, so the rows are the ONLY map to them — which is why they are read
    /// while those rows still exist, and why a file there whose row was already gone is
    /// unattributable by any means this code has.</para>
    /// </summary>
    IReadOnlyList<string> LegacyPaths)
{
    /// <summary>
    /// True when this inventory was rebuilt from the tenant PREFIXES alone, because the rows that
    /// name the rest were already gone — a purge that committed under an earlier build, or an
    /// attempt that died between destroying the rows and recording what it was about to delete.
    ///
    /// <para>What that costs is exact and worth stating rather than glossing: the two prefix trees
    /// (<c>Evidence/tenants/{bu}/</c> and <c>Tenants/{bu}/</c>) are enumerable from the business
    /// unit id and are recovered in full. The legacy flat folders are not partitioned by anything,
    /// so a file there whose row is gone is unattributable by any means this code has, and this
    /// flag is how a report says so instead of implying the sweep was exhaustive.</para>
    ///
    /// <para>Refusing outright was the first version and it was wrong: it stranded a tenant whose
    /// rows were already destroyed, leaving every one of the 273 recoverable objects in place to
    /// protect a handful that were not recoverable either way.</para>
    /// </summary>
    public bool ReconstructedFromPrefixesOnly { get; init; }
}

/// <summary>The result of running one storage sweep.</summary>
/// <param name="Outstanding">
/// How many objects are still there. The ONLY value that permits a purge to report success is
/// zero; anything else is reported to the operator as an incomplete purge naming what survived.
/// </param>
public sealed record TenantStoragePurgeReport(
    long BusinessUnitId,
    IReadOnlyList<TenantStoragePurgeEntry> Entries,
    int Deleted,
    int AlreadyAbsent,
    int Outstanding,
    long BytesFreed)
{
    public bool IsComplete => Outstanding == 0;

    public IEnumerable<TenantStoragePurgeEntry> Refusals =>
        Entries.Where(e => e.Outcome == TenantStoragePurgeOutcome.Refused);
}

/// <summary>
/// Deletes a purged tenant's stored BYTES, and says per object what it managed.
///
/// <para><b>THE DEFECT.</b> <c>TenantPurgeExecutor</c> issues SQL and nothing else. A full tenant
/// purge therefore left 273 objects under <c>Evidence/tenants/{bu}/</c> and a 5 GB disk at
/// <c>/var/data/nexora/uploads/</c> completely intact — including the raw <c>.eml</c> of every
/// message the tenant ever received — while reporting success and telling the customer their data
/// was gone. Worse than leaving them: deleting the rows destroys the only index back to the bytes,
/// so what survived was unattributable as well as undeleted.</para>
///
/// <para><b>Idempotent by construction.</b> Every object is addressed by (bucket, key, version)
/// and an absent object is <see cref="TenantStoragePurgeOutcome.AlreadyAbsent"/> rather than an
/// error, so the step can be re-run any number of times — which it must be, because it is the one
/// part of an offboarding that can legitimately be half-done.</para>
///
/// <para><b>A failure is never swallowed.</b> A store that refuses, times out, or cannot be
/// reached produces <see cref="TenantStoragePurgeOutcome.Refused"/> with the reason attached and
/// increments <see cref="TenantStoragePurgeReport.Outstanding"/>. The caller reports an INCOMPLETE
/// purge. There is deliberately no path here that turns "we could not delete the bytes" into a
/// success — that is the same class of silence as the SQL sweep that counted only the tables it
/// had already visited.</para>
/// </summary>
public sealed class TenantStoragePurger(
    IEvidenceObjectStorage evidence,
    IFileStorage files,
    ILogger<TenantStoragePurger> logger)
{
    /// <summary>
    /// What the tenant has stored: everything under their prefix, plus the legacy flat paths their
    /// rows still point at.
    /// </summary>
    /// <param name="recordedPaths">
    /// Storage paths read from the tenant's rows by
    /// <see cref="TenantPurgeExecutor.CaptureStoragePathsAsync"/>, while those rows still exist.
    /// </param>
    public async Task<TenantStoragePurgeInventory> CaptureAsync(
        long businessUnitId, IReadOnlyCollection<string> recordedPaths, CancellationToken ct)
    {
        var objects = await evidence.ListTenantObjectsAsync(businessUnitId, ct);

        // Anything the prefix sweep already covers is dropped here rather than deleted twice: the
        // second attempt would report AlreadyAbsent, which is true and useless, and would inflate
        // the object count an operator reads.
        var covered = objects.Select(o => Normalize(o.Key)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var prefix = $"Evidence/tenants/{businessUnitId}/";

        // The SECOND tenant-prefixed tree, and it is easy to miss because it is not evidence.
        // FolderService stages watched-folder intake at Tenants/{bu}/Watched/{Shared|SEC|Aramco},
        // on the same Render disk. It is tenant-partitioned, so it is sweepable by prefix like the
        // evidence tree — and a purge that swept only Evidence/ would have left every document a
        // customer ever dropped into a watched folder sitting on the volume.
        var watched = EnumerateLocalPrefix($"Tenants/{businessUnitId}", ct);

        var legacy = recordedPaths
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(Normalize)
            .Where(p => !p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Concat(watched)
            .Where(p => !covered.Contains(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        logger.LogInformation(
            "Tenant storage inventory for business unit {BusinessUnitId}: {Objects} object(s) under "
            + "{Prefix}, {Watched} file(s) under Tenants/{BusinessUnitId}/, and {Legacy} path(s) in "
            + "total outside the evidence prefix.",
            businessUnitId, objects.Count, prefix, watched.Count, businessUnitId, legacy.Count);

        return new TenantStoragePurgeInventory(businessUnitId, objects, legacy);
    }

    /// <summary>
    /// Deletes everything in the inventory, recording one outcome per object. Never throws for a
    /// per-object failure — a refusal is data the caller has to report, not an exception that
    /// abandons the remaining objects half-swept.
    /// </summary>
    public async Task<TenantStoragePurgeReport> ExecuteAsync(
        TenantStoragePurgeInventory inventory, CancellationToken ct)
    {
        var entries = new List<TenantStoragePurgeEntry>();
        var bytesFreed = 0L;

        foreach (var stored in inventory.Objects)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var result = await evidence.TryDeletePurgedObjectAsync(
                    stored.Bucket, stored.Key, stored.Version, ct);
                if (result.Deleted) bytesFreed += result.BytesFreed;
                entries.Add(new TenantStoragePurgeEntry(
                    stored.Bucket, stored.Key, stored.Version, stored.ByteSize,
                    result.Deleted
                        ? TenantStoragePurgeOutcome.Deleted
                        : TenantStoragePurgeOutcome.AlreadyAbsent));
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogError(exception,
                    "Tenant purge could not delete stored object {Bucket}/{Key} for business unit "
                    + "{BusinessUnitId}.", stored.Bucket, stored.Key, inventory.BusinessUnitId);
                entries.Add(new TenantStoragePurgeEntry(
                    stored.Bucket, stored.Key, stored.Version, stored.ByteSize,
                    TenantStoragePurgeOutcome.Refused, Describe(exception)));
            }
        }

        foreach (var path in inventory.LegacyPaths)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                // IFileStorage.TryDeleteAsync applies the same containment every read applies —
                // root-prefix comparison, per-segment symlink rejection, refusal on the root
                // itself and on directories — so a recorded path cannot become a way to delete
                // something outside evidence storage. That matters here specifically: these paths
                // came out of a database column, and a column is the one place a crafted value
                // could have reached.
                var deleted = await files.TryDeleteAsync(path, ct);
                entries.Add(new TenantStoragePurgeEntry(
                    "local", path, string.Empty, 0,
                    deleted ? TenantStoragePurgeOutcome.Deleted : TenantStoragePurgeOutcome.AlreadyAbsent));
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogError(exception,
                    "Tenant purge could not delete recorded file {Path} for business unit "
                    + "{BusinessUnitId}.", path, inventory.BusinessUnitId);
                entries.Add(new TenantStoragePurgeEntry(
                    "local", path, string.Empty, 0,
                    TenantStoragePurgeOutcome.Refused, Describe(exception)));
            }
        }

        var report = new TenantStoragePurgeReport(
            inventory.BusinessUnitId,
            entries,
            entries.Count(e => e.Outcome == TenantStoragePurgeOutcome.Deleted),
            entries.Count(e => e.Outcome == TenantStoragePurgeOutcome.AlreadyAbsent),
            entries.Count(e => e.Outcome == TenantStoragePurgeOutcome.Refused),
            bytesFreed);

        if (report.IsComplete)
            logger.LogWarning(
                "TENANT STORAGE PURGE complete for business unit {BusinessUnitId}: {Deleted} "
                + "object(s) deleted, {Absent} already absent, {Bytes} byte(s) reclaimed.",
                inventory.BusinessUnitId, report.Deleted, report.AlreadyAbsent, report.BytesFreed);
        else
            logger.LogError(
                "TENANT STORAGE PURGE INCOMPLETE for business unit {BusinessUnitId}: {Outstanding} "
                + "object(s) could not be deleted and are still stored. The purge must be reported "
                + "as incomplete and the step re-run.",
                inventory.BusinessUnitId, report.Outstanding);

        return report;
    }

    /// <summary>Operator-safe. The provider's own account of the failure is already in the log
    /// above with the exception attached; this is what goes into a durable record and possibly a
    /// response body.</summary>
    private static string Describe(Exception exception) =>
        exception is EvidenceStorageUnavailableException
            ? exception.Message
            : $"{exception.GetType().Name}: {exception.Message}";

    /// <summary>
    /// Files under one relative prefix of the local volume, as storage-relative paths that
    /// <see cref="IFileStorage.TryDeleteAsync"/> will take back.
    ///
    /// <para>Reads through <see cref="IFileStorage.GetPath"/> and re-relativises against
    /// <see cref="IFileStorage.RootPath"/> rather than composing a path by hand, so the containment
    /// rules that govern every other access to this volume govern this one too — including on the
    /// delete, which goes back through <c>TryDeleteAsync</c> and its symlink and root checks.</para>
    ///
    /// <para>A missing directory is an empty list, not an error: a tenant that never used a watched
    /// folder has none, and so does a re-run of a sweep that already emptied it.</para>
    /// </summary>
    private List<string> EnumerateLocalPrefix(string relativePrefix, CancellationToken ct)
    {
        var root = files.GetPath(relativePrefix.Split('/', StringSplitOptions.RemoveEmptyEntries));
        if (!Directory.Exists(root)) return [];

        var found = new List<string>();
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            found.Add(Normalize(Path.GetRelativePath(files.RootPath, file)));
        }

        return found;
    }

    private static string Normalize(string path) => path.Replace('\\', '/').TrimStart('/');
}
