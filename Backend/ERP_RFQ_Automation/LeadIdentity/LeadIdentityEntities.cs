using System.Text.Json;

namespace ERP_RFQ_Automation.LeadIdentity;

public enum LeadOccurrenceClassification
{
    Pending,
    New,
    ExactDuplicate,
    Revision,
    PossibleMatchReviewRequired,
    RejectedOrUnprocessable
}

public enum LeadProcessingPath
{
    Deterministic,
    LocalParser,
    LocalModel,
    HumanReview,
    ExternalModel
}

public enum LeadRevisionChangeType { Added, Removed, Modified, Unchanged }
public enum LeadMatchReviewState { Pending, ConfirmedDuplicate, ConfirmedRevision, CreatedNew, Rejected, Deferred }

public sealed class LeadIngestionBatch
{
    public Guid Id { get; set; }
    public long BusinessUnitId { get; set; }
    public string SourceChannel { get; set; } = null!;
    public string CreatedBy { get; set; } = null!;
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public int Version { get; set; } = 1;
    public ICollection<LeadIngestionOccurrence> Occurrences { get; } = new List<LeadIngestionOccurrence>();
}

/// <summary>
/// What a <see cref="LeadIngestionOccurrence"/> row actually records.
///
/// <para>A revision cannot exist without an occurrence — <c>EstablishedByOccurrenceId</c> is NOT
/// NULL. So establishing an identity baseline for a lead that never had one must mint an
/// occurrence row, and that row describes no received document. Without a discriminator every
/// analytics reader counts it as a real inbound document: ingestion volume, leads-received, the
/// touchless-decision KPI on the auditor-facing screen, and the extraction-accuracy ground
/// truth all move. This column is what keeps a baseline from masquerading as an arrival.</para>
/// </summary>
public enum LeadOccurrenceRecordKind
{
    /// <summary>A document really arrived through a channel. Everything counts this.</summary>
    Ingestion = 0,

    /// <summary>Canonical identity established from a lead's stored commercial facts. Records
    /// CONTENT, not arrival: no receipt time, no content hash, no sender, no document.</summary>
    IdentityBaseline = 1
}

public sealed class LeadIngestionOccurrence
{
    public long Id { get; set; }

    /// <summary>Ingestion vs identity baseline — see <see cref="LeadOccurrenceRecordKind"/>.
    /// Provenance-immutable: the update guard refuses to change it.</summary>
    public LeadOccurrenceRecordKind RecordKind { get; set; } = LeadOccurrenceRecordKind.Ingestion;
    public long BusinessUnitId { get; set; }
    public Guid BatchId { get; set; }
    public long? LeadId { get; set; }
    public long? LeadRevisionId { get; set; }
    public long? SourceDocumentId { get; set; }
    public long? SourceDocumentOccurrenceId { get; set; }
    public long? ExtractionJobId { get; set; }
    public string SourceChannel { get; set; } = null!;
    public string IdempotencyKey { get; set; } = null!;
    public string? ExternalSourceId { get; set; }
    public string? EmailThreadId { get; set; }
    public string? LogicalGroupKey { get; set; }
    public string? SourceSystem { get; set; }
    public string? Sender { get; set; }
    public string? Subject { get; set; }
    public string? OriginalFileName { get; set; }
    public string? MimeType { get; set; }
    public long? FileSize { get; set; }
    public string? ContentHash { get; set; }
    public string? CustomerScopeKey { get; set; }
    public string LogicalInquiryFingerprint { get; set; } = null!;
    public LeadOccurrenceClassification Classification { get; set; }
    public decimal Confidence { get; set; }
    public string DecisionReasonsJson { get; set; } = "[]";
    public string PolicyVersion { get; set; } = "release-01a/v1";
    public LeadProcessingPath ProcessingPath { get; set; }
    public bool ExternalAiUsed { get; set; }
    public decimal? ExternalCost { get; set; }
    public DateTimeOffset? SourceReceivedAtUtc { get; set; }
    public DateTimeOffset IngestedAtUtc { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public string ActorType { get; set; } = "Service";
    public string ActorId { get; set; } = null!;
    public string CorrelationId { get; set; } = null!;
    public int Version { get; set; } = 1;
    public LeadIngestionBatch Batch { get; set; } = null!;
    public Models.Lead? Lead { get; set; }
    public LeadRevision? Revision { get; set; }
    public ICollection<LeadMatchCandidate> MatchCandidates { get; } = new List<LeadMatchCandidate>();
    public ICollection<LeadOccurrenceDocument> Documents { get; } = new List<LeadOccurrenceDocument>();

    public IReadOnlyList<string> DecisionReasons() =>
        JsonSerializer.Deserialize<string[]>(DecisionReasonsJson) ?? Array.Empty<string>();
}

public sealed class LeadOccurrenceDocument
{
    public long Id { get; set; }
    public long BusinessUnitId { get; set; }
    public long OccurrenceId { get; set; }
    public long SourceDocumentId { get; set; }
    public string Role { get; set; } = "Primary";
    public int Ordinal { get; set; }
    public DateTimeOffset LinkedAtUtc { get; set; }
    public LeadIngestionOccurrence Occurrence { get; set; } = null!;
}

public sealed class LeadRevision
{
    public long Id { get; set; }
    public long BusinessUnitId { get; set; }
    public long LeadId { get; set; }
    public int RevisionNumber { get; set; }
    public long EstablishedByOccurrenceId { get; set; }
    public string LogicalInquiryFingerprint { get; set; } = null!;
    public string SnapshotJson { get; set; } = null!;
    public string? CustomerRfqReference { get; set; }
    public string? NormalizedCustomerRfqReference { get; set; }
    public long? CustomerIdSnapshot { get; set; }
    public long? ContactIdSnapshot { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public string CreatedBy { get; set; } = null!;
    public LeadProcessingPath ProcessingPath { get; set; }
    public bool ExternalAiUsed { get; set; }
    public Models.Lead Lead { get; set; } = null!;
    public LeadIngestionOccurrence EstablishedByOccurrence { get; set; } = null!;
    public ICollection<LeadItemRevision> Items { get; } = new List<LeadItemRevision>();
    public ICollection<LeadRevisionDifference> Differences { get; } = new List<LeadRevisionDifference>();
}

public sealed class LeadItemRevision
{
    public long Id { get; set; }
    public long BusinessUnitId { get; set; }
    public long LeadRevisionId { get; set; }
    public int LineNumber { get; set; }
    public string LineFingerprint { get; set; } = null!;
    public string SnapshotJson { get; set; } = null!;
    public LeadRevision Revision { get; set; } = null!;
}

public sealed class LeadRevisionDifference
{
    public long Id { get; set; }
    public long BusinessUnitId { get; set; }
    public long LeadRevisionId { get; set; }
    public LeadRevisionChangeType ChangeType { get; set; }
    public string Scope { get; set; } = null!;
    public string Path { get; set; } = null!;
    public string? PreviousValueJson { get; set; }
    public string? CurrentValueJson { get; set; }
    public LeadRevision Revision { get; set; } = null!;
}

public sealed class LeadMatchCandidate
{
    public long Id { get; set; }
    public long BusinessUnitId { get; set; }
    public long OccurrenceId { get; set; }
    public long CandidateLeadId { get; set; }
    public decimal Confidence { get; set; }
    public string MatchEvidenceJson { get; set; } = "{}";
    public string DifferencesJson { get; set; } = "[]";
    public string DownstreamImpactJson { get; set; } = "[]";

