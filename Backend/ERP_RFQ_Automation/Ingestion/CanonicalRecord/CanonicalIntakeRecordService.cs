using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ERP_RFQ_Automation.DocumentIntelligence.Persistence;
using ERP_RFQ_Automation.Extraction;
using ERP_RFQ_Automation.LeadIdentity;
using ERP_RFQ_Automation.Models;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Ingestion.CanonicalRecord;

public interface ICanonicalIntakeRecordService
{
    /// <summary>The canonical record for one processed email; null when the message does not
    /// exist in this tenant.</summary>
    Task<CanonicalIntakeRecord?> GetByEmailIngestIdAsync(
        long businessUnitId, long emailIngestId, CancellationToken ct = default);

    /// <summary>The same record looked up from the lead a reviewer has open. Null when the
    /// lead does not exist in this tenant or carries no email-ingest linkage.</summary>
    Task<CanonicalIntakeRecord?> GetByLeadIdAsync(
        long businessUnitId, long leadId, CancellationToken ct = default);
}

/// <summary>
/// Assembles the specification-§1 canonical intake record from the fragments the write side
/// already persists. READ-ONLY by construction: every query is <c>AsNoTracking</c>, every
/// query carries an explicit BusinessUnitId predicate even where a global filter exists
/// (fail-closed — a worker-scope context with no ambient tenant must behave identically),
/// and the whole assembly is a bounded number of set-based queries with no per-line
/// round trips.
///
/// <para>JOIN KEYS (all pre-existing): EmailIngest is the anchor;
/// <c>SourceDocumentOccurrence.LogicalGroupKey == $"email:{MessageId}"</c> finds every file
/// the message fanned out (the same key the triage screen uses); occurrence rows carry the
/// ExtractionJobId; <c>Lead.EmailIngestsId</c> finds the produced lead(s);
/// <c>CanonicalInquiry.LeadId</c> / <c>CanonicalLineItem.LeadItemId</c> reach the
/// deterministic-path evidence; <c>LeadIngestionOccurrence</c> reaches the identity verdict,
/// revisions and match candidates.</para>
/// </summary>
public sealed class CanonicalIntakeRecordService : ICanonicalIntakeRecordService
{
    private readonly ErpRfqAutomationContext _context;

    public CanonicalIntakeRecordService(ErpRfqAutomationContext context) => _context = context;

    public async Task<CanonicalIntakeRecord?> GetByEmailIngestIdAsync(
        long businessUnitId, long emailIngestId, CancellationToken ct = default)
        => await BuildAsync(businessUnitId, emailIngestId, focusLeadId: null, ct);

    public async Task<CanonicalIntakeRecord?> GetByLeadIdAsync(
        long businessUnitId, long leadId, CancellationToken ct = default)
    {
        var ingestId = await _context.Leads.AsNoTracking()
            .Where(l => l.BusinessUnitId == businessUnitId && l.Id == leadId)
            .Select(l => l.EmailIngestsId)
            .SingleOrDefaultAsync(ct);
        return ingestId.HasValue
            ? await BuildAsync(businessUnitId, ingestId.Value, focusLeadId: leadId, ct)
            : null;
    }

