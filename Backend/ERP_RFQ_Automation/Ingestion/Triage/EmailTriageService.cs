using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ERP_RFQ_Automation.DocumentIntelligence.Persistence;
using ERP_RFQ_Automation.Extraction;
using ERP_RFQ_Automation.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MimeKit;

namespace ERP_RFQ_Automation.Ingestion.Triage;

/// <param name="Outcome">The gate's decision, or "Legacy" for mail ingested before the gate existed.</param>
/// <param name="ReasonCodes">Stable snake_case codes; the UI renders them as chips.</param>
/// <param name="LinkedBatchId">The extraction batch this message produced, when it produced one.</param>
/// <param name="AttachmentNames">Null when unknown — never an empty array, which would claim
/// "this message provably had no attachments" for a message nobody looked inside.</param>
/// <param name="BodySubmitted">True when the body text itself was sent for extraction.</param>
/// <param name="LeadId">The lead this message produced, when it produced one.</param>
/// <param name="SkippedAttachments">ING-06: "filename (reason)" for every attachment that was
/// NOT handed to extraction. Empty when nothing was skipped. A durable record nobody can see is
/// only half a fix, and this screen is the one place a human goes to find lost mail.</param>
/// <param name="AssemblyState">
/// The <c>EmailInquiryAssemblyStatus</c> name, or null for mail ingested before the
/// inquiry-assembly cutover. Null means "this message was never captured as an assembly", NOT
/// "this message has no parts" — two different facts the screen must not conflate.
/// </param>
/// <param name="AssemblyReason">Operator-safe sentence for that state, where one exists.</param>
/// <param name="ExpectedComponentCount">Parts decided ONCE at capture. The barrier counts against
/// it, so a part that never reports is a visible hole rather than an early finish.</param>
/// <param name="CompletedComponentCount">Parts that have reached a terminal state.</param>
/// <param name="AssembledLeadId">The ONE lead this message became, once it did. Written in the
/// same transaction as the Assembled status, so it is authoritative over any per-ingest lookup.</param>
/// <param name="RawEvidenceStored">The original message is in durable evidence storage. A
/// BOOLEAN, deliberately: this record is serialized to a browser, and the location of the
/// evidence is useless to the reader and a map of the storage layout to everyone else.</param>
/// <param name="RawEvidenceVerifiable">A SHA-256 was recorded at capture, so a read-back is
/// checked against it rather than trusted.</param>
/// <param name="IngestedAtUtc">When the message was durably captured — the ingestion timestamp
/// that matters, as distinct from when the mailbox happened to be polled.</param>
/// <param name="LastUpdatedAtUtc">
/// When the pipeline last moved this message, the recovery sweep included. With the state, this
/// is how an operator tells "stuck" from "busy".
///
/// <para>It is deliberately NOT called a recovery timestamp. Nothing durable records that a
/// message was recovered rather than assembled inline — the sweep and the worker's barrier both
/// write this same column — and naming it after one of them would state something no row
/// supports.</para>
/// </param>
/// <param name="SenderSentAtUtc">When the sender's server stamped it, from the message.</param>
/// <param name="Components">
/// Every physical part of the message. NULL on the list and populated on the detail: null means
/// "not asked for", which the screen must be able to tell apart from "asked, and this message
/// genuinely has no parts".
/// </param>
public sealed record EmailTriageRow(
    long Id,
    DateTime ReceivedOn,
    string? From,
    string? Subject,
    string Outcome,
    IReadOnlyList<string> ReasonCodes,
    bool HasAttachments,
    Guid? LinkedBatchId,
    string? ParseStatus,
    DateTime? TriageDecidedOn,
    int? AttachmentCount,
    IReadOnlyList<string>? AttachmentNames,
    bool BodySubmitted,
    long? LeadId,
    IReadOnlyList<string> SkippedAttachments,
    // Flat rather than nested under an "assembly" object, and that is a decision about the
    // consumer: the Email Intake screen reads these names off the row and degrades an unknown
    // name to "not reported", so a nested shape would render the whole panel blank while every
    // value was present and correct on the wire.
    string? AssemblyState = null,
    string? AssemblyReason = null,
    int? ExpectedComponentCount = null,
    int? CompletedComponentCount = null,
    long? AssembledLeadId = null,
    bool? RawEvidenceStored = null,
    bool? RawEvidenceVerifiable = null,
    DateTimeOffset? IngestedAtUtc = null,
    DateTimeOffset? LastUpdatedAtUtc = null,
    DateTimeOffset? SenderSentAtUtc = null,
    DateTime? ParsedAt = null,
    IReadOnlyList<EmailIntakeComponentRow>? Components = null);

