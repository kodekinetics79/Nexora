namespace ERP_RFQ_Automation.CommercialIntelligence.Opportunity;

public static class OpportunityPriorityMode
{
    public const string Shadow = "Shadow";
}

public static class OpportunityFeedbackDecision
{
    public const string Accepted = "Accepted";
    public const string Rejected = "Rejected";
    public const string Replaced = "Replaced";
    public const string Deferred = "Deferred";
    public const string Reverted = "Reverted";

    public static readonly string[] All = [Accepted, Rejected, Replaced, Deferred, Reverted];
}

public static class OpportunityOutcomeCode
{
    public const string OrderCreated = "ORDER_CREATED";
    public const string QuoteWon = "QUOTE_WON";
    public const string QuoteLost = "QUOTE_LOST";
    public const string QuoteExpired = "QUOTE_EXPIRED";

    public static readonly string[] All = [OrderCreated, QuoteWon, QuoteLost, QuoteExpired];
}

public sealed class OpportunityRecommendation
{
    public long Id { get; set; }
    public long BusinessUnitId { get; set; }
    public long CommercialCaseId { get; set; }
    public string NexoraSerial { get; set; } = string.Empty;
    public long LeadId { get; set; }
    public long LeadVersion { get; set; }
    public string RecommendationKey { get; set; } = string.Empty;
    public string PolicyVersion { get; set; } = string.Empty;
    public string FeatureSchemaVersion { get; set; } = string.Empty;
    public DateTime EvidenceCutoffAtUtc { get; set; }
    public string EvidenceSnapshotJson { get; set; } = "{}";
    public string EvidenceHash { get; set; } = string.Empty;
    public int PriorityScore { get; set; }
    public string PriorityBand { get; set; } = string.Empty;
    public decimal Confidence { get; set; }
    public decimal Completeness { get; set; }
    public int SampleSize { get; set; }
    public string RecommendedActionCode { get; set; } = string.Empty;
    public string RecommendedActionLabel { get; set; } = string.Empty;
    public decimal? ExpectedCommercialValue { get; set; }
    public string? ExpectedCommercialValueCurrency { get; set; }
    public string ComponentsJson { get; set; } = null!;
    public string RationaleJson { get; set; } = "[]";
    public string CohortKey { get; set; } = string.Empty;
    public string Mode { get; set; } = OpportunityPriorityMode.Shadow;
    public DateTime GeneratedAtUtc { get; set; }
    public long? SupersedesRecommendationId { get; set; }

    public Models.CommercialCase CommercialCase { get; set; } = null!;
    public Models.Lead Lead { get; set; } = null!;
    public OpportunityRecommendation? SupersedesRecommendation { get; set; }
}

public sealed class OpportunityOutcome
{
    public long Id { get; set; }
    public long BusinessUnitId { get; set; }
    public long OpportunityRecommendationId { get; set; }
    public string OutcomeCode { get; set; } = string.Empty;
    public DateTime ObservedAtUtc { get; set; }
    public string SourceType { get; set; } = string.Empty;
    public long SourceId { get; set; }
    public long SourceVersion { get; set; }
    public string EvidenceJson { get; set; } = "{}";
    public string EvidenceHash { get; set; } = string.Empty;
    public string CorrelationId { get; set; } = string.Empty;

    public OpportunityRecommendation Recommendation { get; set; } = null!;
}

public sealed class OpportunityFeedback
{
    public long Id { get; set; }
    public long BusinessUnitId { get; set; }
    public long OpportunityRecommendationId { get; set; }
    public string Decision { get; set; } = string.Empty;
    public string? ReplacementActionCode { get; set; }
    public string Reason { get; set; } = string.Empty;
    public string ActorId { get; set; } = string.Empty;
    public DateTime OccurredAtUtc { get; set; }
    public string IdempotencyKey { get; set; } = string.Empty;
    public string CorrelationId { get; set; } = string.Empty;
    public long? SupersedesFeedbackId { get; set; }

    public OpportunityRecommendation Recommendation { get; set; } = null!;
    public OpportunityFeedback? SupersedesFeedback { get; set; }
}

public sealed class OpportunityEvent
{
    public long Id { get; set; }
    public long BusinessUnitId { get; set; }
    public long OpportunityRecommendationId { get; set; }
    public string EventType { get; set; } = string.Empty;
    public string SourceType { get; set; } = string.Empty;
    public long SourceId { get; set; }
    public string ActorId { get; set; } = string.Empty;
    public DateTime OccurredAtUtc { get; set; }
    public string CorrelationId { get; set; } = string.Empty;
    public string IdempotencyKey { get; set; } = string.Empty;
    public string RequestHash { get; set; } = string.Empty;
    public string PayloadJson { get; set; } = "{}";

    public OpportunityRecommendation Recommendation { get; set; } = null!;
    public OpportunityOutbox OutboxMessage { get; set; } = null!;
}

public sealed class OpportunityOutbox
{
    public long Id { get; set; }
    public long BusinessUnitId { get; set; }
    public long OpportunityEventId { get; set; }
    public string EventType { get; set; } = string.Empty;
    public string PayloadJson { get; set; } = "{}";
    public DateTime OccurredAtUtc { get; set; }
    public DateTime AvailableAtUtc { get; set; }
    public DateTime? ProcessedAtUtc { get; set; }
    public int AttemptCount { get; set; }
    public string? LastError { get; set; }

    public OpportunityEvent Event { get; set; } = null!;
}

public sealed class OpportunityOperation
{
    public long Id { get; set; }
    public long BusinessUnitId { get; set; }
    public string OperationType { get; set; } = string.Empty;
    public long? CommercialCaseId { get; set; }
    public long? OpportunityRecommendationId { get; set; }
    public string IdempotencyKey { get; set; } = string.Empty;
    public string RequestHash { get; set; } = string.Empty;
    public string CorrelationId { get; set; } = string.Empty;
    public string ActorId { get; set; } = string.Empty;
    public string ResultJson { get; set; } = "{}";
    public DateTime OccurredAtUtc { get; set; }

    public OpportunityRecommendation? Recommendation { get; set; }
}