    private async Task<CanonicalIntakeRecord?> BuildAsync(
        long businessUnitId, long emailIngestId, long? focusLeadId, CancellationToken ct)
    {
        // ---- 1. The anchor: the message itself, tenant-scoped through its mailbox. ----
        var ingestRow = await _context.EmailIngests.AsNoTracking()
            .Where(e => e.Id == emailIngestId
                && e.EmailConfiguration.BusinessUnitId == businessUnitId)
            .Select(e => new
            {
                Ingest = e,
                Mailbox = e.EmailConfiguration.EmailAddress
            })
            .SingleOrDefaultAsync(ct);
        if (ingestRow is null)
            return null;
        var ingest = ingestRow.Ingest;

        // ---- The message's evidence-ledger fan-out (body + every enqueued attachment,
        //      including replays — each occurrence is a distinct recorded arrival). ----
        var groupKey = $"email:{ingest.MessageId}";
        var occurrences = await (
                from o in _context.Set<SourceDocumentOccurrence>().AsNoTracking()
                join d in _context.Set<SourceDocument>().AsNoTracking()
                    on o.SourceDocumentId equals d.Id
                where o.BusinessUnitId == businessUnitId
                    && d.BusinessUnitId == businessUnitId
                    && o.LogicalGroupKey == groupKey
                select new
                {
                    o.Id,
                    o.SourceDocumentId,
                    o.ExtractionJobId,
                    o.IntakeStatus,
                    o.OutcomeState,
                    o.ReceivedOn,
                    o.SourceMetadataJson,
                    d.OriginalFileName,
                    d.ContentHash,
                    d.SecurityStatus
                })
            .ToListAsync(ct);
        // DateTimeOffset ordering is done client-side: the SQLite test dialect cannot
        // ORDER BY it, and the row count here is one message's fan-out — a handful.
        occurrences = occurrences.OrderBy(o => o.ReceivedOn).ThenBy(o => o.Id).ToList();

        // ---- The produced lead(s). ----
        var leads = await _context.Leads.AsNoTracking()
            .Where(l => l.BusinessUnitId == businessUnitId && l.EmailIngestsId == emailIngestId)
            .OrderBy(l => l.Id)
            .ToListAsync(ct);
        var primaryLead = focusLeadId.HasValue
            ? leads.FirstOrDefault(l => l.Id == focusLeadId.Value)
            : leads.FirstOrDefault();
        if (focusLeadId.HasValue && primaryLead is null)
            return null; // the lead exists but is not this message's — never mix records
        var leadIds = leads.Select(l => l.Id).ToList();

        // ---- Extraction jobs: linked through occurrences, plus any job whose result is one
        //      of this message's leads (belt-and-braces for legacy rows with no occurrence). ----
        var jobIds = occurrences.Where(o => o.ExtractionJobId.HasValue)
            .Select(o => o.ExtractionJobId!.Value)
            .Distinct()
            .ToList();
        if (leadIds.Count > 0)
        {
            jobIds.AddRange(await _context.Set<ExtractionJob>().AsNoTracking()
                .Where(j => j.BusinessUnitId == businessUnitId
                    && j.ResultLeadId != null && leadIds.Contains(j.ResultLeadId.Value))
                .Select(j => j.Id)
                .ToListAsync(ct));
            jobIds = jobIds.Distinct().ToList();
        }
        var jobs = jobIds.Count == 0
            ? new List<ExtractionJob>()
            : await _context.Set<ExtractionJob>().AsNoTracking()
                .Where(j => j.BusinessUnitId == businessUnitId && jobIds.Contains(j.Id))
                .OrderBy(j => j.CreatedOn).ThenBy(j => j.Id)
                .ToListAsync(ct);
        var jobsById = jobs.ToDictionary(j => j.Id);

        // ---- Lead lines (primary lead only; sibling leads are separate records). ----
        var items = primaryLead is null
            ? new List<LeadItem>()
            : await _context.LeadItems.AsNoTracking()
                .Where(i => i.LeadId == primaryLead.Id && i.Lead.BusinessUnitId == businessUnitId)
                .OrderBy(i => i.Id)
                .ToListAsync(ct);

        // ---- Deterministic-path evidence graph. ----
        var inquiryIds = primaryLead is null
            ? new List<long>()
            : await _context.Set<CanonicalInquiry>().AsNoTracking()
                .Where(i => i.BusinessUnitId == businessUnitId && i.LeadId == primaryLead.Id)
                .Select(i => i.Id)
                .ToListAsync(ct);
        var itemIds = items.Select(i => i.Id).ToList();
        var canonicalLines = inquiryIds.Count == 0 && itemIds.Count == 0
            ? new List<CanonicalLineItem>()
            : await _context.Set<CanonicalLineItem>().AsNoTracking()
                .Where(l => l.BusinessUnitId == businessUnitId
                    && (inquiryIds.Contains(l.InquiryId)
                        || (l.LeadItemId != null && itemIds.Contains(l.LeadItemId.Value))))
                .ToListAsync(ct);
        var lineIds = canonicalLines.Select(l => l.Id).ToList();
        var leadItemByLineId = canonicalLines
            .Where(l => l.LeadItemId.HasValue)
            .ToDictionary(l => l.Id, l => l.LeadItemId!.Value);

        List<IntakeFieldEvidence> fieldEvidence;
        if (inquiryIds.Count == 0 && lineIds.Count == 0)
        {
            fieldEvidence = new List<IntakeFieldEvidence>();
        }
        else
        {
            var evidenceRows = await (
                    from ev in _context.Set<FieldEvidence>().AsNoTracking()
                    join region in _context.Set<DocumentRegion>().AsNoTracking()
                        on ev.RegionId equals region.Id
                    join page in _context.Set<DocumentPage>().AsNoTracking()
                        on region.PageId equals page.Id
                    where ev.BusinessUnitId == businessUnitId
                        && region.BusinessUnitId == businessUnitId
                        && page.BusinessUnitId == businessUnitId
                        && ((ev.InquiryId != null && inquiryIds.Contains(ev.InquiryId.Value))
                            || (ev.LineItemId != null && lineIds.Contains(ev.LineItemId.Value)))
                    orderby ev.Id
                    select new
                    {
                        ev.InquiryId,
                        ev.LineItemId,
                        ev.FieldName,
                        ev.RawValue,
                        ev.NormalizedValue,
                        ev.Confidence,
                        ev.Extractor,
                        ev.ValidationStatus,
                        region.SourceAddress,
                        page.PageNumber,
                        page.SheetName
                    })
                .ToListAsync(ct);
            fieldEvidence = evidenceRows.Select(e => new IntakeFieldEvidence(
                Scope: e.InquiryId.HasValue ? "Header" : "Line",
                LeadItemId: e.LineItemId.HasValue
                    && leadItemByLineId.TryGetValue(e.LineItemId.Value, out var li)
                    ? li : null,
                FieldName: e.FieldName,
                RawValue: e.RawValue,
                NormalizedValue: e.NormalizedValue,
                Confidence: e.Confidence,
                Extractor: e.Extractor,
                ValidationStatus: e.ValidationStatus.ToString(),
                SourceAddress: e.SourceAddress,
                PageNumber: e.PageNumber,
                SheetName: e.SheetName)).ToList();
        }

        // ---- Validation findings, reachable from THIS record: the message's runs, plus the
        //      lead's inquiry/line targets (closes the "no direct path from the lead" gap). ----
        var runIds = jobIds.Count == 0
            ? new List<long>()
            : await _context.Set<ExtractionRun>().AsNoTracking()
                .Where(r => r.BusinessUnitId == businessUnitId && jobIds.Contains(r.ExtractionJobId))
                .Select(r => r.Id)
                .ToListAsync(ct);
        var findings = runIds.Count == 0 && inquiryIds.Count == 0 && lineIds.Count == 0
            ? new List<ValidationFinding>()
            : await _context.Set<ValidationFinding>().AsNoTracking()
                .Where(f => f.BusinessUnitId == businessUnitId
                    && (runIds.Contains(f.ExtractionRunId)
                        || (f.InquiryId != null && inquiryIds.Contains(f.InquiryId.Value))
                        || (f.LineItemId != null && lineIds.Contains(f.LineItemId.Value))))
                .OrderBy(f => f.Id)
                .ToListAsync(ct);

        // ---- Identity verdicts: duplicate / revision classification and candidates. ----
        var evidenceOccurrenceIds = occurrences.Select(o => o.Id).ToList();
        var identityOccurrences = await _context.Set<LeadIngestionOccurrence>().AsNoTracking()
            .Where(o => o.BusinessUnitId == businessUnitId
                && ((o.LeadId != null && leadIds.Contains(o.LeadId.Value))
                    || (o.ExtractionJobId != null && jobIds.Contains(o.ExtractionJobId.Value))
                    || (o.SourceDocumentOccurrenceId != null
                        && evidenceOccurrenceIds.Contains(o.SourceDocumentOccurrenceId.Value))))
            .ToListAsync(ct);
        identityOccurrences = identityOccurrences
            .OrderBy(o => o.CreatedAtUtc).ThenBy(o => o.Id).ToList();
        var identityOccurrenceIds = identityOccurrences.Select(o => o.Id).ToList();
        var revisionIds = identityOccurrences
            .Where(o => o.LeadRevisionId.HasValue)
            .Select(o => o.LeadRevisionId!.Value)
            .Distinct()
            .ToList();
        var revisionNumberById = revisionIds.Count == 0
            ? new Dictionary<long, int>()
            : await _context.Set<LeadRevision>().AsNoTracking()
                .Where(r => r.BusinessUnitId == businessUnitId && revisionIds.Contains(r.Id))
                .Select(r => new { r.Id, r.RevisionNumber })
                .ToDictionaryAsync(r => r.Id, r => r.RevisionNumber, ct);
        var matchCandidates = identityOccurrenceIds.Count == 0
            ? new List<LeadMatchCandidate>()
            : await _context.Set<LeadMatchCandidate>().AsNoTracking()
                .Where(c => c.BusinessUnitId == businessUnitId
                    && identityOccurrenceIds.Contains(c.OccurrenceId))
                .OrderBy(c => c.Id)
                .ToListAsync(ct);

        // ---- Audit history: identity events + review events, merged, oldest first. ----
        var identityAudit = await _context.Set<LeadIdentityAuditEvent>().AsNoTracking()
            .Where(a => a.BusinessUnitId == businessUnitId
                && ((a.LeadId != null && leadIds.Contains(a.LeadId.Value))
                    || identityOccurrenceIds.Contains(a.OccurrenceId)))
            .ToListAsync(ct);
        identityAudit = identityAudit
            .OrderBy(a => a.OccurredAtUtc).ThenBy(a => a.Id).ToList();
        var reviewAudit = primaryLead is null
            ? new List<LeadReviewAudit>()
            : await _context.Set<LeadReviewAudit>().AsNoTracking()
                .Where(a => a.BusinessUnitId == businessUnitId && a.LeadId == primaryLead.Id)
                .OrderBy(a => a.ReviewedOn).ThenBy(a => a.Id)
                .ToListAsync(ct);

        // ================================ assembly (pure) ================================

        var inventory = BuildInventory(ingest, occurrences.Select(o => new OccurrenceFacts(
            o.Id, o.SourceDocumentId, o.ExtractionJobId, o.IntakeStatus.ToString(),
            o.OutcomeState.ToString(), o.ReceivedOn, o.SourceMetadataJson, o.OriginalFileName,
            o.ContentHash, o.SecurityStatus.ToString())).ToList(), jobsById);

        var primaryIdentity = primaryLead is null
            ? null
            : identityOccurrences
                .Where(o => o.LeadId == primaryLead.Id
                    && o.RecordKind == LeadOccurrenceRecordKind.Ingestion)
                .OrderByDescending(o => o.CreatedAtUtc).ThenByDescending(o => o.Id)
                .FirstOrDefault();

        var classification = new IntakeClassification(
            TriageOutcome: ingest.TriageOutcome ?? "Legacy",
            TriageReasonCodes: ParseJsonStringArray(ingest.TriageReasonJson),
            TriageDecidedOn: ingest.TriageDecidedOn,
            AiConfidence: primaryLead?.Aiconfidence,
            ProcessingPath: primaryIdentity?.ProcessingPath.ToString(),
            ExternalAiUsed: primaryIdentity?.ExternalAiUsed);

        var header = primaryLead is null
            ? null
            : new IntakeExtractedHeader(
                LeadId: primaryLead.Id,
                NexoraSerial: primaryLead.CommercialCaseReference,
                RfqNumber: primaryLead.Rfqno,
                BuyerName: primaryLead.BuyersName,
                ClientEmail: primaryLead.Clientemail,
                ReceivedDate: primaryLead.RecDate,
                BidClosingDate: primaryLead.BidClosingDate,
                LeadSource: primaryLead.LeadSource,
                EmailSource: primaryLead.EmailSource,
                AiConfidence: primaryLead.Aiconfidence,
                LineItemCount: primaryLead.NoOfLineItems,
                CurrentRevisionNumber: primaryLead.CurrentRevisionNumber);

        var lines = items.Select(i => new IntakeExtractedLine(
            LeadItemId: i.Id,
            LineNumber: i.LineItemNo,
            ProductName: i.ProductShortName,
            Description: i.ProductShortDescription,
            Quantity: i.Quantity,
            UnitOfMeasure: i.UnitOfMeasure,
            ManufacturerName: i.ManufacturerName,
            ManufacturerPartNumber: i.ManufacturerPartNumber,
            Confidence: i.Aiconfidence)).ToList();

        var evidenceSection = new IntakeEvidenceSection(
            PerFieldEvidenceRecorded: fieldEvidence.Count > 0,
            Note: fieldEvidence.Count > 0 || primaryLead is null
                ? null
                : "No per-field evidence was recorded for this message's extracted values. "
                  + "The deterministic spreadsheet path anchors every value to a document "
                  + "region; LLM-path extraction does not, and this record reports that "
                  + "absence rather than fabricating provenance.",
            Fields: fieldEvidence);

        var validationIssues = findings.Select(f => new IntakeValidationIssue(
            ExtractionRunId: f.ExtractionRunId,
            Scope: f.LineItemId.HasValue ? "Line" : f.InquiryId.HasValue ? "Header" : "Run",
            LeadItemId: f.LineItemId.HasValue
                && leadItemByLineId.TryGetValue(f.LineItemId.Value, out var li) ? li : null,
            Code: f.Code,
            Severity: f.Severity.ToString(),
            Message: f.Message,
            CreatedOn: f.CreatedOn)).ToList();

        var identity = new IntakeIdentityDecision(
            Occurrences: identityOccurrences.Select(o => new IntakeOccurrenceDecision(
                OccurrenceId: o.Id,
                Classification: o.Classification.ToString(),
                Confidence: o.Confidence,
                DecisionReasons: o.DecisionReasons(),
                ProcessingPath: o.ProcessingPath.ToString(),
                RecordKind: o.RecordKind.ToString(),
                RevisionNumber: o.LeadRevisionId.HasValue
                    && revisionNumberById.TryGetValue(o.LeadRevisionId.Value, out var rev)
                    ? rev : null,
                CreatedAtUtc: o.CreatedAtUtc)).ToList(),
            MatchCandidates: matchCandidates.Select(c => new IntakeMatchCandidate(
                OccurrenceId: c.OccurrenceId,
                CandidateLeadId: c.CandidateLeadId,
                Confidence: c.Confidence,
                ReviewState: c.ReviewState.ToString(),
                ReviewedBy: c.ReviewedBy,
                ReviewedAtUtc: c.ReviewedAtUtc)).ToList());

        var audit = identityAudit
            .Select(a => new IntakeAuditEntry(
                At: a.OccurredAtUtc,
                Source: "identity",
                EventType: a.EventType,
                Actor: $"{a.ActorType}:{a.ActorId}",
                Detail: a.PayloadJson))
            .Concat(reviewAudit.Select(a => new IntakeAuditEntry(
                At: new DateTimeOffset(DateTime.SpecifyKind(a.ReviewedOn, DateTimeKind.Utc)),
                Source: "review",
                EventType: a.Action,
                Actor: a.ReviewedBy,
                Detail: a.Reason)))
            .OrderBy(a => a.At)
            .ToList();

        var record = new CanonicalIntakeRecord(
            SourceEmail: new IntakeSourceEmail(
                EmailIngestId: ingest.Id,
                Mailbox: ingestRow.Mailbox,
                MessageId: ingest.MessageId,
                InReplyToMessageId: ingest.InReplyToMessageId,
                References: ParseJsonStringArray(ingest.ReferencesJson),
                ReceivedOn: ingest.CreatedOn,
                RawEmailAvailable: !string.IsNullOrWhiteSpace(ingest.RawEmailPath)
                    && (ERP_RFQ_Automation.Infrastructure.Storage.EvidenceObjectUris.IsObjectUri(ingest.RawEmailPath)
                        || File.Exists(ingest.RawEmailPath)),
                ParseStatus: ingest.ParseStatus),
            Classification: classification,
            Message: new IntakeMessageMetadata(
                From: ingest.FromEmail,
                To: ingest.ToEmail,
                Subject: ingest.EmailSubject,
                SentOn: identityOccurrences
                    .Select(o => o.SourceReceivedAtUtc)
                    .FirstOrDefault(d => d.HasValue)),
            Inventory: inventory,
            Header: header,
            Lines: lines,
            OtherLeadIds: primaryLead is null
                ? Array.Empty<long>()
                : leads.Where(l => l.Id != primaryLead.Id).Select(l => l.Id).ToList(),
            Evidence: evidenceSection,
            ValidationIssues: validationIssues,
            Identity: identity,
            AuditTrail: audit,
            FinalStatus: DeriveFinalStatus(
                ingest.ParseStatus,
                jobs,
                occurrences.Select(o => o.IntakeStatus).ToList(),
                primaryLead is not null || leads.Count > 0,
                primaryLead));
        return record;
    }

