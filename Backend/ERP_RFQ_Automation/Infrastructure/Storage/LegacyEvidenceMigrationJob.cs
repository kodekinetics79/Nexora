using ERP_RFQ_Automation.Extraction;
using ERP_RFQ_Automation.Models;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Infrastructure.Storage;

public enum LegacyMigrationOutcome
{
    /// <summary>Bytes copied to the object store and the row re-pointed at the object URI.</summary>
    Migrated,
    /// <summary>Row already pointed at an object; the object was re-read and its digest re-checked.</summary>
    Verified,
    /// <summary>The row's path resolves inside the storage root but no file is there. Left untouched.</summary>
    SourceMissing,
    /// <summary>The file's bytes do not hash to the digest the row (or its evidence record) claims. Left untouched.</summary>
    HashMismatch,
    /// <summary>Path outside the storage root, unparseable key, or a store error. Left untouched.</summary>
    Refused
}

public sealed record LegacyMigrationEntry(string Table, long Id, LegacyMigrationOutcome Outcome, string Detail);

public sealed record LegacyMigrationReport(IReadOnlyList<LegacyMigrationEntry> Entries)
{
    public int Count(LegacyMigrationOutcome outcome) => Entries.Count(e => e.Outcome == outcome);
    public int Migrated => Count(LegacyMigrationOutcome.Migrated);
    public int Verified => Count(LegacyMigrationOutcome.Verified);
    public int SourceMissing => Count(LegacyMigrationOutcome.SourceMissing);
    public int HashMismatch => Count(LegacyMigrationOutcome.HashMismatch);
    public int Refused => Count(LegacyMigrationOutcome.Refused);
    /// <summary>True when nothing is left on disk that could still be moved.</summary>
    public bool Drained => Migrated == 0 && HashMismatch == 0 && Refused == 0;
}

/// <summary>
/// One-off, idempotent, re-runnable copy of every legacy disk document into the evidence object
/// store (docs/design/evidence-object-store-cutover.md §3). Safe to run while the app serves:
/// pages of <see cref="PageSize"/>, one row per <c>SaveChanges</c>, no locks; a row that changed
/// under it is simply skipped this pass.
///
/// <para>Three row kinds, three rules. <c>Attachments</c> (lead attachments from the four legacy
/// doors) go to zone <c>legacy</c> unless the path already IS an evidence key, in which case the
/// zone in the key is kept. <c>EmailIngests.RawEmailPath</c> goes to <c>raw-mail</c>, the same key
/// the capture service writes for the same bytes. <c>ExtractionJobs.StoragePath</c> under the root
/// must hash to <c>ContentHash</c> and keeps its key; only <c>StoragePath</c> is re-pointed, because
/// <c>source_documents.object_*</c> are frozen by trigger once Cleared.</para>
///
/// <para>Never destructive: bytes are copied, never removed. A row whose file is gone is reported
/// and left exactly as it was — 24 production rows point at an earlier host's disk and this job
/// must not pretend otherwise.</para>
/// </summary>
public sealed class LegacyEvidenceMigrationJob
{
    public const string EnabledKey = "EvidenceStorage:LegacyMigration:Enabled";
    internal const int PageSize = 100;
    private const string TombstonePrefix = "purged:";

    private readonly ErpRfqAutomationContext _db;
    private readonly IFileStorage _files;
    private readonly IEvidenceObjectStorage _evidence;
    private readonly ILogger<LegacyEvidenceMigrationJob> _logger;

    public LegacyEvidenceMigrationJob(
        ErpRfqAutomationContext db, IFileStorage files, IEvidenceObjectStorage evidence,
        ILogger<LegacyEvidenceMigrationJob> logger)
    {
        _db = db;
        _files = files;
        _evidence = evidence;
        _logger = logger;
    }

    public async Task<LegacyMigrationReport> RunAsync(CancellationToken ct = default)
    {
        if (!_evidence.IsDurable)
            throw new InvalidOperationException(
                "Refusing to migrate legacy documents into a non-durable evidence store: the target must be S3-compatible.");

        var entries = new List<LegacyMigrationEntry>();
        await MigrateAttachmentsAsync(entries, ct);
        await MigrateRawMailAsync(entries, ct);
        await MigrateExtractionJobsAsync(entries, ct);
        var report = new LegacyMigrationReport(entries);
        _logger.LogWarning(
            "LEGACY EVIDENCE MIGRATION: migrated={Migrated} verified={Verified} sourceMissing={Missing} "
            + "hashMismatch={Mismatch} refused={Refused} drained={Drained}.",
            report.Migrated, report.Verified, report.SourceMissing, report.HashMismatch, report.Refused, report.Drained);
        foreach (var entry in entries.Where(e => e.Outcome is not (LegacyMigrationOutcome.Migrated or LegacyMigrationOutcome.Verified)).Take(50))
            _logger.LogWarning("  {Table} #{Id}: {Outcome} — {Detail}", entry.Table, entry.Id, entry.Outcome, entry.Detail);
        return report;
    }

    // ------------------------------------------------------------------ Attachments

