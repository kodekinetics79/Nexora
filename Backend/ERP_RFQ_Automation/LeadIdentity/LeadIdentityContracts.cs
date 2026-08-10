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
    int? Quantity,
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
}
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
