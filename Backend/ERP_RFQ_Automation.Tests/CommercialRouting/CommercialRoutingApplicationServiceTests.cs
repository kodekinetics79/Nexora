using ERP_RFQ_Automation.CommercialRouting;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Tests.CommercialRouting;

public sealed class CommercialRoutingApplicationServiceTests
{
    [Fact]
    public async Task Route_assigns_verified_customer_owner_and_updates_compatibility_state()
    {
        using var db = new TestDb();
        await SeedRoutingGraphAsync(db, includeIdentifier: true, includeOwnership: true);
        await using var context = db.ContextFor(71);
        var service = Service(context);

        var result = await service.RouteLeadAsync(71,
            new RouteLeadCommand(701, "route-701", "corr-701"), CancellationToken.None);

        Assert.Equal(RoutingOutcome.AssignedPrimary, result.Outcome);
        Assert.Equal(7101, result.SelectedUserId);
        Assert.NotNull(result.AssignmentId);
        Assert.Null(result.WorkItemId);
        Assert.Equal(7101, (await context.Leads.SingleAsync(l => l.Id == 701)).AssignTo);
        var assignment = await context.Set<LeadAssignment>().SingleAsync();
        Assert.Equal("PRIMARY_OWNER_ASSIGNED", assignment.ReasonCode);
        Assert.Null(assignment.EffectiveTo);
    }

    [Fact]
    public async Task Route_without_evidence_creates_one_durable_queue_item_and_replay_is_idempotent()
    {
        using var db = new TestDb();
        await SeedRoutingGraphAsync(db, includeIdentifier: false, includeOwnership: false);
        await using var context = db.ContextFor(71);
        var service = Service(context);
        var command = new RouteLeadCommand(701, "route-no-match", "corr-no-match");

        var first = await service.RouteLeadAsync(71, command, CancellationToken.None);
        var replay = await service.RouteLeadAsync(71, command, CancellationToken.None);

        Assert.Equal(first.DecisionId, replay.DecisionId);
        Assert.Equal(CustomerMatchStatus.NoEvidence, first.MatchStatus);
        Assert.NotNull(first.WorkItemId);
        Assert.Single(await context.Set<LeadRoutingDecision>().ToListAsync());
        var item = Assert.Single(await context.Set<UnassignedWorkItem>().ToListAsync());
        Assert.Equal(WorkItemStatus.Open, item.Status);
        Assert.Equal(1, item.Version);
    }

    [Fact]
    public async Task Manual_assignment_rejects_a_stale_expected_assignee()
    {
        using var db = new TestDb();
        await SeedRoutingGraphAsync(db, includeIdentifier: false, includeOwnership: false);
        await using var context = db.ContextFor(71);
        var service = Service(context);
        await service.AssignLeadAsync(71, new ManualAssignLeadCommand(
            701, 7101, 7102, "manual-1", "corr-1", AssignmentScope.LeadOnly,
            null, true, null), CancellationToken.None);

        var stale = new ManualAssignLeadCommand(
            701, 7102, 7101, "manual-2", "corr-2", AssignmentScope.LeadOnly,
            null, true, null);

        await Assert.ThrowsAsync<RoutingConflictException>(() =>
            service.AssignLeadAsync(71, stale, CancellationToken.None));
        Assert.Equal(7101, (await context.Leads.SingleAsync(l => l.Id == 701)).AssignTo);
    }

    [Fact]
    public async Task Queue_claim_uses_version_and_prevents_an_active_lease_takeover()
    {
        using var db = new TestDb();
        await SeedRoutingGraphAsync(db, includeIdentifier: false, includeOwnership: false);
        await using var context = db.ContextFor(71);
        var service = Service(context);
        var routed = await service.RouteLeadAsync(71,
            new RouteLeadCommand(701, "queue-route", "queue-corr"), CancellationToken.None);

        var claimed = await service.ClaimAsync(71, routed.WorkItemId!.Value,
            new QueueLeaseCommand(1, 7101, 20), CancellationToken.None);

        Assert.Equal(WorkItemStatus.Claimed, claimed.Status);
        Assert.Equal(2, claimed.Version);
        await Assert.ThrowsAsync<RoutingConflictException>(() => service.ClaimAsync(
            71, claimed.Id, new QueueLeaseCommand(2, 7102, 20), CancellationToken.None));
        await Assert.ThrowsAsync<RoutingConflictException>(() => service.ReleaseAsync(
            71, claimed.Id, new QueueReleaseCommand(1, 7101), CancellationToken.None));
    }

    [Fact]
    public async Task Queue_assignment_resolves_work_and_creates_assignment_history_atomically()
    {
        using var db = new TestDb();
        await SeedRoutingGraphAsync(db, includeIdentifier: false, includeOwnership: false);
        await using var context = db.ContextFor(71);
        var service = Service(context);
        var routed = await service.RouteLeadAsync(71,
            new RouteLeadCommand(701, "queue-for-assignment", "corr-queue"), CancellationToken.None);

        var assigned = await service.AssignQueueItemAsync(71, routed.WorkItemId!.Value,
            new AssignQueueItemCommand(1, 7101, 7102, "queue-assign", "corr-assign"),
            CancellationToken.None);

        Assert.Equal(RoutingOutcome.AssignedPrimary, assigned.Outcome);
        var item = await context.Set<UnassignedWorkItem>().SingleAsync();
        Assert.Equal(WorkItemStatus.Resolved, item.Status);
        Assert.Equal("MANUALLY_ASSIGNED", item.ResolutionCode);
        Assert.Equal(7101, (await context.Leads.SingleAsync(l => l.Id == 701)).AssignTo);
    }

