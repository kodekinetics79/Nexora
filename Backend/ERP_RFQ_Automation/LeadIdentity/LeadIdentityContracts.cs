using ERP_RFQ_Automation.Models;

namespace ERP_RFQ_Automation.LeadIdentity;

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
    string CustomerResolutionStatus, string? AssignedOpportunityOwner);
public sealed record LeadMatchCandidateDto(long CandidateId, long CandidateLeadId, string NexoraSerial,
    string? CustomerRfqReference, decimal Confidence, string MatchEvidenceJson, string DifferencesJson,
    string DownstreamImpactJson, string ReviewState, int Version);
public sealed record BatchReconciliationDto(Guid BatchId, int FilesReceived, int LogicalInquiries,
    int NewLeads, int ExactDuplicates, int Revisions, int PossibleMatches, int Rejected,
    int ExternalOccurrences, decimal? ExternalCost, IReadOnlyList<BatchReconciliationItemDto> Items);
public sealed record PossibleMatchQueueItemDto(Guid BatchId, long OccurrenceId, string? FileName,
    DateTimeOffset IngestedAtUtc, decimal Confidence, IReadOnlyList<LeadMatchCandidateDto> MatchCandidates);

public sealed record MatchDecisionRequest(string Action, long? CandidateLeadId, int ExpectedVersion,
    string Reason, string IdempotencyKey);
public sealed record LeadIdentityMetricDto(string Key, decimal Value, int Numerator, int? Denominator,
    IReadOnlyList<long> LeadIds, IReadOnlyList<long> OccurrenceIds);
public sealed record LeadIdentityAnalyticsDto(string DefinitionVersion, DateTimeOffset From, DateTimeOffset To,
    DateTimeOffset GeneratedAtUtc, IReadOnlyList<LeadIdentityMetricDto> Metrics);
