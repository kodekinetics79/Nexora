namespace ERP_RFQ_Automation.CommercialLearning;

public sealed record CurrencyValueSummary(long CurrencyId, string CurrencyCode, decimal? LastValue,
    decimal? MedianValue, decimal? MinimumValue, decimal? MaximumValue, int SampleSize);

public sealed record ProductCommercialMemory(
    long ProductId, string PartNumber, string ProductName, DateTime PeriodFrom, DateTime PeriodTo,
    int TimesRequested, int TimesQuoted, int DecidedCount, int WonCount, int LostCount, int PendingCount,
    decimal? LineWinRatePercent, int StockoutBlockedCount, decimal? TypicalWinningLeadTimeDays,
    ProductWonContext? LastWonContext,
    IReadOnlyCollection<CurrencyValueSummary> WonSellingPrices,
    IReadOnlyCollection<CurrencyValueSummary> SupplierLandedCosts,
    IReadOnlyCollection<CommercialReasonCount> LossReasons,
    IReadOnlyCollection<CommercialEvidenceLink> Evidence);

public sealed record ProductWonContext(long CustomerQuoteId, string CustomerQuoteNumber,
    decimal Quantity, decimal UnitPrice, long CurrencyId, string CurrencyCode,
    int? DeliveryLeadTimeDays, DateTime OutcomeOn);

public sealed record SupplierCommercialEvaluation(long SupplierId, string SupplierName,
    int QuoteRevisions, int SelectedOfferCount, int SupportedWonCount, int CompleteCurrentOfferCount,
    decimal? AverageResponseDays, decimal? AverageReliabilitySnapshot,
    IReadOnlyCollection<CurrencyValueSummary> LandedCosts,
    IReadOnlyCollection<CommercialEvidenceLink> Evidence,
    SupplierBidQualitySummary BidQuality);

public sealed record CustomerCommercialMemory(long CustomerId, string CustomerName,
    int InquiryCount, int QuoteCount, int DecidedCount, int WonCount, int LostCount, int PendingCount,
    decimal? ConversionRatePercent, IReadOnlyCollection<CurrencyValueSummary> WonValues,
    IReadOnlyCollection<CommercialReasonCount> LossReasons,
    IReadOnlyCollection<CommercialEvidenceLink> Evidence);

public sealed record SalesRepCommercialMemory(long SalesRepUserId, string SalesRepName,
    int OwnedOpportunities, int DecidedCount, int WonCount, int LostCount,
    int CommercialConstraintLosses, int CustomerDecisionLosses, int ExecutionReviewLosses,
    int FollowUpsDue, int FollowUpsCompleted, decimal? ConversionRatePercent,
    decimal WeightedCoverage, decimal? FirstMeaningfulActionHours, decimal? QuoteTurnaroundHours,
    decimal? FollowUpCompletionPercent, int InsightCaptureCount, decimal? ValueConversionPercent,
    string CoachingOpportunity,
    IReadOnlyCollection<CommercialEvidenceLink> Evidence);

public sealed record InventoryDemandMemory(long ProductId, string PartNumber, string ProductName,
    decimal ObservedDemand, decimal QualifiedDemand, decimal QuotedDemand,
    decimal ProbabilityWeightedDemand, decimal CommittedDemand, decimal FulfilledDemand,
    int DecidedOpportunities, int WonOpportunities, decimal? ConversionRatePercent,
    bool StockingRecommendationEligible, string Recommendation,
    IReadOnlyCollection<CommercialEvidenceLink> Evidence);

public sealed record SupplierBidQualitySummary(int OfferCount, int CompleteOfferCount, int EligibleOfferCount,
    decimal? CompletenessPercent, int MissingTermCount, int PriceOutlierCount, int RevisionVolatilityCount,
    IReadOnlyCollection<BidQualityFlag> Flags);

public sealed record BidQualityFlag(long SupplierQuotedItemId, string Code, string Severity,
    string Explanation, decimal Confidence, CommercialEvidenceLink Evidence, string ReviewAction);

public sealed record ExplainableRecommendation(string Code, string Label, string Explanation,
    decimal Confidence, bool UserOverrideAllowed, string OverrideAction,
    IReadOnlyCollection<CommercialEvidenceLink> Evidence);

public sealed record RfqLineIntelligence(long RfqItemId, string PartNumber, decimal RequestedQuantity,
    decimal StockQuantity, decimal UnfulfilledQuantity, string FulfilmentRoute, int OfferCount,
    int EligibleOfferCount, IReadOnlyCollection<string> Blockers,
    IReadOnlyCollection<BidQualityFlag> BidQualityFlags);

