namespace ERP_RFQ_Automation.CommercialIntelligence.Sales;

public enum CommercialActivityType
{
    OpportunityCreated,
    Call,
    Email,
    Meeting,
    Note,
    QuoteSent,
    CustomerResponded,
    FollowUpCompleted,
    Won,
    Lost
}

public enum FollowUpStatus
{
    Open,
    InProgress,
    Completed,
    Cancelled
}

public enum SalesContributionType
{
    Sourced,
    Owned,
    Assisted,
    Closed
}

public sealed class SalesRepProfile
{
    public long Id { get; set; }
    public long BusinessUnitId { get; set; }
    public long UserId { get; set; }
    public bool IsRoutingEligible { get; set; } = true;
    public int CapacityPercent { get; set; } = 100;
    public decimal DistributionWeight { get; set; } = 1m;
    public string[] TerritoryKeys { get; set; } = [];
    public string[] ProductCategoryKeys { get; set; } = [];
    public DateTime EffectiveFromUtc { get; set; }
    public DateTime? EffectiveToUtc { get; set; }
    public long Version { get; set; } = 1;
    public DateTime UpdatedAtUtc { get; set; }
    public string UpdatedBy { get; set; } = string.Empty;
    public string LastMutationIdempotencyKey { get; set; } = string.Empty;
}

public sealed class SalesTeamMembership
{
    public long Id { get; set; }
    public long BusinessUnitId { get; set; }
    public long UserId { get; set; }
    public long TeamId { get; set; }
    public bool IsPrimary { get; set; }
    public DateTime EffectiveFromUtc { get; set; }
    public DateTime? EffectiveToUtc { get; set; }
    public long Version { get; set; } = 1;
}

public sealed class CommercialActivity
{
    public long Id { get; set; }
    public long BusinessUnitId { get; set; }
    public long SalesRepUserId { get; set; }
    public CommercialActivityType ActivityType { get; set; }
    public string AggregateType { get; set; } = string.Empty;
    public long AggregateId { get; set; }
    public long? CustomerId { get; set; }
    public long? LeadAssignmentId { get; set; }
    public DateTime OccurredAtUtc { get; set; }
    public string OutcomeCode { get; set; } = string.Empty;
    public string EvidenceReference { get; set; } = string.Empty;
    public string ActorId { get; set; } = string.Empty;
    public string CorrelationId { get; set; } = string.Empty;
    public string IdempotencyKey { get; set; } = string.Empty;
}

public sealed class FollowUpTask
{
    public long Id { get; set; }
    public long BusinessUnitId { get; set; }
    public long AssignedToUserId { get; set; }
    public string AggregateType { get; set; } = string.Empty;
    public long AggregateId { get; set; }
    public long? CustomerId { get; set; }
    public DateTime DueAtUtc { get; set; }
    public FollowUpStatus Status { get; set; } = FollowUpStatus.Open;
    public int Priority { get; set; }
    public string PurposeCode { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public long Version { get; set; } = 1;
    public string CreatedBy { get; set; } = string.Empty;
    public string CorrelationId { get; set; } = string.Empty;
    public string CreationIdempotencyKey { get; set; } = string.Empty;
}

public sealed class FollowUpTransitionEvent
{
    public long Id { get; set; }
    public long BusinessUnitId { get; set; }
    public long FollowUpTaskId { get; set; }
    public FollowUpStatus FromStatus { get; set; }
    public FollowUpStatus ToStatus { get; set; }
    public long FromVersion { get; set; }
    public long ToVersion { get; set; }
    public DateTime OccurredAtUtc { get; set; }
    public string ActorId { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    public string CorrelationId { get; set; } = string.Empty;
    public string IdempotencyKey { get; set; } = string.Empty;
}

public sealed class SalesContribution
{
    public long Id { get; set; }
    public long BusinessUnitId { get; set; }
    public long SalesRepUserId { get; set; }
    public SalesContributionType ContributionType { get; set; }
    public string AggregateType { get; set; } = string.Empty;
    public long AggregateId { get; set; }
    public long? CustomerId { get; set; }
    public decimal ContributionPercent { get; set; }
    public decimal? RevenueAmount { get; set; }
    public string? CurrencyCode { get; set; }
    public DateTime RecognizedAtUtc { get; set; }
    public string EvidenceReference { get; set; } = string.Empty;
    public string ActorId { get; set; } = string.Empty;
    public string CorrelationId { get; set; } = string.Empty;
    public string IdempotencyKey { get; set; } = string.Empty;
}
