using ERP_RFQ_Automation.Models;

namespace ERP_RFQ_Automation.LeadIdentity;

/// <summary>
/// Request to establish canonical identity for a Lead that has none.
///
/// <para><b>The type is the guarantee.</b> There is deliberately no field for a content hash,
/// file name, MIME type, sender, subject or receipt time — so a baseline cannot assert that a
/// document arrived, because the caller has no way to say it did. It records the lead's own
/// stored commercial facts and nothing else.</para>
/// </summary>
public sealed record LeadIdentityBaselineRequest(
    string SourceChannel,
    string Reason,
    string ActorType,
    string ActorId,
    string CorrelationId);

public sealed record LeadIntakeDescriptor(
    Guid BatchId,
    string SourceChannel,
    string IdempotencyKey,
    string? ExternalSourceId,
    string? EmailThreadId,
    string? SourceSystem,
    string? Sender,
    string? Subject,
    string? OriginalFileName,
    string? MimeType,
    long? FileSize,
    string? ContentHash,
    long? SourceDocumentId,
    long? ExtractionJobId,
    DateTimeOffset? SourceReceivedAtUtc,
    DateTimeOffset IngestedAtUtc,
    LeadProcessingPath ProcessingPath,
    bool ExternalAiUsed,
    decimal? ExternalCost,
    string ActorType,
    string ActorId,
    string CorrelationId)
{
    public long? SourceDocumentOccurrenceId { get; init; }
    public string? LogicalGroupKey { get; init; }

    /// <summary>
    /// FR-RFQ-05/06: thread-ancestor keys of the message this document arrived in, in occurrence
    /// <c>EmailThreadId</c> form ("email:{Message-Id}") — the union of its In-Reply-To and
    /// References headers as persisted on the EmailIngest row. An occurrence already linked to a
    /// lead whose EmailThreadId appears here is strong evidence the incoming document belongs to
    /// that lead's thread. It is deliberately NEVER sufficient alone — subjects and threads get
    /// reused for unrelated inquiries — so reconciliation pairs it with the same corroboration
    /// the logical-group arm demands. Empty for doors with no mail message.
    /// </summary>
    public IReadOnlyList<string>? ThreadReferencedMessageIds { get; init; }
}

/// <summary>
/// The incoming document's commercial values EXACTLY as extracted — no normalisation, no
/// field pruning. Persisted on <see cref="LeadMatchCandidate.ProposedLeadSnapshotJson"/> so a
/// human match decision taken minutes or days later can apply the buyer's real text instead
/// of the lowercased, punctuation-stripped hash input that feeds the dedup fingerprint.
///
/// <para>The field list is deliberately the union of what the AUTOMATIC revision path
/// preserves (<c>ApplyCurrentProjection</c> for the header, <c>CloneCurrentItem</c> for the
/// lines). If a property is added there it must be added here, or the human path silently
/// starts losing it again.</para>
/// </summary>
public sealed record VerbatimLeadSnapshot(
    string? Rfqno,
    string? BuyersName,
    DateTime? RecDate,
    DateTime? BidClosingDate,
    string? HeaderRemarks,
    int? NoOfLineItems,
    string? Rfqtype,
    DateTime? AcknowledgmentDate,
    DateTime? SubmissionDate,
    string? OpportunityNo,
    string? DurationAgreement,
    DateTime? RequiredDeliveryDate,
    string? DeliveryLocation,
    string? BidClosingDateHijri,
    string? AgreementReference,
    string? InquiryType,
    IReadOnlyList<VerbatimLeadItemSnapshot> Items);