/// <summary>
/// One physical part of the message: what it was, what became of it, and why.
///
/// <para>Same rule as the row — <see cref="SizeBytes"/>, <see cref="ContentType"/> and "we hold a
/// digest for it" are safe and useful; the evidence URI is neither and is never projected.</para>
/// </summary>
/// <param name="Kind">Body | Attachment | EmbeddedMessage.</param>
/// <param name="State">The <c>EmailInquiryComponentStatus</c> name.</param>
/// <param name="ReasonCode">Stable snake_case code for a skip, refusal or hold.</param>
/// <param name="Reason">The operator-safe sentence behind that code — why this part failed, or
/// why it is waiting for a person.</param>
/// <param name="HasContentDigest">A SHA-256 of these exact bytes was recorded.</param>
/// <param name="EvidenceStored">The part's bytes are in durable evidence storage.</param>
public sealed record EmailIntakeComponentRow(
    long Id,
    int Ordinal,
    string Kind,
    string? FileName,
    string State,
    string? ReasonCode,
    string? Reason,
    string? ContentType,
    long? SizeBytes,
    bool HasContentDigest,
    bool EvidenceStored,
    long? ExtractionJobId);

public sealed record EmailTriagePage(
    int PageNumber, int PageSize, int TotalCount, IReadOnlyList<EmailTriageRow> Items);

/// <param name="Enqueued">Jobs created by the replay. Zero means nothing extractable was found.</param>
/// <param name="Status">The ingest's resulting ParseStatus.</param>
public sealed record EmailTriageReprocessResult(
    long Id, Guid BatchId, int Enqueued, string Outcome, string Status);

public interface IEmailTriageService
{
    Task<EmailTriagePage> ListAsync(
        long businessUnitId, string? outcome, int page, int pageSize, CancellationToken ct = default);

    /// <summary>
    /// One message, with every part it was made of.
    ///
    /// <para>The SAME row the list renders, with <see cref="EmailTriageRow.Components"/>
    /// populated — not a parallel detail shape, which is how a list and a detail screen come to
    /// disagree about what happened to a message. The per-part detail is deliberately not on the
    /// list: a page of 25 messages would carry a few hundred component rows nobody reads.</para>
    /// </summary>
    Task<EmailTriageRow> GetAsync(
        long businessUnitId, long id, CancellationToken ct = default);

    /// <summary>
    /// Replays a stored message through ingestion as an INQUIRY-eligible message, whatever the
    /// gate originally decided. This is the guarantee that a rejected email is never
    /// unreachable: the raw .eml is retained for every message, so any triage decision can be
    /// overturned by a human without asking the customer to resend.
    /// </summary>
    Task<EmailTriageReprocessResult> ReprocessAsync(
        long businessUnitId, long id, string actor, string reason, string idempotencyKey,
        CancellationToken ct = default);
}

public sealed class EmailTriageService : IEmailTriageService
{
    private readonly ErpRfqAutomationContext _context;
    private readonly ERP_RFQ_Automation.Ingestion.Assembly.IEmailInquiryIntakeService _intake;
    private readonly ERP_RFQ_Automation.Ingestion.Assembly.IRawEmailEvidenceReader _rawEmail;
    private readonly ILogger<EmailTriageService> _log;

    // No file-size guard is needed any more, and the constant that used to be here is gone with
    // the code that opened the stored .eml. Nothing on the read path touches the filesystem: the
    // list and the detail read persisted rows only, so a 40 MB message costs the same as a
    // one-line one.

    public EmailTriageService(
        ErpRfqAutomationContext context,
        ERP_RFQ_Automation.Ingestion.Assembly.IEmailInquiryIntakeService intake,
        ERP_RFQ_Automation.Ingestion.Assembly.IRawEmailEvidenceReader rawEmail,
        ILogger<EmailTriageService> log)
    {
        _context = context;
        _intake = intake;
        _rawEmail = rawEmail;
        _log = log;
    }

