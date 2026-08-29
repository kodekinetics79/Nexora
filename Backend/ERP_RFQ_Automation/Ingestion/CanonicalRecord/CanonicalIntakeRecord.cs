using System;
using System.Collections.Generic;

namespace ERP_RFQ_Automation.Ingestion.CanonicalRecord;

/// <summary>
/// Specification §1: ONE queryable record per processed email, exposing every contract fact
/// the pipeline persisted about it — assembled from the fragments that already exist
/// (EmailIngest, evidence ledger, extraction queue, lead-identity graph, Lead) without
/// re-plumbing the write side. Everything here is a PROJECTION of durable rows; nothing in
/// this shape is written back.
/// </summary>
public sealed record CanonicalIntakeRecord(
    // 1. Source email occurrence.
    IntakeSourceEmail SourceEmail,
    // 2. Classification and confidence.
    IntakeClassification Classification,
    // 3. Original message metadata.
    IntakeMessageMetadata Message,
    // 4. The UNIFIED attachment inventory: every file that arrived, each with its fate.
    IReadOnlyList<IntakeInventoryEntry> Inventory,
    // 5–6. Extracted RFQ header + lines. Null when this message produced no lead.
    IntakeExtractedHeader? Header,
    IReadOnlyList<IntakeExtractedLine> Lines,
    /// <summary>An email can split into several leads. Sections 5–10 describe
    /// <see cref="IntakeExtractedHeader.LeadId"/>; the siblings are listed here and each is
    /// addressable via <c>GET api/intake-records/by-lead/{id}</c>.</summary>
    IReadOnlyList<long> OtherLeadIds,
    // 7. Evidence for extracted values.
    IntakeEvidenceSection Evidence,
    // 8. Validation issues.
    IReadOnlyList<IntakeValidationIssue> ValidationIssues,
    // 9. Duplicate / revision decision.
    IntakeIdentityDecision Identity,
    // 10. Audit history (identity + review events, merged, oldest first).
    IReadOnlyList<IntakeAuditEntry> AuditTrail,
    // 11. One derived status. Derivation rules: see CanonicalIntakeRecordService.DeriveFinalStatus.
    string FinalStatus);

/// <param name="RawEmailAvailable">True only when the stored .eml provably exists on disk
/// right now — never inferred from the path column alone.</param>
public sealed record IntakeSourceEmail(
    long EmailIngestId,
    string Mailbox,
    string MessageId,
    string? InReplyToMessageId,
    IReadOnlyList<string> References,
    DateTime ReceivedOn,
    bool RawEmailAvailable,
    string? ParseStatus);

/// <param name="TriageOutcome">Inquiry | CommercialNonInquiry | Noise | Uncertain, or
/// "Legacy" for mail ingested before the gate existed.</param>
/// <param name="AiConfidence">The extraction confidence stamped on the produced lead.
/// Null when no lead exists — a rejection has a triage classification but no AI verdict,
/// and this record never invents one.</param>
public sealed record IntakeClassification(
    string TriageOutcome,
    IReadOnlyList<string> TriageReasonCodes,
    DateTime? TriageDecidedOn,
    decimal? AiConfidence,
    string? ProcessingPath,
    bool? ExternalAiUsed);

public sealed record IntakeMessageMetadata(
    string? From,
    string? To,
    string? Subject,
    DateTimeOffset? SentOn);

/// <summary>
/// One row per file the message carried (plus the extracted body text, marked
/// <c>Kind == "Body"</c> so a caller counting attachments can exclude it). This list is the
/// reconciliation of the three places attachment facts were booked — the evidence ledger
/// (SourceDocument/SourceDocumentOccurrence) for files that were enqueued, and
/// EmailIngest.SkippedAttachmentsJson for files that were not. The legacy Attachment table
/// carries no file the ledger does not (it is written per produced lead from the same job),
/// so it contributes no rows of its own.
/// </summary>
/// <param name="Disposition">"Enqueued" — the file entered extraction (the SourceDocument
/// facts follow) | "Skipped" — the intake door dropped it (<paramref name="SkippedReason"/>
/// says why).</param>
public sealed record IntakeInventoryEntry(
    string Kind, // "Attachment" | "Body"
    string Disposition, // "Enqueued" | "Skipped"
    string FileName,
    // -- Enqueued fate (null when skipped) --
    long? SourceDocumentId,
    long? SourceDocumentOccurrenceId,
    string? ContentHash,
    string? SecurityStatus,
    string? IntakeStatus,
    string? OutcomeState,
    long? ExtractionJobId,
    string? JobStatus,
    string? JobLastError,
    long? ResultLeadId,
    DateTimeOffset? ReceivedOn,
    // -- Skipped fate (null when enqueued) --
    string? SkippedReason);

