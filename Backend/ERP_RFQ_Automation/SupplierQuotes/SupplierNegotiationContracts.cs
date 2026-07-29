namespace ERP_RFQ_Automation.SupplierQuotes;

public static class SupplierNegotiationRecommendationCodes
{
    public const string BestAndFinalPrice = "BEST_AND_FINAL_PRICE";
    public const string QuantityBreak = "QUANTITY_BREAK";
    public const string FasterDelivery = "FASTER_DELIVERY";
    public const string FreightInclusiveOffer = "FREIGHT_INCLUSIVE_OFFER";
    public const string ImprovedPaymentTerms = "IMPROVED_PAYMENT_TERMS";
    public const string PartialImmediateAvailability = "PARTIAL_IMMEDIATE_AVAILABILITY";
    public const string ApprovedAlternate = "APPROVED_ALTERNATE";
}

public static class SupplierBidQualityFlagCodes
{
    public const string MissingValidity = "MISSING_VALIDITY";
    public const string UnconfirmedStock = "UNCONFIRMED_STOCK";
    public const string IncompleteCommercialTerms = "INCOMPLETE_COMMERCIAL_TERMS";
    public const string UnrealisticLeadTime = "UNREALISTIC_LEAD_TIME";
    public const string PriceOutlier = "PRICE_OUTLIER";
    public const string UnusualCurrency = "UNUSUAL_CURRENCY";
    public const string RevisionVolatility = "REVISION_VOLATILITY";
    public const string PostSelectionPriceIncrease = "POST_SELECTION_PRICE_INCREASE";
    public const string SuspiciousAlternate = "SUSPICIOUS_ALTERNATE";
    public const string MissingAuthorization = "MISSING_AUTHORIZATION";
    public const string StaleEvidence = "STALE_EVIDENCE";
    public const string PoorResponseCompleteness = "POOR_RESPONSE_COMPLETENESS";

    public static readonly IReadOnlyCollection<string> All =
    [
        MissingValidity, UnconfirmedStock, IncompleteCommercialTerms, UnrealisticLeadTime,
        PriceOutlier, UnusualCurrency, RevisionVolatility, PostSelectionPriceIncrease,
        SuspiciousAlternate, MissingAuthorization, StaleEvidence, PoorResponseCompleteness
    ];
}

public sealed record SupplierNegotiationCommand(
    long BusinessUnitId,
    long SupplierQuoteId,
    long ExpectedQuoteVersion,
    string RecommendationCode,
    string Disposition,
    string Reason,
    string IdempotencyKey,
    string Actor,
    string CorrelationId);

public sealed record SupplierNegotiationWorkspace(
    long SupplierQuoteId,
    long SupplierQuoteRevisionId,
    int RevisionNumber,
    long QuoteVersion,
    string Mode,
    string PolicyVersion,
    SupplierNegotiationRound CurrentRound,
    IReadOnlyCollection<string> EvaluatedCategories,
    IReadOnlyCollection<SupplierBidQualityFlag> BidFlags,
    IReadOnlyCollection<SupplierNegotiationRecommendation> Recommendations,
    IReadOnlyCollection<SupplierNegotiationDecisionView> PriorDecisions,
    int PriorDecisionTotal,
    bool PriorDecisionsTruncated);

public sealed record SupplierNegotiationRound(
    int RoundNumber,
    string CurrencyCode,
    DateTime? ValidUntil,
    string? Incoterms,
    string? PaymentTerms,
    decimal FreightAmount,
    decimal TaxAmount,
    DateTime CapturedOn);

public sealed record SupplierBidQualityFlag(
    string Code,
    string Severity,
    bool Blocking,
    string Explanation,
    decimal Confidence,
    int SampleSize,
    IReadOnlyCollection<string> Evidence);

public sealed record SupplierNegotiationRecommendation(
    string Code,
    string Title,
    string Rationale,
    int SampleSize,
    decimal Confidence,
    IReadOnlyCollection<string> Evidence,
    IReadOnlyCollection<string> Limitations,
    string Mode = "SHADOW");

public sealed record SupplierNegotiationDecisionView(
    long DecisionId,
    long SupplierQuoteRevisionId,
    string RecommendationCode,
    string Disposition,
    string Reason,
    string PolicyVersion,
    long ExpectedQuoteVersion,
    string Actor,
    DateTime DecidedOn,
    string CorrelationId,
    bool Replayed = false,
    long? ResultingQuoteVersion = null);