public sealed record VerbatimLeadItemSnapshot(
    string? CompanyRef,
    string? CustomerAccountPortalId,
    string? CustomerRfqno,
    string? ItemMaterialCode,
    string? CommodityProduct,
    string? BuyerName,
    string? LineItemNo,
    string? ProductShortName,
    string? Alternative,
    string? ProductShortDescription,
    string? Currency,
    string? UnitOfMeasure,
    decimal? UnitPrice,

    // Nullable in lockstep with LeadItem.Quantity: a verbatim snapshot that turned "the buyer
    // stated no quantity" into 0 would restore a demand for zero units on amendment.
    decimal? Quantity,
    string? StorageLocation,
    string? ManufacturerName,
    string? ManufacturerPartNumber,
    string? AlternateProductName,
    string? AlternatePartNumber,
    string? ItemText,
    string? MaterialPotext,
    int? LeadTime,
    DateTime? ReceivedDate,
    DateTime? BidClosingDateLine,
    decimal? Aiconfidence,
    string? ExtraFields);

public sealed record LeadReconciliationResult(
    long LeadId,
    string NexoraSerial,
    long OccurrenceId,
    long? RevisionId,
    int RevisionNumber,
    LeadOccurrenceClassification Classification,
    decimal Confidence,
    IReadOnlyList<string> Reasons,
    bool ShouldRoute);

public sealed record LeadRevisionDto(long Id, int RevisionNumber, DateTimeOffset CreatedAtUtc,
    string Fingerprint, string? CustomerRfqReference, string ProcessingPath, bool ExternalAiUsed,
    IReadOnlyList<LeadRevisionDifferenceDto> Differences, IReadOnlyList<LeadRevisionImpactDto> Impacts,
    int ChangedLineCount, int ModifiedLineCount);
public sealed record LeadRevisionDifferenceDto(string ChangeType, string Scope, string Path, string? PreviousValueJson, string? CurrentValueJson);
public sealed record LeadRevisionImpactDto(string AggregateType, long AggregateId, string ImpactType, string Status, string DetailsJson);

public sealed record BatchReconciliationItemDto(long OccurrenceId, long? LeadId, string? NexoraSerial,
    string Classification, int? RevisionNumber, string? FileName, DateTimeOffset IngestedAtUtc,
    string ProcessingPath, bool ExternalAiUsed, decimal Confidence, IReadOnlyList<string> Reasons,
    IReadOnlyList<LeadMatchCandidateDto> MatchCandidates,
    string CustomerResolutionStatus, string? AssignedOpportunityOwner,
    string IntakeStatus, string? ErrorCode, long? SourceDocumentOccurrenceId)
{
    public string? SecurityStatus { get; init; }
    public DateTimeOffset? SecurityScanUpdatedAtUtc { get; init; }
    public DateTimeOffset? LastUpdatedAtUtc { get; init; }
    public string? ExtractionStatus { get; init; }
    public DateTimeOffset? ExtractionUpdatedAtUtc { get; init; }

    /// <summary>
    /// True when this file is held by a malware scanner that never produced a verdict, so it can be
    /// replayed from its immutable source without a re-upload. This is the durable "our
    /// infrastructure failed" signal — distinct from a real malware detection
    /// (<c>ErrorCode = malware_detected</c>), a macro-enabled document
    /// (<c>macro_enabled_document</c>) or any other terminal inspection verdict
    /// (<c>document_rejected</c>), none of which is ever replayable.
    ///
    /// <para>
    /// <c>document_rejected</c> is a BUCKET, so it never explains anything on its own: the specific
    /// reason travels in <see cref="BatchReconciliationItemDto.Reasons"/>, read straight out of the
    /// occurrence's <c>last_error_details</c>. Any surface rendering the code must render that
    /// reason too rather than guessing at the cause.
    /// </para>
    /// </summary>
    public bool RecoverableSecurityHold { get; init; }

    /// <summary>
    /// For an <c>ExactDuplicate</c>: the inquiry this file is a byte-for-byte repeat of, so the
    /// batch page can name it and open it instead of spinning on "Identifying the customer" for a
    /// document that will never be read. Null when the original was never reconciled into a lead
    /// (a hash match against a file that itself stopped at intake).
    /// </summary>
    public DuplicateOfDto? DuplicateOf { get; init; }

    /// <summary>The customer's own RFQ number on the lead this row belongs to (Lead.Rfqno).</summary>
    public string? CustomerReference { get; init; }

    /// <summary>
    /// For a <c>Revision</c>: what the new document changed, compacted from
    /// <c>LeadRevisionDifferences</c> into line/field/from/to so the batch page can say
    /// "line 2 qty 20→35" instead of "One inquiry is ready". Empty when nothing commercial moved.
    /// </summary>
    public IReadOnlyList<LeadRevisionChangeDto> Changes { get; init; } = Array.Empty<LeadRevisionChangeDto>();

    /// <summary>
    /// Set when the lead this row belongs to has already become an RFQ. A converted lead is never
    /// offered "Decide" again; the page sends the rep to the RFQ instead.
    /// </summary>
    public LeadRfqLinkDto? Rfq { get; init; }
}

