namespace ERP_RFQ_Automation.CommercialRouting;

public sealed record RoutingPolicy
{
    public string Version { get; init; } = "1";
    public decimal MatchThreshold { get; init; } = 0.85m;
    public decimal AmbiguityMargin { get; init; } = 0.10m;
    public TimeSpan UnassignedSla { get; init; } = TimeSpan.FromHours(4);

    public IReadOnlyList<CustomerIdentifierType> IdentifierPrecedence { get; init; } =
    [
        CustomerIdentifierType.ErpAccount,
        CustomerIdentifierType.TaxRegistration,
        CustomerIdentifierType.Email,
        CustomerIdentifierType.Domain,
        CustomerIdentifierType.Phone,
        CustomerIdentifierType.Alias,
        CustomerIdentifierType.CustomerName,
        CustomerIdentifierType.HistoricalInference
    ];

    public IReadOnlyList<OwnershipScope> OwnershipPrecedence { get; init; } =
    [
        OwnershipScope.CustomerException,
        OwnershipScope.ProductCategory,
        OwnershipScope.Branch,
        OwnershipScope.Territory,
        OwnershipScope.KeyAccountTeam,
        OwnershipScope.GeneralCustomer
    ];

    public void Validate()
    {
        if (MatchThreshold is < 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(MatchThreshold));
        if (AmbiguityMargin is < 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(AmbiguityMargin));
        if (UnassignedSla <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(UnassignedSla));
        if (IdentifierPrecedence.Count == 0 || IdentifierPrecedence.Distinct().Count() != IdentifierPrecedence.Count)
            throw new ArgumentException("Identifier precedence must contain unique values.");
        if (OwnershipPrecedence.Count == 0 || OwnershipPrecedence.Distinct().Count() != OwnershipPrecedence.Count)
            throw new ArgumentException("Ownership precedence must contain unique values.");
    }
}

public sealed record CustomerMatchCandidate(
    long BusinessUnitId,
    long CustomerId,
    long IdentifierId,
    CustomerIdentifierType IdentifierType,
    decimal Confidence,
    bool IsVerified = true);

public sealed record RoutingUserAvailability(
    long BusinessUnitId,
    long UserId,
    bool IsActive = true,
    bool IsAvailable = true,
    int CapacityPercent = 100);

public sealed record RoutingRequest(
    long BusinessUnitId,
    long LeadId,
    string IdempotencyKey,
    string CorrelationId,
    DateTime OccurredOn,
    IReadOnlyCollection<CustomerMatchCandidate> MatchCandidates,
    IReadOnlyCollection<CustomerOwnership> Ownerships,
    IReadOnlyCollection<RoutingUserAvailability> UserAvailability,
    IReadOnlyDictionary<OwnershipScope, string?> ScopeKeys,
    AssignmentScope AssignmentScope = AssignmentScope.LeadOnly);

public sealed record RoutingResult(
    LeadRoutingDecision Decision,
    LeadAssignment? Assignment,
    UnassignedWorkItem? WorkItem);