public sealed record ScenarioQuantityAllocation(long RfqItemId, decimal RequestedQuantity,
    decimal ImmediateStockQuantity, decimal SupplierQuantity, long? SupplierQuotedItemId,
    DateTime? ExpectedDeliveryOn);

public sealed record ScenarioCostSource(string SourceType, string Label, decimal? Amount,
    long? CurrencyId, string Status, CommercialEvidenceLink? Evidence);

public sealed record OpportunityScenario(string Code, string Label, bool Eligible, string Explanation,
    decimal? EstimatedLandedCost, long? CurrencyId, int? EstimatedLeadTimeDays,
    DateTime? ExpectedDeliveryOn, DateTime? ValidUntil, decimal? GrossMarginPercent,
    string RiskBand, string RiskExplanation, decimal Confidence,
    IReadOnlyCollection<ScenarioQuantityAllocation> Quantities,
    IReadOnlyCollection<ScenarioCostSource> CostSources,
    IReadOnlyCollection<string> Assumptions, IReadOnlyCollection<string> ApprovalRequirements,
    IReadOnlyCollection<CommercialEvidenceLink> Evidence, string? CurrencyCode = null);

public sealed record CustomerTargetBridge(long RfqItemId, string Status, decimal CustomerTargetUnitPrice,
    decimal RequiredGrossMarginPercent, decimal? MaximumLandedUnitCost, decimal? FreightDutyTaxOtherPerUnit,
    decimal? MaximumSupplierUnitCost, long CurrencyId, string Formula, CommercialEvidenceLink Evidence);

public sealed record PredictivePriceLine(long RfqItemId, string Status, string Mode,
    decimal? RecommendedUnitPrice, decimal? LastWonUnitPrice, decimal? WinningRangeLow,
    decimal? WinningRangeHigh, decimal? EstimatedWinProbability, int QuoteSampleSize,
    int CustomerOrderSampleSize, decimal? BacktestMeanAbsolutePercentError,
    string Context, IReadOnlyCollection<string> Limitations,
    IReadOnlyCollection<CommercialEvidenceLink> Evidence, int BacktestHoldoutCount = 0,
    decimal? CohortConversionBaseline = null, long? CurrencyId = null, string? CurrencyCode = null);

public sealed record PricingBacktestSummary(string Status, int HoldoutCount,
    decimal? MeanAbsolutePercentError, string Cohort, string Limitation);

public sealed record OpportunityDigitalTwin(DateTime CalculatedOn, string Validity,
    string Mode, string PolicyVersion, IReadOnlyCollection<OpportunityScenario> Scenarios,
    IReadOnlyCollection<CustomerTargetBridge> CustomerTargetBridges,
    IReadOnlyCollection<PredictivePriceLine> PredictivePricing,
    PricingBacktestSummary Backtest, string OverrideAction);

public sealed record RfqCommercialIntelligence(long RfqId, string RfqNumber, string NexoraSerial,
    decimal ReadinessScore, string CommercialDecision, string SlaRisk, bool ClarificationRequired,
    ExplainableRecommendation NextBestAction, IReadOnlyCollection<RfqLineIntelligence> Lines,
    OpportunityDigitalTwin DigitalTwin);

public sealed record CommercialReasonCount(string Code, string Label, int Count);
public sealed record CommercialEvidenceLink(string RecordType, long RecordId, string Reference,
    DateTime? OccurredOn, string Role);

public sealed record CommercialMemoryCard(string NexoraSerial, long RfqId, long RfqItemId,
    ProductCommercialMemory? Product, InventoryDemandMemory? Inventory,
    IReadOnlyCollection<SupplierCommercialEvaluation> Suppliers,
    string NextAction);

public sealed record LearningStudioSummary(DateTime GeneratedAt,
    int ApprovedCorrections, int ConflictingCorrections, int SupplierQuoteTemplates,
    int ProductMemoriesWithDecisions, int ProductMemoriesBelowThreshold,
    IReadOnlyCollection<LearningSignal> RecentSignals);

public sealed record LearningSignal(string SignalId, string SignalType, string Subject, string Value,
    int SampleSize, DateTime LastObservedOn, string Status, string EvidenceReference,
    long GovernanceVersion, string? GovernanceAction, DateTime? GovernedOn, long? GovernedByUserId);

public sealed record LearningGovernanceCommand(long ExpectedVersion, string Reason, long? RevertsVersion = null);

public sealed record LearningGovernanceResult(string SignalId, long Version, string Action,
    string EffectiveStatus, long? RevertsVersion, DateTime OccurredOn, bool IdempotentReplay);