    // ====================================================================== final status

    /// <summary>
    /// Section 11: the ONE derived status — the honest join of EmailIngest.ParseStatus, the
    /// extraction-job statuses, the occurrence intake statuses and the review state.
    ///
    /// DERIVATION RULES (ordered; the first that applies wins — pinned by
    /// CanonicalIntakeRecordTests):
    ///
    ///  1. InProgress            — any extraction job is non-terminal (Pending / Leased /
    ///                             Extracting / Persisting), OR no job exists yet while
    ///                             ParseStatus still says Pending/Queued (the fan-out or
    ///                             crash-window state). In-flight truth beats every terminal
    ///                             claim. When jobs exist and are ALL terminal, a stale
    ///                             "Queued" ParseStatus is ignored: the ledger outranks the
    ///                             flag.
    ///  2. A lead exists:
    ///     a. CompletedWithFailures — at least one sibling job dead-lettered or an
    ///                             occurrence is DeadLetter/Rejected. The message produced a
    ///                             lead AND lost something; both facts stay visible.
    ///     b. NeedsReview        — ParseStatus == "NeedsReview", or the lead awaits
    ///                             commercial review (RequiresCommercialReview with no
    ///                             ReviewApprovedOn).
    ///     c. Completed          — otherwise (including the reviewed-and-approved
    ///                             "Success" state).
    ///  3. No lead exists:
    ///     a. Rejected           — ParseStatus == "Rejected": the triage gate refused the
    ///                             message before any spend; Classification says why.
    ///     b. DeadLettered       — any job is DeadLetter, or ParseStatus is the dead-letter
    ///                             writeback ("Failed - extraction dead-lettered").
    ///     c. Failed             — any other "Failed*" ParseStatus (nothing to extract, raw
    ///                             message lost, …).
    ///     d. ProcessedNoLead    — every job finished (Succeeded/Duplicate/Failed-free) yet
    ///                             no lead was minted (e.g. a commercial non-inquiry
    ///                             document completed without creating one).
    ///     e. Unknown            — legacy rows carrying none of the above signals.
    /// </summary>
    internal static string DeriveFinalStatus(
        string? parseStatus,
        IReadOnlyList<ExtractionJob> jobs,
        IReadOnlyList<IntakeOccurrenceStatus> occurrenceStatuses,
        bool anyLead,
        Lead? primaryLead)
    {
        var anyJobInFlight = jobs.Any(j => j.Status is ExtractionStatus.Pending
            or ExtractionStatus.Leased or ExtractionStatus.Extracting
            or ExtractionStatus.Persisting);
        if (anyJobInFlight || (jobs.Count == 0 && parseStatus is "Pending" or "Queued"))
            return IntakeFinalStatus.InProgress;

        var anyJobDeadLettered = jobs.Any(j => j.Status == ExtractionStatus.DeadLetter);
        var anyOccurrenceLost = occurrenceStatuses.Any(s =>
            s is IntakeOccurrenceStatus.DeadLetter or IntakeOccurrenceStatus.Rejected);

        if (anyLead)
        {
            if (anyJobDeadLettered || anyOccurrenceLost)
                return IntakeFinalStatus.CompletedWithFailures;
            var awaitingReview = parseStatus == "NeedsReview"
                || (primaryLead is { RequiresCommercialReview: true, ReviewApprovedOn: null });
            return awaitingReview ? IntakeFinalStatus.NeedsReview : IntakeFinalStatus.Completed;
        }

        if (parseStatus == "Rejected")
            return IntakeFinalStatus.Rejected;
        if (anyJobDeadLettered || parseStatus == ExtractionWorker.DeadLetterParseStatus)
            return IntakeFinalStatus.DeadLettered;
        if (parseStatus is not null && parseStatus.StartsWith("Failed", StringComparison.Ordinal))
            return IntakeFinalStatus.Failed;
        if (jobs.Count > 0)
            return IntakeFinalStatus.ProcessedNoLead;
        return IntakeFinalStatus.Unknown;
    }

