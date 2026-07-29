namespace ERP_RFQ_Automation.CommercialIntelligence.Growth;

public static class GrowthIntelligencePolicy
{
    public const string Version = "growth-intelligence-v2.5";
    public const int FirstResponseHours = 24;
    public const int QuoteFollowUpDays = 3;
    public const decimal WeakQuoteCoveragePercent = 60m;
    public const int MinimumCoverageSample = 5;
    public const int RepeatedExecutionLossCount = 3;
    public const int DormancyDays = 90;
    public const int MinimumDormantCustomerCohort = 5;
}

public sealed record GrowthAccessScope(long UserId, bool IsManager, long? RequestedSalesRepUserId = null)
{
    public long? EffectiveRepId => IsManager ? RequestedSalesRepUserId : UserId;
}

public sealed record EvidenceFact(string Code, string Value, string SourceType, long SourceId,
    DateTime? ObservedAtUtc = null);

public sealed record CoachingAcknowledgementView(long Id, string DecisionCode, string Reason,
    long ManagerUserId, DateTime CreatedAtUtc, string PolicyVersion);

public sealed record CoachingFinding(
    string FindingKey,
    string Code,
    long SalesRepUserId,
    long? OwnerUserId,
    IReadOnlyCollection<long> ContributorUserIds,
    string SourceAggregateType,
    long SourceAggregateId,
    string SourceAggregateVersion,
    long? CustomerId,
    string? NexoraSerial,
    decimal? ObservedValue,
    decimal? ThresholdValue,
    int SampleSize,
    decimal Confidence,
    DateTime GeneratedAtUtc,
    string Recommendation,
    string Route,
    IReadOnlyCollection<EvidenceFact> Evidence,
    CoachingAcknowledgementView? LatestAcknowledgement);

public sealed record RecoveryRow(
    string RecoveryKey,
    string Code,
    long? SalesRepUserId,
    long? CustomerId,
    string? NexoraSerial,
    string SourceAggregateType,
    long SourceAggregateId,
    string SourceAggregateVersion,
    string Summary,
    string RecommendedAction,
    string Route,
    DateTime GeneratedAtUtc,
    IReadOnlyCollection<EvidenceFact> Evidence);

public sealed record CoachingRecoveryWorkspace(
    string PolicyVersion,
    DateTime PeriodFromUtc,
    DateTime PeriodToUtc,
    DateTime GeneratedAtUtc,
    long? SalesRepScope,
    IReadOnlyCollection<CoachingFinding> CoachingFindings,
    IReadOnlyCollection<RecoveryRow> RecoveryRows,
    DataCompleteness DataCompleteness);

public sealed record DataCompleteness(bool IsComplete, IReadOnlyCollection<string> IncompleteSources);

public sealed record PeriodComparison(DateTime FromUtc, DateTime ToUtc,
    DateTime PreviousFromUtc, DateTime PreviousToUtc);

public sealed record TrendMetric(int CurrentCount, int PreviousCount, decimal? TrendPercent);

public sealed record QuoteFunnelMetric(int EligibleRfqCount, int QuotedRfqCount,
    decimal? CoveragePercent, int DecidedQuoteCount, int WonQuoteCount, decimal? ConversionPercent);

public sealed record AcceptedPriceEvidence(long QuoteId, long QuoteItemId, string QuoteNumber,
    long CurrencyId, string CurrencyCode, decimal Quantity, decimal UnitPrice, DateTime AcceptedAtUtc,
    string EvidenceType, long EvidenceId, string Route);

public sealed record MarginAvailability(string Status, decimal? Percent, int SampleSize, string Reason);

public sealed record RevisionBurden(int LeadRevisionCount, int LeadChangedFieldCount,
    int LeadComparedFieldCount, int LeadChangedLineCount, int LeadComparedLineCount,
    int QuoteRevisionCount, decimal? ChangedFieldPercent, decimal? ChangedLinePercent);

public sealed record FollowUpHealth(int DueCount, int OverdueCount, int CompletedCount,
    int RespondedCount, decimal? ResponsePercent);

public sealed record HealthAssessment(string Band, IReadOnlyCollection<string> Reasons,
    string PolicyVersion, int EvidenceSampleSize);

public sealed record AccountOpportunity(long RfqId, string RfqNumber, string? NexoraSerial,
    long? SalesRepUserId, DateTime ReceivedAtUtc, DateTime? DecisionDeadlineUtc,
    bool HasCustomerQuote, string Route);

public sealed record NextGrowthAction(string Code, string Label, string Route,
    string PolicyVersion, IReadOnlyCollection<EvidenceFact> Evidence);

public sealed record CustomerHealth(
    string PolicyVersion,
    long CustomerId,
    string CustomerName,
    DateTime GeneratedAtUtc,
    PeriodComparison Period,
    TrendMetric RfqTrend,
    QuoteFunnelMetric QuoteFunnel,
    IReadOnlyCollection<AcceptedPriceEvidence> AcceptedPrices,
    MarginAvailability Margin,
    RevisionBurden RevisionBurden,
    FollowUpHealth FollowUp,
    DateTime? LastCommercialActivityAtUtc,
    HealthAssessment Health,
    IReadOnlyCollection<AccountOpportunity> Opportunities,
    NextGrowthAction? NextAction,
    DataCompleteness DataCompleteness);

public sealed record AcknowledgeCoachingFindingCommand(
    string FindingKey,
    string DecisionCode,
    string Reason,
    long ManagerUserId,
    string IdempotencyKey,
    string CorrelationId,
    DateTime PeriodFromUtc,
    DateTime PeriodToUtc,
    DateTime AsOfUtc,
    long? SalesRepUserId = null);

public sealed record FirstResponseObservation(long AssignmentId, long SalesRepUserId,
    DateTime AssignedAtUtc, DateTime? FirstQualifyingResponseAtUtc);

public sealed record QuoteCoverageObservation(long SalesRepUserId, int EligibleRfqCount,
    int QuotedRfqCount);

public sealed record DormantCustomerObservation(long CustomerId, long CurrencyId,
    decimal HistoricalAcceptedValue, DateTime LastCommercialActivityAtUtc);
