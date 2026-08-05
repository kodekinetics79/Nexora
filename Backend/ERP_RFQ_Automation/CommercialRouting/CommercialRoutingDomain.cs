namespace ERP_RFQ_Automation.CommercialRouting;

public enum CustomerIdentifierType
{
    ErpAccount,
    TaxRegistration,
    Email,
    Domain,
    Phone,
    Alias,
    CustomerName,
    HistoricalInference,

    // ── Client-organisation identity (additive, string-converted, ≤32 chars) ──
    // Deliberately OUTSIDE the UX_customer_identifiers_authoritative filter: these are
    // LEARNED, not authoritative, so several customers may legitimately share one value
    // and the resolver reports AMBIGUOUS rather than guessing.

    /// <summary>Buying portal / document template ("MATERIALS E-BIDDING SYSTEM", "Ariba", "Etimad").</summary>
    Portal,

    /// <summary>"PORTAL|OUR-VENDOR-CODE" — the pair is unique per customer; the code alone is not.</summary>
    PortalAccount,

    /// <summary>Regex shape of the customer's RFQ numbering ("^C\d{9}$"). Suggestion-grade only.</summary>
    RfqNumberPattern
}

public enum OwnershipScope
{
    CustomerException,
    ProductCategory,
    Branch,
    Territory,
    KeyAccountTeam,
    GeneralCustomer
}

public enum AssignmentScope
{
    LeadOnly,
    CustomerPermanent,
    CustomerTemporary,
    Branch,
    ProductCategory,
    SharedBackup
}

public enum CustomerMatchStatus
{
    Matched,
    Ambiguous,
    BelowThreshold,
    NoEvidence
}

public enum RoutingOutcome
{
    AssignedPrimary,
    AssignedBackup,
    Unassigned
}

public enum WorkItemStatus
{
    Open,
    Claimed,
    Resolved,
    Cancelled
}

public sealed class CustomerIdentifier
{
    public long Id { get; set; }
    public long BusinessUnitId { get; set; }
    public long CustomerId { get; set; }
    public CustomerIdentifierType IdentifierType { get; set; }
    public string NormalizedValue { get; set; } = string.Empty;
    public string DisplayValue { get; set; } = string.Empty;
    public bool IsVerified { get; set; }
    public decimal Confidence { get; set; }
    public string Source { get; set; } = string.Empty;
    public DateTime EffectiveFrom { get; set; }
    public DateTime? EffectiveTo { get; set; }

    // ── Learning provenance (client-organisation identity) ───────────────────
    // Which human correction taught this edge. Without it a later reversal cannot
    // find and expire what a now-known-wrong review asserted (safeguard P5).

    /// <summary>The lead whose review taught this identifier. Null for profile-synced rows.</summary>
    public long? LearnedFromLeadId { get; set; }

    /// <summary>The LeadReviewAudit row that carries the before/after image of that correction.</summary>
    public long? LearnedFromReviewAuditId { get; set; }

    /// <summary>How many times a human has re-confirmed this identifier. Never decremented.</summary>
    public int ObservationCount { get; set; } = 1;

    public DateTime? LastObservedOn { get; set; }
}

public sealed class CustomerOwnership
{
    public long Id { get; set; }
    public long BusinessUnitId { get; set; }
    public long CustomerId { get; set; }
    public long PrimaryUserId { get; set; }
    public long? BackupUserId { get; set; }
    public OwnershipScope Scope { get; set; }
    public string? ScopeKey { get; set; }
    public int Priority { get; set; }
    public DateTime EffectiveFrom { get; set; }
    public DateTime? EffectiveTo { get; set; }
    public bool IsActive { get; set; } = true;
    public string Source { get; set; } = string.Empty;
    public string? Reason { get; set; }
    public string? MutationIdempotencyKey { get; set; }
    public long Version { get; set; }
}

public sealed class LeadRoutingDecision
{
    public long Id { get; set; }
    public long BusinessUnitId { get; set; }
    public long LeadId { get; set; }
    public long? CustomerId { get; set; }
    public long? MatchedIdentifierId { get; set; }
    public long? OwnershipId { get; set; }
    public long? SuggestedUserId { get; set; }
    public long? SelectedUserId { get; set; }
    public CustomerMatchStatus MatchStatus { get; set; }
    public RoutingOutcome Outcome { get; set; }
    public decimal MatchConfidence { get; set; }
    public string DecisionCode { get; set; } = string.Empty;
    public string Explanation { get; set; } = string.Empty;
    public string PolicyVersion { get; set; } = string.Empty;
    public string CorrelationId { get; set; } = string.Empty;
    public string IdempotencyKey { get; set; } = string.Empty;
    public DateTime CreatedOn { get; set; }
}

public sealed class LeadAssignment
{
    public long Id { get; set; }
    public long BusinessUnitId { get; set; }
    public long LeadId { get; set; }
    public long? FromUserId { get; set; }
    public long ToUserId { get; set; }
    public AssignmentScope AssignmentScope { get; set; }
    public long? OwnershipId { get; set; }
    public long RoutingDecisionId { get; set; }
    public LeadRoutingDecision RoutingDecision { get; set; } = null!;
    public string ReasonCode { get; set; } = string.Empty;
    public string? Comment { get; set; }
    public DateTime EffectiveFrom { get; set; }
    public DateTime? EffectiveTo { get; set; }
    public long? AssignedByUserId { get; set; }
    public string CorrelationId { get; set; } = string.Empty;
    public string IdempotencyKey { get; set; } = string.Empty;
}

public sealed class UnassignedWorkItem
{
    public long Id { get; set; }
    public long BusinessUnitId { get; set; }
    public long LeadId { get; set; }
    public long RoutingDecisionId { get; set; }
    public LeadRoutingDecision RoutingDecision { get; set; } = null!;
    public string QueueType { get; set; } = "Unassigned";
    public string ReasonCode { get; set; } = string.Empty;
    public WorkItemStatus Status { get; set; } = WorkItemStatus.Open;
    public int Priority { get; set; }
    public DateTime EnteredOn { get; set; }
    public DateTime SlaDueOn { get; set; }
    public long? SuggestedCustomerId { get; set; }
    public long? SuggestedUserId { get; set; }
    public decimal MatchConfidence { get; set; }
    public string RequiredAction { get; set; } = string.Empty;
    public long? ClaimedByUserId { get; set; }
    public DateTime? ClaimedUntil { get; set; }
    public DateTime? ResolvedOn { get; set; }
    public string? ResolutionCode { get; set; }
    public string IdempotencyKey { get; set; } = string.Empty;
    public long Version { get; set; }
}