    [Fact]
    public async Task Customer_profile_is_not_visible_across_tenants()
    {
        using var db = new TestDb();
        await SeedRoutingGraphAsync(db, includeIdentifier: true, includeOwnership: true);
        await using var context = db.ContextFor(72);
        var service = Service(context);

        Assert.Null(await service.GetCustomerProfileAsync(72, 7201, CancellationToken.None));
    }

    [Fact]
    public async Task Bulk_assignment_returns_a_result_for_each_success_and_conflict()
    {
        using var db = new TestDb();
        await SeedRoutingGraphAsync(db, includeIdentifier: false, includeOwnership: false);
        await using var context = db.ContextFor(71);
        var service = Service(context);
        var routed = await service.RouteLeadAsync(71,
            new RouteLeadCommand(701, "bulk-route", "bulk-route-corr"), CancellationToken.None);

        var results = await service.BulkAssignQueueAsync(71, new BulkAssignQueueCommand(
            [new(routed.WorkItemId!.Value, 1), new(999999, 1)],
            7101, 7102, "bulk-assignment", "bulk-correlation"), CancellationToken.None);

        Assert.Collection(results,
            success => Assert.True(success.Succeeded),
            failure =>
            {
                Assert.False(failure.Succeeded);
                Assert.Contains("not found", failure.Error, StringComparison.OrdinalIgnoreCase);
            });
    }

    [Fact]
    public async Task Soft_identifiers_can_produce_an_ambiguous_customer_queue_item()
    {
        using var db = new TestDb();
        await SeedRoutingGraphAsync(db, includeIdentifier: false, includeOwnership: false);
        await using (var seed = db.ContextFor(null))
        {
            Seed.Customer(seed, 7202, 71, "Acme Trading");
            seed.Set<CustomerIdentifier>().AddRange(
                Identifier(7351, 7201, 0.94m),
                Identifier(7352, 7202, 0.89m));
            await seed.SaveChangesAsync();
        }
        await using var context = db.ContextFor(71);
        var service = Service(context);

        var result = await service.RouteLeadAsync(71,
            new RouteLeadCommand(701, "ambiguous-route", "ambiguous-corr"), CancellationToken.None);

        Assert.Equal(CustomerMatchStatus.Ambiguous, result.MatchStatus);
        Assert.Equal("AMBIGUOUS_CUSTOMER", result.DecisionCode);
        Assert.NotNull(result.WorkItemId);

        static CustomerIdentifier Identifier(long id, long customerId, decimal confidence) => new()
        {
            Id = id,
            BusinessUnitId = 71,
            CustomerId = customerId,
            IdentifierType = CustomerIdentifierType.CustomerName,
            NormalizedValue = "ACME TRADING",
            DisplayValue = "Acme Trading",
            IsVerified = true,
            Confidence = confidence,
            Source = "test",
            EffectiveFrom = DateTime.UtcNow.AddDays(-1)
        };
    }

    [Theory]
    [InlineData(CustomerIdentifierType.Email, " Buyer@Example.COM ", "buyer@example.com")]
    [InlineData(CustomerIdentifierType.Domain, "https://www.Example.com/path", "example.com")]
    [InlineData(CustomerIdentifierType.Phone, "+1 (212) 555-0199", "12125550199")]
    [InlineData(CustomerIdentifierType.CustomerName, "  Acme   Trading ", "ACME TRADING")]
    public void Normalizer_produces_stable_matching_values(
        CustomerIdentifierType type, string input, string expected) =>
        Assert.Equal(expected, RoutingValueNormalizer.Normalize(type, input));

    private static CommercialRoutingApplicationService Service(ErpRfqAutomationContext context) =>
        new(context, new DeterministicRoutingEngine(), new RoutingPolicy());

    private static async Task SeedRoutingGraphAsync(TestDb db, bool includeIdentifier, bool includeOwnership)
    {
        await using var context = db.ContextFor(null);
        var lead = Seed.Lead(context, 701, 71, buyersName: "Acme Trading");
        lead.Clientemail = "buyer@acme.example";
        Seed.Customer(context, 7201, 71, "Acme Trading");
        context.Users.AddRange(User(7101, 71, "owner"), User(7102, 71, "manager"));
        await context.SaveChangesAsync();

        if (includeIdentifier)
        {
            context.Set<CustomerIdentifier>().Add(new CustomerIdentifier
            {
                Id = 7301,
                BusinessUnitId = 71,
                CustomerId = 7201,
                IdentifierType = CustomerIdentifierType.Email,
                NormalizedValue = "buyer@acme.example",
                DisplayValue = "buyer@acme.example",
                IsVerified = true,
                Confidence = 0.99m,
                Source = "test",
                EffectiveFrom = DateTime.UtcNow.AddDays(-1)
            });
        }
        if (includeOwnership)
        {
            context.Set<CustomerOwnership>().Add(new CustomerOwnership
            {
                Id = 7401,
                BusinessUnitId = 71,
                CustomerId = 7201,
                PrimaryUserId = 7101,
                BackupUserId = 7102,
                Scope = OwnershipScope.GeneralCustomer,
                Priority = 100,
                EffectiveFrom = DateTime.UtcNow.AddDays(-1),
                IsActive = true,
                Source = "test",
                Version = 1
            });
        }
        await context.SaveChangesAsync();
    }

    private static User User(long id, long businessUnitId, string name) => new()
    {
        Id = id,
        FirstName = name,
        LastName = "User",
        Email = $"{name}@example.com",
        PasswordHash = "not-used",
        ImageUrl = "n/a",
        Buid = businessUnitId,
        IsActive = true,
        CreatedBy = "test",
        CreatedOn = DateTime.UtcNow
    };
}