    // ======================================================================== inventory

    private sealed record OccurrenceFacts(
        long Id, long SourceDocumentId, long? ExtractionJobId, string IntakeStatus,
        string OutcomeState, DateTimeOffset ReceivedOn, string SourceMetadataJson,
        string OriginalFileName, string ContentHash, string SecurityStatus);

    /// <summary>
    /// The UNIFIED answer to "what files arrived on this message". Enqueued files come from
    /// the evidence ledger (one row per recorded occurrence — a replay is a second, honest
    /// arrival); skipped files come from EmailIngest.SkippedAttachmentsJson (the single
    /// durable skip record, ING-06). The legacy per-lead Attachment rows describe the same
    /// stored documents and are deliberately NOT a third source.
    /// </summary>
    private static List<IntakeInventoryEntry> BuildInventory(
        EmailIngest ingest,
        IReadOnlyList<OccurrenceFacts> occurrences,
        IReadOnlyDictionary<long, ExtractionJob> jobsById)
    {
        var entries = new List<IntakeInventoryEntry>(occurrences.Count + 4);

        foreach (var o in occurrences)
        {
            var sourceOccurrenceId = ReadSourceOccurrenceId(o.SourceMetadataJson);
            var isBody = (sourceOccurrenceId is not null
                    && sourceOccurrenceId.EndsWith(":body", StringComparison.Ordinal))
                || o.OriginalFileName.EndsWith("_body.txt", StringComparison.OrdinalIgnoreCase);
            ExtractionJob? job = null;
            if (o.ExtractionJobId.HasValue)
                jobsById.TryGetValue(o.ExtractionJobId.Value, out job);
            entries.Add(new IntakeInventoryEntry(
                Kind: isBody ? "Body" : "Attachment",
                Disposition: "Enqueued",
                FileName: o.OriginalFileName,
                SourceDocumentId: o.SourceDocumentId,
                SourceDocumentOccurrenceId: o.Id,
                ContentHash: o.ContentHash,
                SecurityStatus: o.SecurityStatus,
                IntakeStatus: o.IntakeStatus,
                OutcomeState: o.OutcomeState,
                ExtractionJobId: o.ExtractionJobId,
                JobStatus: job?.Status.ToString(),
                JobLastError: job?.LastError,
                ResultLeadId: job?.ResultLeadId,
                ReceivedOn: o.ReceivedOn,
                SkippedReason: null));
        }

        foreach (var entry in ParseJsonStringArray(ingest.SkippedAttachmentsJson))
        {
            var (fileName, reason) = SplitSkippedEntry(entry);
            entries.Add(new IntakeInventoryEntry(
                Kind: "Attachment",
                Disposition: "Skipped",
                FileName: fileName,
                SourceDocumentId: null,
                SourceDocumentOccurrenceId: null,
                ContentHash: null,
                SecurityStatus: null,
                IntakeStatus: null,
                OutcomeState: null,
                ExtractionJobId: null,
                JobStatus: null,
                JobLastError: null,
                ResultLeadId: null,
                ReceivedOn: null,
                SkippedReason: reason ?? entry));
        }

        return entries;
    }

