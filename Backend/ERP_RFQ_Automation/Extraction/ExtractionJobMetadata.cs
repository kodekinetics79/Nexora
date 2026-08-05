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

    /// <summary>Authenticated uploader or governed source actor recorded for audit display.</summary>
    public string? UploadedBy { get; set; }

    /// <summary>Legacy mailbox metadata. Customer identity must use <see cref="FromEmail"/>.</summary>
    public string? ClientEmail { get; set; }

    /// <summary>Overrides Lead.LeadSource (e.g. "Email", "SEC Leads", "Aramco Leads").
    /// Null -&gt; the persister uses the job's SourceType name, as before.</summary>
    public string? LeadSource { get; set; }

    /// <summary>Overrides Lead.EmailSource (file-type label parity, e.g. "PDF, Excel").</summary>
    public string? EmailSource { get; set; }

    /// <summary>
    /// Explicit commercial intake intent set only by a governed authenticated door.
    /// Non-customer documents are completed by the extraction worker without creating a Lead.
    /// </summary>
    public string? CommercialDocumentTypeHint { get; set; }

    /// <summary>
    /// ING-06: "filename (reason)" entries for sibling attachments the intake door had to
    /// skip (unsupported type, oversize, empty, unnamed). Set on the email BODY job so the
    /// durable provenance record (job sidecar + SourceDocumentOccurrence.SourceMetadataJson)
    /// shows a dropped attachment was never silent. Null when nothing was skipped.
    /// </summary>
    public IReadOnlyList<string>? SkippedAttachments { get; set; }

    /// <summary>
    /// Shape of the submitted text, set by the intake door. <c>"prose"</c> marks a
    /// conversational email BODY — free text with no table and no line-item rows — which the
    /// extraction worker routes to the conversational extractor instead of the structured
    /// RFQ prompt. Null (the default) keeps the existing structured/unstructured routing
    /// exactly as it was. Attachment jobs never carry it: an attachment is a document.
    /// </summary>
    public string? BodyShape { get; set; }

    /// <summary>The intake gate's decision for the source message
    /// (<c>Inquiry</c> | <c>CommercialNonInquiry</c> | <c>Noise</c> | <c>Uncertain</c>).
    /// Diagnostic provenance: a noise message never produces a job at all.</summary>
    public string? TriageOutcome { get; set; }

    /// <summary>Stable snake_case reason codes behind <see cref="TriageOutcome"/>.</summary>
    public IReadOnlyList<string>? TriageReasonCodes { get; set; }

    /// <summary>True when the source message was a reply/forward (In-Reply-To or References).
    /// The conversational path flags such a lead for review rather than minting a silent
    /// duplicate of an inquiry that is already in the system.</summary>
    public bool? ThreadContinuation { get; set; }

    public static bool IsNonLeadCommercialType(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        !value.Equals("CUSTOMER_RFQ", StringComparison.OrdinalIgnoreCase) &&
        !value.Equals("CUSTOMER_RFQ_REVISION", StringComparison.OrdinalIgnoreCase);

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
