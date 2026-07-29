namespace ERP_RFQ_Automation.CommercialIntelligence.Opportunity;

public sealed record OpportunityPriorityAccessScope(bool TenantWide, long? OwnerUserId)
{
    public static OpportunityPriorityAccessScope ForTenant() => new(true, null);

    public static OpportunityPriorityAccessScope ForOwner(long ownerUserId)
        => ownerUserId > 0
            ? new OpportunityPriorityAccessScope(false, ownerUserId)
            : throw new ArgumentOutOfRangeException(nameof(ownerUserId));
}

public sealed record OpportunityPriorityQuery(
    int PageNumber = 1,
    int PageSize = 25,
    string? PriorityBand = null,
    bool InsufficientEvidenceOnly = false);

public sealed record OpportunityPriorityItem(
    long RecommendationId,
    long CommercialCaseId,
    string NexoraSerial,
    long LeadId,
    long? OwnerUserId,
    string? OwnerName,
    int Rank,
    int PriorityScore,
    string PriorityBand,
    decimal Confidence,
    decimal Completeness,
    int SampleSize,
    bool InsufficientEvidence,
    string RecommendedActionCode,
    string RecommendedActionLabel,
    IReadOnlyList<string> Reasons,
    string EvidenceJson,
    DateTime EvidenceCutoffAtUtc,
    DateTime GeneratedAtUtc,
    string PolicyVersion,
    string FeatureSchemaVersion,
    string Mode,
    bool IsCurrent,
    IReadOnlyList<OpportunityOutcomeItem> Outcomes,
    OpportunityFeedbackItem? LatestFeedback);

public sealed record OpportunityPriorityPage(
    IReadOnlyList<OpportunityPriorityItem> Items,
    int Total,
    int PageNumber,
    int PageSize,
    DateTime GeneratedAtUtc,
    string AccessScope,
    string PolicyVersion,
    string Mode,
    OpportunityPriorityCohort Cohort);

public sealed record OpportunityPriorityCohort(
    int CurrentRecommendations,
    int EligibleRecommendations,
    int InsufficientEvidenceRecommendations,
    int RecommendationsWithObservedOutcome,
    int RecommendationsWithFeedback,
    decimal? AccuracyPercent,
    string AccuracyStatus);

public sealed record OpportunityOutcomeItem(
    long Id,
    string OutcomeCode,
    DateTime ObservedAtUtc,
    string SourceType,
    long SourceId,
    long SourceVersion,
    string EvidenceJson);

public sealed record OpportunityFeedbackItem(
    long Id,
    string Decision,
    string? ReplacementActionCode,
    string Reason,
    string ActorId,
    DateTime OccurredAtUtc,
    long? SupersedesFeedbackId);

public sealed record ReconcileOpportunityPrioritiesCommand(
    string CorrelationId,
    string IdempotencyKey,
    string ActorId,
    long? AfterCommercialCaseId = null,
    int BatchSize = 100);

public sealed record ReconcileOpportunityPrioritiesResult(
    int Evaluated,
    int Created,
    int Replayed,
    int OutcomesRecorded,
    int TerminalCasesSkipped,
    bool HasMore,
    long? NextAfterCommercialCaseId,
    DateTime ReconciledAtUtc,
    string PolicyVersion,
    string Mode);

public sealed record RecordOpportunityFeedbackCommand(
    long ExpectedRecommendationId,
    string Decision,
    string? ReplacementActionCode,
    string Reason,
    long? SupersedesFeedbackId,
    string CorrelationId,
    string IdempotencyKey,
    string ActorId,
    bool IsManager);

public interface IOpportunityPriorityApplicationService
{
    Task<OpportunityPriorityPage> QueryAsync(
        long businessUnitId,
        OpportunityPriorityQuery query,
        OpportunityPriorityAccessScope scope,
        CancellationToken cancellationToken);

    Task<OpportunityPriorityItem> GetForCommercialCaseAsync(
        long businessUnitId,
        long commercialCaseId,
        OpportunityPriorityAccessScope scope,
        CancellationToken cancellationToken);

    Task<ReconcileOpportunityPrioritiesResult> ReconcileAsync(
        long businessUnitId,
        ReconcileOpportunityPrioritiesCommand command,
        CancellationToken cancellationToken);

    Task<OpportunityFeedbackItem> RecordFeedbackAsync(
        long businessUnitId,
        long recommendationId,
        RecordOpportunityFeedbackCommand command,
        OpportunityPriorityAccessScope scope,
        CancellationToken cancellationToken);
}

public sealed class OpportunityPriorityNotFoundException(string message) : Exception(message);

public sealed class OpportunityPriorityConflictException(string message) : Exception(message);
