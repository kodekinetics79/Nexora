using ERP_RFQ_Automation.CommercialRouting;

namespace ERP_RFQ_Automation.Tests.CommercialRouting;

public sealed class DeterministicRoutingEngineTests
{
    private static readonly DateTime Now = new(2026, 7, 21, 12, 0, 0, DateTimeKind.Utc);
    private readonly DeterministicRoutingEngine _engine = new();
    private readonly RoutingPolicy _policy = new() { MatchThreshold = 0.80m, AmbiguityMargin = 0.10m };

    [Fact]
    public void Route_UsesIdentifierPrecedenceBeforeConfidence()
    {
        var request = Request(
            candidates:
            [
                new(7, 100, 1, CustomerIdentifierType.Email, 0.82m),
                new(7, 200, 2, CustomerIdentifierType.CustomerName, 0.99m)
            ],
            ownerships: [Ownership(10, 100, 501, OwnershipScope.GeneralCustomer)],
            availability: [Available(501)]);

        var result = _engine.Route(request, _policy);

        Assert.Equal(100, result.Decision.CustomerId);
        Assert.Equal(501, result.Assignment?.ToUserId);
        Assert.Same(result.Decision, result.Assignment?.RoutingDecision);
    }

    [Fact]
    public void Route_CreatesWorkItemWhenBestTierIsAmbiguous()
    {
        var request = Request(candidates:
        [
            new(7, 100, 1, CustomerIdentifierType.Email, 0.94m),
            new(7, 200, 2, CustomerIdentifierType.Email, 0.89m)
        ]);

        var result = _engine.Route(request, _policy);

        Assert.Equal(CustomerMatchStatus.Ambiguous, result.Decision.MatchStatus);
        Assert.Equal("AMBIGUOUS_CUSTOMER", result.WorkItem?.ReasonCode);
        Assert.Same(result.Decision, result.WorkItem?.RoutingDecision);
        Assert.Null(result.Assignment);
    }

    [Fact]
    public void Route_IgnoresExpiredOwnershipAndUsesMatchingScopePrecedence()
    {
        var request = Request(
            candidates: [new(7, 100, 1, CustomerIdentifierType.ErpAccount, 0.99m)],
            ownerships:
            [
                Ownership(10, 100, 501, OwnershipScope.CustomerException, effectiveTo: Now),
                Ownership(11, 100, 502, OwnershipScope.ProductCategory, "valves"),
                Ownership(12, 100, 503, OwnershipScope.GeneralCustomer)
            ],
            availability: [Available(501), Available(502), Available(503)],
            scopeKeys: new Dictionary<OwnershipScope, string?> { [OwnershipScope.ProductCategory] = "VALVES" });

        var result = _engine.Route(request, _policy);

        Assert.Equal(11, result.Decision.OwnershipId);
        Assert.Equal(502, result.Assignment?.ToUserId);
    }

    [Fact]
    public void Route_FallsBackToAvailableBackup()
    {
        var request = Request(
            candidates: [new(7, 100, 1, CustomerIdentifierType.Email, 0.95m)],
            ownerships: [Ownership(10, 100, 501, OwnershipScope.GeneralCustomer, backupUserId: 502)],
            availability: [Available(501, isAvailable: false), Available(502)]);

        var result = _engine.Route(request, _policy);

        Assert.Equal(RoutingOutcome.AssignedBackup, result.Decision.Outcome);
        Assert.Equal(502, result.Assignment?.ToUserId);
        Assert.Equal(AssignmentScope.LeadOnly, result.Assignment?.AssignmentScope);
    }

    [Fact]
    public void Route_uses_backup_when_measured_workload_exceeds_relief_threshold()
    {
        var request = Request(
            candidates: [new(7, 100, 1, CustomerIdentifierType.Email, 0.95m)],
            ownerships: [Ownership(10, 100, 501, OwnershipScope.GeneralCustomer, backupUserId: 502)],
            availability:
            [
                Available(501, workloadPoints: 70),
                Available(502, workloadPoints: 10)
            ]);

        var result = _engine.Route(request, _policy);

        Assert.Equal(RoutingOutcome.AssignedBackup, result.Decision.Outcome);
        Assert.Equal("BACKUP_OWNER_ASSIGNED_FOR_WORKLOAD", result.Decision.DecisionCode);
        Assert.Equal(502, result.Assignment?.ToUserId);
    }

    [Fact]
    public void Route_QueuesEvidenceBelowConfidenceThreshold()
    {
        var request = Request(candidates:
            [new(7, 100, 1, CustomerIdentifierType.Email, 0.79m)]);

        var result = _engine.Route(request, _policy);

        Assert.Equal(CustomerMatchStatus.BelowThreshold, result.Decision.MatchStatus);
        Assert.Equal("MATCH_BELOW_THRESHOLD", result.WorkItem?.ReasonCode);
    }