    /// <summary>Reads <c>metadata.SourceOccurrenceId</c> out of the occurrence's provenance
    /// JSON (shape: <c>DocumentIngestionService.BuildSourceMetadata</c>). Tolerant: a corrupt
    /// column reads as "not recorded", never a 500 on the audit surface.</summary>
    private static string? ReadSourceOccurrenceId(string sourceMetadataJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(sourceMetadataJson);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("metadata", out var metadata)
                && metadata.ValueKind == JsonValueKind.Object
                && metadata.TryGetProperty("SourceOccurrenceId", out var id)
                && id.ValueKind == JsonValueKind.String)
                return id.GetString();
        }
        catch (JsonException)
        {
            // fall through — the filename heuristic still classifies body vs attachment
        }
        return null;
    }

    /// <summary>Splits the durable ING-06 entry shape <c>"filename (reason)"</c>. When the
    /// shape does not hold (a truncation marker, say), the whole entry is the filename and
    /// the raw entry doubles as the reason — nothing is dropped.</summary>
    internal static (string FileName, string? Reason) SplitSkippedEntry(string entry)
    {
        var open = entry.LastIndexOf(" (", StringComparison.Ordinal);
        if (open > 0 && entry.EndsWith(")", StringComparison.Ordinal))
            return (entry[..open], entry[(open + 2)..^1]);
        return (entry, null);
    }

    /// <summary>The same tolerant JSON-string-array reader the triage surface uses: a corrupt
    /// column must read as "nothing recorded" rather than break the one record a reviewer
    /// opens to find out what happened.</summary>
    internal static IReadOnlyList<string> ParseJsonStringArray(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return Array.Empty<string>();
        try
        {
            return JsonSerializer.Deserialize<string[]>(json) ?? Array.Empty<string>();
        }
        catch (JsonException)
        {
            return Array.Empty<string>();
        }
    }
}