/// <summary>The inquiry an exact-duplicate upload repeats, in the words the batch page prints.</summary>
public sealed record DuplicateOfDto(long LeadId, string? RfqNo, string? NexoraSerial, string? CustomerName, string? OwnerName);

/// <summary>
/// One thing a revision changed. <c>Line</c> is the customer's line number (null for a header
/// field); <c>Field</c> is in a salesperson's words ("qty", "unit", "part", "closing date");
/// <c>From</c>/<c>To</c> are plain values, null when the line itself was added or removed.
/// </summary>
public sealed record LeadRevisionChangeDto(string? Line, string Field, string? From, string? To);

/// <summary>The RFQ a lead became, and whose desk it is on.</summary>
public sealed record LeadRfqLinkDto(long RfqId, string RfqNo, string? OwnerName);

public sealed record LeadMatchCandidateDto(long CandidateId, long CandidateLeadId, string NexoraSerial,
    string? CustomerRfqReference, decimal Confidence, string MatchEvidenceJson, string DifferencesJson,
    string DownstreamImpactJson, string ReviewState, int Version);
public sealed record BatchReconciliationDto(Guid BatchId, int FilesReceived, int LogicalInquiries,
    int NewLeads, int ExactDuplicates, int Revisions, int PossibleMatches, int Rejected,
    int ExternalOccurrences, decimal? ExternalCost, IReadOnlyList<BatchReconciliationItemDto> Items)
{
    public int AwaitingSecurityScan { get; init; }
    public int LocalFirstOccurrences { get; init; }
}
public sealed record PossibleMatchQueueItemDto(Guid BatchId, long OccurrenceId, string? FileName,
    DateTimeOffset IngestedAtUtc, decimal Confidence, IReadOnlyList<LeadMatchCandidateDto> MatchCandidates);

public sealed record DuplicateResourceAccountingDto(
    long BytesUploaded,
    long HashingDurationMs,
    long StoragePhysicalBytes,
    long StorageLogicalBytes,
    bool MalwareScanReused,
    bool MalwareScanRerun,
    bool ParserReused,
    bool OcrReused,
    bool LocalModelReused,
    bool ExternalModelReused,
    decimal LocalComputeCost,
    decimal ExternalCost,
    decimal TotalActualCost,
    decimal EstimatedProcessingAvoided,
    string CostStatus);

public sealed record DuplicateUploadDto(
    long OccurrenceId,
    string FileName,
    Guid UploadBatch,
    DateTimeOffset IngestedAt,
    string UploadedBy,
    string Source,
    string DuplicateType,
    long? OriginalOccurrenceId,
    long? CanonicalLeadId,
    string? NexoraSerial,
    string SecurityStatus,
    bool ProcessingReused,
    DuplicateResourceAccountingDto Resources,
    IReadOnlyList<string> Actions);

public sealed record MatchDecisionRequest(string Action, long? CandidateLeadId, int ExpectedVersion,
    string Reason, string IdempotencyKey);
public sealed record LeadIdentityMetricDto(string Key, decimal Value, int Numerator, int? Denominator,
    IReadOnlyList<long> LeadIds, IReadOnlyList<long> OccurrenceIds);
public sealed record LeadIdentityAnalyticsDto(string DefinitionVersion, DateTimeOffset From, DateTimeOffset To,
    DateTimeOffset GeneratedAtUtc, IReadOnlyList<LeadIdentityMetricDto> Metrics);
