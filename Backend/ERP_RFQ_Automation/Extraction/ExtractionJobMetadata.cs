using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ERP_RFQ_Automation.Extraction;

/// <summary>
/// Optional per-job ingest metadata, persisted as a small JSON sidecar NEXT TO the
/// content-addressed source file (path: <c>&lt;StoragePath&gt;.bu&lt;BusinessUnitId&gt;.ingest.json</c>).
///
/// WHY a sidecar and not new ExtractionJobs columns: the job table already exists in
/// production and this work item is under a strict no-new-migrations constraint, so the
/// least-invasive way to carry "who sent this" (real EmailIngest id, from-address,
/// subject) from the ingest door to the persister is a file that lives and dies with the
/// stored document. The BusinessUnitId is embedded in the sidecar file name because the
/// content-addressed FILE is shared across tenants (same bytes, same hash, same path)
/// while jobs — and their ingest provenance — are per-tenant.
///
/// All reads/writes are best-effort: a missing or corrupt sidecar never fails a job; the
/// persister simply falls back to the synthetic-ingest behavior that existed before.
/// </summary>
public sealed class ExtractionJobMetadata
{
    /// <summary>
    /// Stable identity of this receipt within its source system, such as an email
    /// attachment id, MIME ordinal, or folder event id. It identifies an occurrence,
    /// not the immutable source bytes; content/job deduplication remains hash-based.
    /// </summary>
    public string? SourceOccurrenceId { get; set; }

    /// <summary>Stable source-level grouping hint, such as an email message id.</summary>
    public string? LogicalGroupKey { get; set; }

    /// <summary>Id of a PRE-CREATED EmailIngest row the produced lead(s) must link to.
    /// Null for doors that have no real ingest (manual upload, folder).</summary>
    public long? EmailIngestId { get; set; }

    /// <summary>Original sender ("From") for email-sourced documents.</summary>
    public string? FromEmail { get; set; }

    /// <summary>Original email subject for email-sourced documents.</summary>
    public string? Subject { get; set; }

    public DateTimeOffset? SourceReceivedAtUtc { get; set; }

    /// <summary>Legacy mailbox metadata. Customer identity must use <see cref="FromEmail"/>.</summary>
    public string? ClientEmail { get; set; }

    /// <summary>Overrides Lead.LeadSource (e.g. "Email", "SEC Leads", "Aramco Leads").
    /// Null -&gt; the persister uses the job's SourceType name, as before.</summary>
    public string? LeadSource { get; set; }

    /// <summary>Overrides Lead.EmailSource (file-type label parity, e.g. "PDF, Excel").</summary>
    public string? EmailSource { get; set; }

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = false };

    public static string SidecarPath(string storagePath, long businessUnitId)
        => $"{storagePath}.bu{businessUnitId}.ingest.json";

    /// <summary>Best-effort write; returns false (and swallows the error) on failure.</summary>
    public async Task<bool> SaveAsync(string storagePath, long businessUnitId, CancellationToken ct = default)
    {
        try
        {
            var path = SidecarPath(storagePath, businessUnitId);
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(this, JsonOpts), ct);
            return true;
        }
        catch
        {
            return false; // provenance is nice-to-have; the job itself must not fail
        }
    }

    /// <summary>Best-effort read; null when no sidecar exists or it cannot be parsed.</summary>
    public static ExtractionJobMetadata? TryLoad(ExtractionJob job)
    {
        try
        {
            var path = SidecarPath(job.StoragePath, job.BusinessUnitId);
            if (!File.Exists(path))
                return null;
            return JsonSerializer.Deserialize<ExtractionJobMetadata>(File.ReadAllText(path), JsonOpts);
        }
        catch
        {
            return null;
        }
    }
}