    [Fact]
    public void Route_QueuesLeadWhenPrimaryAndBackupAreUnavailable()
    {
        var request = Request(
            candidates: [new(7, 100, 1, CustomerIdentifierType.Email, 0.95m)],
            ownerships: [Ownership(10, 100, 501, OwnershipScope.GeneralCustomer, backupUserId: 502)],
            availability: [Available(501, isAvailable: false), Available(502, isAvailable: false)]);

        var result = _engine.Route(request, _policy);

        Assert.Equal("OWNER_UNAVAILABLE", result.WorkItem?.ReasonCode);
        Assert.Equal(501, result.WorkItem?.SuggestedUserId);
    }

    [Fact]
    public void Route_PreservesExplicitAssignmentScope()
    {
        var request = Request(
            candidates: [new(7, 100, 1, CustomerIdentifierType.Email, 0.95m)],
            ownerships: [Ownership(10, 100, 501, OwnershipScope.GeneralCustomer)],
            availability: [Available(501)]) with
        {
            AssignmentScope = AssignmentScope.CustomerTemporary
        };

        var result = _engine.Route(request, _policy);

        Assert.Equal(AssignmentScope.CustomerTemporary, result.Assignment?.AssignmentScope);
    }

    [Fact]
    public void Route_RejectsCrossTenantEvidenceAndOwnership()
    {
        var request = Request(
            candidates: [new(8, 100, 1, CustomerIdentifierType.Email, 0.99m)],
            ownerships: [Ownership(10, 100, 501, OwnershipScope.GeneralCustomer)],
            availability: [Available(501)]);

        var result = _engine.Route(request, _policy);

        Assert.Equal(CustomerMatchStatus.NoEvidence, result.Decision.MatchStatus);
        Assert.Equal(RoutingOutcome.Unassigned, result.Decision.Outcome);
    }

    [Fact]
    public void Route_IsDeterministicForIdenticalInputAndRetainsIdempotencyKey()
    {
        var request = Request(
            candidates: [new(7, 100, 1, CustomerIdentifierType.Email, 0.95m)],
            ownerships: [Ownership(10, 100, 501, OwnershipScope.GeneralCustomer)],
            availability: [Available(501)]);

        var first = _engine.Route(request, _policy);
        var second = _engine.Route(request, _policy);

        Assert.Equal(first.Decision.CustomerId, second.Decision.CustomerId);
        Assert.Equal(first.Decision.SelectedUserId, second.Decision.SelectedUserId);
        Assert.Equal(first.Decision.DecisionCode, second.Decision.DecisionCode);
        Assert.Equal(first.Decision.Explanation, second.Decision.Explanation);
        Assert.Equal("route-42", first.Decision.IdempotencyKey);
        Assert.Equal("route-42", first.Assignment?.IdempotencyKey);
    }

    [Fact]
    public void Route_RequiresUtcTimestampAndIdempotencyKey()
    {
        var localTime = Request() with { OccurredOn = DateTime.SpecifyKind(Now, DateTimeKind.Local) };
        var noKey = Request() with { IdempotencyKey = " " };

        Assert.Throws<ArgumentException>(() => _engine.Route(localTime, _policy));
        Assert.Throws<ArgumentException>(() => _engine.Route(noKey, _policy));
    }

    private static RoutingRequest Request(
        IReadOnlyCollection<CustomerMatchCandidate>? candidates = null,
        IReadOnlyCollection<CustomerOwnership>? ownerships = null,
        IReadOnlyCollection<RoutingUserAvailability>? availability = null,
        IReadOnlyDictionary<OwnershipScope, string?>? scopeKeys = null) => new(
            7, 42, "route-42", "correlation-42", Now,
            candidates ?? [], ownerships ?? [], availability ?? [], scopeKeys ?? new Dictionary<OwnershipScope, string?>());

    private static CustomerOwnership Ownership(
        long id,
        long customerId,
        long primaryUserId,
        OwnershipScope scope,
        string? scopeKey = null,
        long? backupUserId = null,
        DateTime? effectiveTo = null) => new()
        {
            Id = id,
            BusinessUnitId = 7,
            CustomerId = customerId,
            PrimaryUserId = primaryUserId,
            BackupUserId = backupUserId,
            Scope = scope,
            ScopeKey = scopeKey,
            EffectiveFrom = Now.AddDays(-30),
            EffectiveTo = effectiveTo,
            IsActive = true
        };

    private static RoutingUserAvailability Available(
        long userId, bool isAvailable = true, int workloadPoints = 0) =>
        new(7, userId, IsAvailable: isAvailable, CapacityPercent: Math.Max(0, 100 - workloadPoints),
            Workload: new RoutingWorkloadSnapshot(0, 0, 0, 0, 0, 0, 0, 0, workloadPoints));
}