public sealed record IntakeExtractedHeader(
    long LeadId,
    string? NexoraSerial,
    string? RfqNumber,
    string? BuyerName,
    string? ClientEmail,
    DateTime ReceivedDate,
    DateTime? BidClosingDate,
    string? LeadSource,
    string? EmailSource,
    decimal? AiConfidence,
    int? LineItemCount,
    int CurrentRevisionNumber);

public sealed record IntakeExtractedLine(
    long LeadItemId,
    string? LineNumber,
    string? ProductName,
    string? Description,
    decimal? Quantity,
    string? UnitOfMeasure,
    string? ManufacturerName,
    string? ManufacturerPartNumber,
    decimal? Confidence);

/// <param name="PerFieldEvidenceRecorded">True when at least one region-anchored
/// FieldEvidence row exists for this lead's values (the deterministic spreadsheet path
/// writes them). False is an HONEST absence: LLM-path extraction records no per-field
/// regions, and this record says so instead of fabricating provenance.</param>
public sealed record IntakeEvidenceSection(
    bool PerFieldEvidenceRecorded,
    string? Note,
    IReadOnlyList<IntakeFieldEvidence> Fields);

/// <param name="Scope">"Header" (inquiry-level value) or "Line".</param>
public sealed record IntakeFieldEvidence(
    string Scope,
    long? LeadItemId,
    string FieldName,
    string? RawValue,
    string? NormalizedValue,
    decimal Confidence,
    string Extractor,
    string ValidationStatus,
    string? SourceAddress,
    int? PageNumber,
    string? SheetName);

public sealed record IntakeValidationIssue(
    long ExtractionRunId,
    string Scope, // "Run" | "Header" | "Line"
    long? LeadItemId,
    string Code,
    string Severity,
    string Message,
    DateTimeOffset CreatedOn);

/// <summary>Section 9: what the identity arbiter decided about this arrival.</summary>
public sealed record IntakeIdentityDecision(
    IReadOnlyList<IntakeOccurrenceDecision> Occurrences,
    IReadOnlyList<IntakeMatchCandidate> MatchCandidates);

public sealed record IntakeOccurrenceDecision(
    long OccurrenceId,
    string Classification,
    decimal Confidence,
    IReadOnlyList<string> DecisionReasons,
    string ProcessingPath,
    string RecordKind,
    int? RevisionNumber,
    DateTimeOffset CreatedAtUtc);

public sealed record IntakeMatchCandidate(
    long OccurrenceId,
    long CandidateLeadId,
    decimal Confidence,
    string ReviewState,
    string? ReviewedBy,
    DateTimeOffset? ReviewedAtUtc);

/// <param name="Source">"identity" (LeadIdentityAuditEvent) or "review" (LeadReviewAudit).</param>
public sealed record IntakeAuditEntry(
    DateTimeOffset At,
    string Source,
    string EventType,
    string Actor,
    string? Detail);

/// <summary>The closed vocabulary of <see cref="CanonicalIntakeRecord.FinalStatus"/>.</summary>
public static class IntakeFinalStatus
{
    public const string InProgress = "InProgress";
    public const string Completed = "Completed";
    public const string NeedsReview = "NeedsReview";
    public const string CompletedWithFailures = "CompletedWithFailures";
    public const string Rejected = "Rejected";
    public const string DeadLettered = "DeadLettered";
    public const string Failed = "Failed";
    public const string ProcessedNoLead = "ProcessedNoLead";
    public const string Unknown = "Unknown";
}