    public async Task<EmailTriagePage> ListAsync(
        long businessUnitId, string? outcome, int page, int pageSize, CancellationToken ct = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);

        var query = _context.EmailIngests.AsNoTracking()
            .Where(e => e.EmailConfiguration.BusinessUnitId == businessUnitId);

        if (!string.IsNullOrWhiteSpace(outcome))
        {
            var wanted = outcome.Trim();
            query = wanted.Equals("Legacy", StringComparison.OrdinalIgnoreCase)
                ? query.Where(e => e.TriageOutcome == null)
                : query.Where(e => e.TriageOutcome == wanted);
        }

        var total = await query.CountAsync(ct);
        var rows = await Project(query
                .OrderByDescending(e => e.CreatedOn).ThenByDescending(e => e.Id)
                .Skip((page - 1) * pageSize).Take(pageSize))
            .ToListAsync(ct);

        return new EmailTriagePage(page, pageSize, total,
            await BuildRowsAsync(businessUnitId, rows, ct));
    }

    public async Task<EmailTriageRow> GetAsync(
        long businessUnitId, long id, CancellationToken ct = default)
    {
        // The SAME tenant predicate the list uses, stated on the read rather than inherited: a
        // detail endpoint that finds a row the list would never have shown is a cross-tenant
        // read with a different name.
        var ingest = await Project(_context.EmailIngests.AsNoTracking()
                .Where(e => e.Id == id && e.EmailConfiguration.BusinessUnitId == businessUnitId))
            .FirstOrDefaultAsync(ct)
            ?? throw new KeyNotFoundException($"Email ingest {id} was not found for this tenant.");

        var row = (await BuildRowsAsync(businessUnitId, new[] { ingest }, ct))[0];

        // The assembly id is not on the row — it is an internal identifier with nothing to say to
        // a reader — so the parts are read through the ingest, which is the tenant-scoped key the
        // caller already asked for.
        var components = await _context.EmailInquiryComponents.AsNoTracking()
            .Where(c => c.BusinessUnitId == businessUnitId
                        && c.Assembly.EmailIngestId == id
                        && c.Assembly.BusinessUnitId == businessUnitId)
            .OrderBy(c => c.Ordinal)
            .Select(c => new EmailIntakeComponentRow(
                c.Id,
                c.Ordinal,
                c.Kind.ToString(),
                c.FileName,
                c.Status.ToString(),
                c.ReasonCode,
                c.ReasonDetail,
                c.MimeType,
                c.ByteSize,
                c.ContentHash != null,
                c.EvidenceUri != null,
                c.ExtractionJobId))
            .ToListAsync(ct);

        // An empty array, not null: this caller DID ask, so "no parts" is now a verified answer
        // rather than a question nobody put.
        return row with { Components = components };
    }

    /// <summary>The ingest columns both surfaces read. One projection, so they cannot drift.</summary>
    private static IQueryable<IngestProjection> Project(IQueryable<EmailIngest> query)
        => query.Select(e => new IngestProjection(
            e.Id, e.CreatedOn, e.FromEmail, e.EmailSubject, e.TriageOutcome, e.TriageReasonJson,
            e.TriageDecidedOn, e.ParseStatus, e.ParsedAt, e.MessageId, e.SkippedAttachmentsJson));

    private sealed record IngestProjection(
        long Id, DateTime CreatedOn, string? FromEmail, string? EmailSubject, string? TriageOutcome,
        string? TriageReasonJson, DateTime? TriageDecidedOn, string? ParseStatus, DateTime? ParsedAt,
        string MessageId, string? SkippedAttachmentsJson);

    private async Task<IReadOnlyList<EmailTriageRow>> BuildRowsAsync(
        long businessUnitId, IReadOnlyList<IngestProjection> rows, CancellationToken ct)
    {
        if (rows.Count == 0) return Array.Empty<EmailTriageRow>();

        // One round trip for every message on the page: the extraction occurrences share the
        // message's logical group key, and each carries its job (hence the batch).
        var groupKeys = rows.Select(r => $"email:{r.MessageId}").ToList();
        var occurrences = groupKeys.Count == 0
            ? new List<(string Key, long? JobId)>()
            : (await _context.Set<SourceDocumentOccurrence>().AsNoTracking()
                    .Where(o => o.BusinessUnitId == businessUnitId
                        && o.LogicalGroupKey != null && groupKeys.Contains(o.LogicalGroupKey))
                    .Select(o => new { o.LogicalGroupKey, o.ExtractionJobId })
                    .ToListAsync(ct))
                .Select(o => (Key: o.LogicalGroupKey!, JobId: o.ExtractionJobId))
                .ToList();

        var jobIds = occurrences.Where(o => o.JobId.HasValue).Select(o => o.JobId!.Value).Distinct().ToList();
        var jobs = jobIds.Count == 0
            ? new List<(long Id, Guid BatchId, string? FileName)>()
            : (await _context.Set<ExtractionJob>().AsNoTracking()
                    .Where(j => j.BusinessUnitId == businessUnitId && jobIds.Contains(j.Id))
                    .Select(j => new { j.Id, j.BatchId, j.FileName })
                    .ToListAsync(ct))
                .Select(j => (j.Id, j.BatchId, j.FileName))
                .ToList();
        var jobsById = jobs.ToDictionary(j => j.Id);

        var ingestIds = rows.Select(r => r.Id).ToList();
        var leadIdByIngest = ingestIds.Count == 0
            ? new Dictionary<long, long>()
            : (await _context.Leads.AsNoTracking()
                    .Where(l => l.BusinessUnitId == businessUnitId
                        && l.EmailIngestsId != null && ingestIds.Contains(l.EmailIngestsId.Value))
                    .Select(l => new { IngestId = l.EmailIngestsId!.Value, l.Id })
                    .ToListAsync(ct))
                .GroupBy(x => x.IngestId)
                .ToDictionary(g => g.Key, g => g.Min(x => x.Id));

        // THE MESSAGE-LEVEL STATE. One row per ingest, or none for mail that predates capture.
        // No RawEvidenceUri and no RawEvidenceVersionId are projected: they leave the server
        // only as the two booleans below.
        var assemblies = (await _context.EmailInquiryAssemblies.AsNoTracking()
                .Where(a => a.BusinessUnitId == businessUnitId && ingestIds.Contains(a.EmailIngestId))
                .Select(a => new
                {
                    a.Id,
                    a.EmailIngestId,
                    a.Status,
                    a.StatusReason,
                    a.ExpectedComponentCount,
                    a.CompletedComponentCount,
                    a.AssembledLeadId,
                    HasRawEvidence = a.RawEvidenceUri != null,
                    HasRawDigest = a.RawEvidenceSha256 != null,
                    a.ReceivedAtUtc,
                    a.CreatedAtUtc,
                    a.UpdatedAtUtc
                })
                .ToListAsync(ct))
            .ToDictionary(a => a.EmailIngestId);

        // THE ATTACHMENT COUNT, from the persisted manifest.
        //
        // This used to File.OpenRead the stored .eml and count `message.Attachments` — the same
        // walk that was deleted from the fan-out, and wrong in the same way twice over. It is not
        // the MIME tree (MimeKit yields only entities whose Content-Disposition says
        // "attachment", so a forwarded enquiry counted zero), and the path it read is local
        // container storage that does not survive a deploy on the managed target — so the number
        // on the screen silently became "unknown" for every historic message and understated the
        // rest. The component rows are what the pipeline actually planned from the message, they
        // are tenant-scoped, and they cost no file I/O on a list render.
        var assemblyIds = assemblies.Values.Select(a => a.Id).ToList();
        var componentKindsByAssembly = assemblyIds.Count == 0
            ? new Dictionary<long, List<ERP_RFQ_Automation.Ingestion.Assembly.EmailInquiryComponentKind>>()
            : (await _context.EmailInquiryComponents.AsNoTracking()
                    .Where(c => c.BusinessUnitId == businessUnitId && assemblyIds.Contains(c.AssemblyId))
                    .Select(c => new { c.AssemblyId, c.Kind })
                    .ToListAsync(ct))
                .GroupBy(c => c.AssemblyId)
                .ToDictionary(g => g.Key, g => g.Select(c => c.Kind).ToList());

        var items = new List<EmailTriageRow>(rows.Count);
        foreach (var row in rows)
        {
            var key = $"email:{row.MessageId}";
            var messageJobs = occurrences
                .Where(o => o.Key == key && o.JobId.HasValue && jobsById.ContainsKey(o.JobId!.Value))
                .Select(o => jobsById[o.JobId!.Value])
                .ToList();

            assemblies.TryGetValue(row.Id, out var assembly);

            // Attachment names are known only for messages that produced jobs. For a message
            // that was STOPPED — the ones this screen exists for — the persisted manifest is
            // counted instead, because "rejected, and it had an attachment" is the single most
            // important fact on the page. Null stays "not known": a message with no assembly
            // and no jobs was never inspected, and claiming zero would be an absence nobody
            // verified.
            List<string>? attachmentNames = null;
            int? attachmentCount = null;
            if (messageJobs.Count > 0)
            {
                attachmentNames = messageJobs
                    .Where(j => !IsBodyDocument(j.FileName))
                    .Select(j => j.FileName ?? "(unnamed)")
                    .ToList();
                attachmentCount = attachmentNames.Count;
            }
            else if (assembly is not null
                && componentKindsByAssembly.TryGetValue(assembly.Id, out var kinds))
            {
                attachmentCount = kinds.Count(
                    k => k != ERP_RFQ_Automation.Ingestion.Assembly.EmailInquiryComponentKind.Body);
            }

            items.Add(new EmailTriageRow(
                Id: row.Id,
                ReceivedOn: row.CreatedOn,
                From: row.FromEmail,
                Subject: row.EmailSubject,
                Outcome: row.TriageOutcome ?? "Legacy",
                ReasonCodes: ParseReasonCodes(row.TriageReasonJson),
                HasAttachments: attachmentCount is > 0,
                LinkedBatchId: messageJobs.Count > 0 ? messageJobs[0].BatchId : null,
                ParseStatus: row.ParseStatus,
                TriageDecidedOn: row.TriageDecidedOn,
                AttachmentCount: attachmentCount,
                AttachmentNames: attachmentNames,
                BodySubmitted: messageJobs.Any(j => IsBodyDocument(j.FileName)),
                // The message-level lead is the assembled one when the message got that far. The
                // per-ingest lookup stays as the fallback for mail that predates assembly; if the
                // two ever disagree the assembly is authoritative, because it is written in the
                // same transaction as the Assembled status.
                LeadId: assembly?.AssembledLeadId
                    ?? (leadIdByIngest.TryGetValue(row.Id, out var leadId) ? leadId : null),
                SkippedAttachments: ParseSkippedAttachments(row.SkippedAttachmentsJson),
                AssemblyState: assembly?.Status.ToString(),
                AssemblyReason: assembly?.StatusReason,
                ExpectedComponentCount: assembly?.ExpectedComponentCount,
                CompletedComponentCount: assembly?.CompletedComponentCount,
                AssembledLeadId: assembly?.AssembledLeadId,
                RawEvidenceStored: assembly?.HasRawEvidence,
                RawEvidenceVerifiable: assembly?.HasRawDigest,
                IngestedAtUtc: assembly?.CreatedAtUtc,
                LastUpdatedAtUtc: assembly?.UpdatedAtUtc,
                SenderSentAtUtc: assembly?.ReceivedAtUtc,
                ParsedAt: row.ParsedAt));
        }

        return items;
    }

    public async Task<EmailTriageReprocessResult> ReprocessAsync(
        long businessUnitId, long id, string actor, string reason, string idempotencyKey,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(reason))
            throw new ArgumentException("A reason is required to reprocess a message.", nameof(reason));
        if (string.IsNullOrWhiteSpace(idempotencyKey))
            throw new ArgumentException("An idempotency key is required.", nameof(idempotencyKey));

        var ingest = await _context.EmailIngests
            .Include(e => e.EmailConfiguration)
            .FirstOrDefaultAsync(e => e.Id == id && e.EmailConfiguration.BusinessUnitId == businessUnitId, ct)
            ?? throw new KeyNotFoundException($"Email ingest {id} was not found for this tenant.");

        // THE AUTHORITATIVE COPY, through the one reader that knows where it lives and verifies
        // it against the hash recorded at capture.
        //
        // This used to File.OpenRead(ingest.RawEmailPath) directly. That path is local container
        // storage — ephemeral on the managed target, gone on the next deploy, and unverified: a
        // truncated or replaced file would have been reprocessed as though it were the message
        // the customer sent. The reader prefers the durable evidence object, checks its digest,
        // and falls back to the legacy path only for rows written before the assembly existed.
        var message = await _rawEmail.TryLoadAsync(businessUnitId, ingest, ct)
            ?? throw new InvalidOperationException(
                "The stored raw message is no longer available, so it cannot be reprocessed.");

        var parts = EmailBodyNormalizer.Normalize(GetBodyText(message));
        // FORCED Uncertain: a human overrode the gate, so the message is extracted and flagged
        // — never re-judged by the same rules that stopped it, and never silently promoted to
        // a clean Inquiry either.
        var decision = new EmailTriageDecision(
            EmailTriageOutcome.Uncertain,
            new[] { EmailTriageReasonCodes.ManualReprocess },
            CommercialDocumentTypeHint: null,
            ThreadContinuation: !string.IsNullOrWhiteSpace(message.InReplyTo) || message.References?.Count > 0);

        // The SAME canonical intake the poller uses. Capture is idempotent, so a reprocess of an
        // already-captured message resolves the existing assembly and re-schedules only the
        // components that still need it — no second assembly, no duplicated component jobs.
        var result = await _intake.CaptureAndScheduleAsync(
            message, ingest, ingest.EmailConfiguration, parts.Fresh, decision,
            ingest.FromEmail, ct);

        if (!result.SafeToAcknowledge)
            throw new InvalidOperationException(
                "The message could not be captured durably, so it was not reprocessed.");

        var queued = result.Scheduled + result.AlreadyScheduled;
        ingest.TriageOutcome = EmailTriageOutcome.Uncertain.ToString();
        ingest.TriageReasonJson = JsonSerializer.Serialize(decision.ReasonCodes);
        ingest.TriageDecidedOn = DateTime.UtcNow;
        ingest.ParseStatus = queued > 0 ? "Queued" : "Failed - nothing to extract";
        ingest.ParsedAt = queued > 0 ? null : DateTime.UtcNow;
        await _context.SaveChangesAsync(ct);

        _log.LogInformation(
            "Email ingest {IngestId} reprocessed as an inquiry by {Actor} (idempotency {Key}): "
            + "assembly {AssemblyId}, {Scheduled} scheduled, {AlreadyScheduled} already "
            + "scheduled, {Held} held, batch {BatchId}. Reason recorded.",
            ingest.Id, actor, idempotencyKey, result.AssemblyId, result.Scheduled,
            result.AlreadyScheduled, result.Held, result.BatchId);

        return new EmailTriageReprocessResult(
            ingest.Id, result.BatchId, queued,
            EmailTriageOutcome.Uncertain.ToString(), ingest.ParseStatus!);
    }

    private static bool IsBodyDocument(string? fileName)
        => fileName is not null && fileName.EndsWith("_body.txt", StringComparison.OrdinalIgnoreCase);

    /// <summary>ING-06: the same tolerant JSON-array reader as the reason codes. A corrupt column
    /// must read as "nothing recorded" rather than 500 the one page an operator opens to find
    /// mail the system dropped.</summary>
    internal static IReadOnlyList<string> ParseSkippedAttachments(string? json) => ParseReasonCodes(json);

    internal static IReadOnlyList<string> ParseReasonCodes(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Array.Empty<string>();
        try
        {
            return JsonSerializer.Deserialize<string[]>(json) ?? Array.Empty<string>();
        }
        catch (JsonException)
        {
            return Array.Empty<string>();
        }
    }

    private static string GetBodyText(MimeMessage message)
    {
        var text = message.GetTextBody(MimeKit.Text.TextFormat.Plain);
        if (!string.IsNullOrWhiteSpace(text)) return text;
        var html = message.GetTextBody(MimeKit.Text.TextFormat.Html);
        return html is null
            ? string.Empty
            : System.Text.RegularExpressions.Regex.Replace(html, "<.*?>", " ")
                .Replace("&nbsp;", " ")
                .Replace("\r\n", "\n");
    }
}