    private async Task MigrateAttachmentsAsync(List<LegacyMigrationEntry> entries, CancellationToken ct)
    {
        long lastId = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            _db.ChangeTracker.Clear();
            var page = await _db.Attachments
                .Where(a => a.Id > lastId && a.ParentType == "Lead" && !a.FilePath.StartsWith(TombstonePrefix))
                .OrderBy(a => a.Id).Take(PageSize).ToListAsync(ct);
            if (page.Count == 0) break;
            lastId = page[^1].Id;

            foreach (var row in page)
            {
                if (EvidenceObjectUris.IsObjectUri(row.FilePath))
                {
                    entries.Add(await VerifyAsync("Attachments", row.Id, row.FilePath, row.ContentSha256, ct));
                    continue;
                }

                var businessUnitId = await _db.Leads.IgnoreQueryFilters()
                    .Where(l => l.Id == row.ParentId).Select(l => (long?)l.BusinessUnitId).SingleOrDefaultAsync(ct);
                if (businessUnitId is null)
                {
                    entries.Add(new("Attachments", row.Id, LegacyMigrationOutcome.Refused, "parent lead not found"));
                    continue;
                }

                var read = ReadDisk(row.FilePath);
                if (read.Outcome is not null)
                {
                    entries.Add(new("Attachments", row.Id, read.Outcome.Value, read.Detail));
                    continue;
                }
                var sha = LegacyDocumentStore.Digest(read.Bytes);
                if (!string.IsNullOrWhiteSpace(row.ContentSha256)
                    && !string.Equals(row.ContentSha256, sha, StringComparison.OrdinalIgnoreCase))
                {
                    entries.Add(new("Attachments", row.Id, LegacyMigrationOutcome.HashMismatch,
                        $"row records {row.ContentSha256}, file hashes to {sha}"));
                    continue;
                }
                // A path that already is an evidence key keeps its zone (the bytes are the source
                // document's, and the retention purge must keep seeing one key for them); a legacy
                // folder copy goes to the legacy zone.
                var zone = EvidenceObjectUris.TryParseZone(row.FilePath, out var parsedZone)
                    ? parsedZone
                    : LegacyDocumentStore.LegacyZone;

                try
                {
                    var stored = await _evidence.WriteImmutableAsync(
                        businessUnitId.Value, zone, sha, Path.GetExtension(row.FileName), read.Bytes, ct);
                    row.FilePath = stored.StorageUri;
                    row.ContentSha256 = sha;
                    row.FileSize ??= read.Bytes.Length;
                    await _db.SaveChangesAsync(ct);
                    entries.Add(new("Attachments", row.Id, LegacyMigrationOutcome.Migrated, stored.StorageUri));
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    entries.Add(new("Attachments", row.Id, LegacyMigrationOutcome.Refused, Describe(exception)));
                }
            }
        }
    }

    // ------------------------------------------------------------------ EmailIngests.RawEmailPath

    private async Task MigrateRawMailAsync(List<LegacyMigrationEntry> entries, CancellationToken ct)
    {
        long lastId = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            _db.ChangeTracker.Clear();
            // BytesPurgedOn: the tombstone trigger (20260824140000) refuses a RawEmailPath on a
            // purged row, and a purged message has no bytes to copy anyway.
            var page = await _db.EmailIngests
                .Where(e => e.Id > lastId && e.RawEmailPath != null && e.BytesPurgedOn == null)
                .OrderBy(e => e.Id).Take(PageSize)
                .Select(e => new { e.Id, e.RawEmailPath, e.EmailConfiguration.BusinessUnitId })
                .ToListAsync(ct);
            if (page.Count == 0) break;
            lastId = page[^1].Id;

            foreach (var row in page)
            {
                if (EvidenceObjectUris.IsObjectUri(row.RawEmailPath))
                {
                    entries.Add(await VerifyAsync("EmailIngests", row.Id, row.RawEmailPath!, null, ct));
                    continue;
                }
                var read = ReadDisk(row.RawEmailPath!);
                if (read.Outcome is not null)
                {
                    entries.Add(new("EmailIngests", row.Id, read.Outcome.Value, read.Detail));
                    continue;
                }
                var sha = LegacyDocumentStore.Digest(read.Bytes);
                try
                {
                    var stored = await _evidence.WriteImmutableAsync(row.BusinessUnitId, "raw-mail", sha, ".eml", read.Bytes, ct);
                    var updated = await _db.EmailIngests
                        .Where(e => e.Id == row.Id && e.RawEmailPath == row.RawEmailPath && e.BytesPurgedOn == null)
                        .ExecuteUpdateAsync(s => s.SetProperty(e => e.RawEmailPath, stored.StorageUri), ct);
                    entries.Add(updated == 1
                        ? new("EmailIngests", row.Id, LegacyMigrationOutcome.Migrated, stored.StorageUri)
                        : new("EmailIngests", row.Id, LegacyMigrationOutcome.Refused, "row changed while migrating; next run"));
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    entries.Add(new("EmailIngests", row.Id, LegacyMigrationOutcome.Refused, Describe(exception)));
                }
            }
        }
    }

    // ------------------------------------------------------------------ ExtractionJobs.StoragePath

    private async Task MigrateExtractionJobsAsync(List<LegacyMigrationEntry> entries, CancellationToken ct)
    {
        long lastId = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            _db.ChangeTracker.Clear();
            var page = await _db.Set<ExtractionJob>().IgnoreQueryFilters()
                .Where(j => j.Id > lastId && !j.StoragePath.Contains("://"))
                .OrderBy(j => j.Id).Take(PageSize).ToListAsync(ct);
            if (page.Count == 0) break;
            lastId = page[^1].Id;

            foreach (var job in page)
            {
                var read = ReadDisk(job.StoragePath);
                if (read.Outcome is not null)
                {
                    entries.Add(new("ExtractionJobs", job.Id, read.Outcome.Value, read.Detail));
                    continue;
                }
                var sha = LegacyDocumentStore.Digest(read.Bytes);
                if (!string.Equals(sha, job.ContentHash, StringComparison.OrdinalIgnoreCase))
                {
                    entries.Add(new("ExtractionJobs", job.Id, LegacyMigrationOutcome.HashMismatch,
                        $"job records {job.ContentHash}, file hashes to {sha}"));
                    continue;
                }
                // The key on disk is the key in the store: readers address it by URI + hash and
                // source_documents keeps naming the same object.
                var zone = EvidenceObjectUris.TryParseZone(job.StoragePath, out var parsedZone)
                    ? parsedZone
                    : LegacyDocumentStore.LegacyZone;
                try
                {
                    var stored = await _evidence.WriteImmutableAsync(
                        job.BusinessUnitId, zone, sha, Path.GetExtension(job.StoragePath), read.Bytes, ct);
                    job.StoragePath = stored.StorageUri;
                    await _db.SaveChangesAsync(ct);
                    entries.Add(new("ExtractionJobs", job.Id, LegacyMigrationOutcome.Migrated, stored.StorageUri));
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    entries.Add(new("ExtractionJobs", job.Id, LegacyMigrationOutcome.Refused, Describe(exception)));
                }
            }
        }
    }

    // ------------------------------------------------------------------ helpers

    private async Task<LegacyMigrationEntry> VerifyAsync(string table, long id, string uri, string? recordedSha, CancellationToken ct)
    {
        var sha = recordedSha;
        if (string.IsNullOrWhiteSpace(sha) && !EvidenceObjectUris.TryParseDigest(uri, out sha))
            return new(table, id, LegacyMigrationOutcome.Refused, "object URI carries no digest and the row records none");
        try
        {
            await using var stream = await _evidence.OpenVerifiedReadAsync(uri, sha, ct);
            await stream.CopyToAsync(Stream.Null, ct);
            return new(table, id, LegacyMigrationOutcome.Verified, uri);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new(table, id, LegacyMigrationOutcome.Refused, Describe(exception));
        }
    }

    private (byte[] Bytes, LegacyMigrationOutcome? Outcome, string Detail) ReadDisk(string recordedPath)
    {
        string path;
        try
        {
            // Relative paths (Uploads\RFQ_Attachments\..., uploads/Evidence/...) resolve under the
            // root with containment; absolute ones (/var/data/..., /app/Uploads/..., D:\Sites\...)
            // must ALSO be under the root or they are refused rather than read.
            path = _files.ResolvePath(recordedPath);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return ([], LegacyMigrationOutcome.Refused, $"outside the storage root: {recordedPath}");
        }
        if (!File.Exists(path))
            return ([], LegacyMigrationOutcome.SourceMissing, path);
        try
        {
            return (File.ReadAllBytes(path), null, path);
        }
        catch (IOException exception)
        {
            return ([], LegacyMigrationOutcome.Refused, Describe(exception));
        }
    }

    private static string Describe(Exception exception) => $"{exception.GetType().Name}: {exception.Message}";
}

/// <summary>
/// Runs <see cref="LegacyEvidenceMigrationJob"/> once per boot when
/// <see cref="LegacyEvidenceMigrationJob.EnabledKey"/> is true. Off by default; an operator turns
/// it on, reads the report in the log, repeats until <c>drained=True</c>, turns it off. Idempotent,
/// so a restart mid-run costs nothing but time.
/// </summary>
public sealed class LegacyEvidenceMigrationHostedService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<LegacyEvidenceMigrationHostedService> _logger;

    public LegacyEvidenceMigrationHostedService(
        IServiceScopeFactory scopeFactory, IConfiguration configuration,
        ILogger<LegacyEvidenceMigrationHostedService> logger)
    {
        _scopeFactory = scopeFactory;
        _configuration = configuration;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_configuration.GetValue(LegacyEvidenceMigrationJob.EnabledKey, false))
            return;
        // Let the host finish starting (migrations, probes) before touching the store.
        try { await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken); }
        catch (OperationCanceledException) { return; }
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var job = scope.ServiceProvider.GetRequiredService<LegacyEvidenceMigrationJob>();
            await job.RunAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Legacy evidence migration failed; it is safe to re-run.");
        }
    }
}