    /// <summary>
    /// The incoming document's VERBATIM commercial values, held while the match waits for a
    /// human decision.
    ///
    /// <para>
    /// <see cref="DifferencesJson"/> is NOT this. It carries the normalised
    /// <c>Snapshot()</c> on both sides — lowercased, punctuation stripped, five item
    /// properties out of twenty-two — because its only job is to feed a SHA-256 fingerprint
    /// and a field-level diff. Projecting it back onto a canonical <c>Lead</c> rewrote
    /// <c>RFQ-2026/0012</c> as <c>rfq20260012</c> and deleted <c>UnitPrice</c>,
    /// <c>Currency</c>, <c>CustomerRfqno</c> and six other columns outright.
    /// </para>
    ///
    /// <para>
    /// The automatic revision path never had that problem: it still holds the incoming
    /// <c>Lead</c> in memory and copies it field-for-field. The human path resumes in a
    /// later request, after that object is gone, so the verbatim values have to be durable.
    /// This column is that copy. Null on candidates raised before it existed — the decision
    /// path then leaves the canonical record untouched rather than projecting hash text.
    /// </para>
    /// </summary>
    public string? ProposedLeadSnapshotJson { get; set; }
    public LeadMatchReviewState ReviewState { get; set; }
    public string? ReviewedBy { get; set; }
    public DateTimeOffset? ReviewedAtUtc { get; set; }
    public string? ReviewReason { get; set; }
    public int Version { get; set; } = 1;
    public LeadIngestionOccurrence Occurrence { get; set; } = null!;
    public Models.Lead CandidateLead { get; set; } = null!;
}

public sealed class LeadIdentityAuditEvent
{
    public long Id { get; set; }
    public long BusinessUnitId { get; set; }
    public long? LeadId { get; set; }
    public long OccurrenceId { get; set; }
    public string EventType { get; set; } = null!;
    public string PayloadJson { get; set; } = "{}";
    public string ActorType { get; set; } = null!;
    public string ActorId { get; set; } = null!;
    public string CorrelationId { get; set; } = null!;
    public string IdempotencyKey { get; set; } = null!;
    public DateTimeOffset OccurredAtUtc { get; set; }
}

public sealed class LeadRevisionImpact
{
    public long Id { get; set; }
    public long BusinessUnitId { get; set; }
    public long LeadId { get; set; }
    public long LeadRevisionId { get; set; }
    public string AggregateType { get; set; } = null!;
    public long AggregateId { get; set; }
    public string ImpactType { get; set; } = null!;
    public string Status { get; set; } = "OPEN";
    public string DetailsJson { get; set; } = "{}";
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset? ResolvedAtUtc { get; set; }
}
