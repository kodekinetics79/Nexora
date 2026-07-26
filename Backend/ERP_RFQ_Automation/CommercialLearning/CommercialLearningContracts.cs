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
    IReadOnlyCollection<CommercialEvidenceLink> Evidence);

public sealed record CustomerCommercialMemory(long CustomerId, string CustomerName,
    int InquiryCount, int QuoteCount, int DecidedCount, int WonCount, int LostCount, int PendingCount,
    decimal? ConversionRatePercent, IReadOnlyCollection<CurrencyValueSummary> WonValues,
    IReadOnlyCollection<CommercialReasonCount> LossReasons,
    IReadOnlyCollection<CommercialEvidenceLink> Evidence);

public sealed record SalesRepCommercialMemory(long SalesRepUserId, string SalesRepName,
    int OwnedOpportunities, int DecidedCount, int WonCount, int LostCount,
    int CommercialConstraintLosses, int CustomerDecisionLosses, int ExecutionReviewLosses,
    int FollowUpsDue, int FollowUpsCompleted, decimal? ConversionRatePercent,
    IReadOnlyCollection<CommercialEvidenceLink> Evidence);

public sealed record InventoryDemandMemory(long ProductId, string PartNumber, string ProductName,
    decimal ObservedDemand, decimal QualifiedDemand, decimal QuotedDemand,
    decimal ProbabilityWeightedDemand, decimal CommittedDemand, decimal FulfilledDemand,
    int DecidedOpportunities, int WonOpportunities, decimal? ConversionRatePercent,
    bool StockingRecommendationEligible, string Recommendation,
    IReadOnlyCollection<CommercialEvidenceLink> Evidence);

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

public sealed record LearningSignal(string SignalType, string Subject, string Value,
    int SampleSize, DateTime LastObservedOn, string Status, string EvidenceReference);
