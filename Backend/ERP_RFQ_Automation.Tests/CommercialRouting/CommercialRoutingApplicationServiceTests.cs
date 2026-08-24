using ERP_RFQ_Automation.CommercialRouting;
using ERP_RFQ_Automation.CommercialIntelligence.Sales;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace ERP_RFQ_Automation.Tests.CommercialRouting;

public sealed class CommercialRoutingApplicationServiceTests
{
    [Fact]
    public async Task Route_assigns_verified_customer_owner_and_updates_compatibility_state()
    {
        using var db = new TestDb();
        await SeedRoutingGraphAsync(db, includeIdentifier: true, includeOwnership: true);
        await using var context = db.ContextFor(71);
        await AddEligibleProfileAsync(context, 7101);
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
    public async Task Route_rejects_idempotency_key_reuse_with_different_request_content()
    {
        using var db = new TestDb();
        await SeedRoutingGraphAsync(db, includeIdentifier: false, includeOwnership: false);
        await using var context = db.ContextFor(71);
        var service = Service(context);

        await service.RouteLeadAsync(71,
            new RouteLeadCommand(701, "route-content-bound", "corr-original"), CancellationToken.None);

        var conflict = await Assert.ThrowsAsync<RoutingConflictException>(() =>
            service.RouteLeadAsync(71,
                new RouteLeadCommand(701, "route-content-bound", "corr-changed"), CancellationToken.None));

        Assert.Contains("different routing request content", conflict.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Single(await context.Set<LeadRoutingDecision>().ToListAsync());
    }

    [Fact]
    public async Task Route_uses_measured_workload_and_selects_lower_load_backup()
    {
        using var db = new TestDb();
        await SeedRoutingGraphAsync(db, includeIdentifier: true, includeOwnership: true);
        await using (var seed = db.ContextFor(null))
        {
            var first = Seed.Lead(seed, 702, 71, items: Enumerable.Range(1, 10)
                .Select(index => Seed.LeadItem(7200 + index, index.ToString(), 1)));
            first.AssignTo = 7101;
            first.BidClosingDate = DateTime.UtcNow.AddHours(-2);
            var rfq = new Rfq
            {
                Id = 7702,
                Rfqno = "RFQ-WORKLOAD",
                RecDate = DateTime.UtcNow.AddDays(-2),
                LeadId = first.Id,
                CreatedBy = "test",
                CreatedDate = DateTime.UtcNow.AddDays(-2),
                BusinessUnitId = 71
            };
            seed.Rfqs.Add(rfq);
            seed.Quotes.Add(new Quote
            {
                Id = 7802,
                QuoteNo = "QUOTE-WORKLOAD",
                Rfqid = rfq.Id,
                BusinessUnitId = 71,
                QuoteDate = DateTime.UtcNow.AddDays(-1),
                CreatedBy = "test",
                CreatedDate = DateTime.UtcNow.AddDays(-1),
                SentOn = DateTime.UtcNow.AddHours(-8)
            });
            await seed.SaveChangesAsync();
        }
        await using var context = db.ContextFor(71);
        await AddEligibleProfileAsync(context, 7101);
        await AddEligibleProfileAsync(context, 7102);
        var service = Service(context);

        var result = await service.RouteLeadAsync(71,
            new RouteLeadCommand(701, "route-measured-load", "corr-measured-load"), CancellationToken.None);

        Assert.Equal(RoutingOutcome.AssignedBackup, result.Outcome);
        Assert.Equal(7102, result.SelectedUserId);
        Assert.Equal("BACKUP_OWNER_ASSIGNED_FOR_WORKLOAD", result.DecisionCode);
        using var explanation = JsonDocument.Parse(result.Explanation);
        Assert.Equal(64, explanation.RootElement.GetProperty("requestHash").GetString()!.Length);
        var owners = explanation.RootElement.GetProperty("consideredOwners").EnumerateArray().ToArray();
        var primary = owners.Single(owner => owner.GetProperty("UserId").GetInt64() == 7101);
        var backup = owners.Single(owner => owner.GetProperty("UserId").GetInt64() == 7102);
        var workload = primary.GetProperty("workload");
        Assert.Equal(1, workload.GetProperty("ActiveLeadCount").GetInt32());
        Assert.Equal(10, workload.GetProperty("LeadLineCount").GetInt32());
        Assert.Equal(1, workload.GetProperty("OverdueDeadlineCount").GetInt32());
        Assert.Equal(1, workload.GetProperty("OpenRfqCount").GetInt32());
        Assert.Equal(1, workload.GetProperty("OpenQuoteCount").GetInt32());
        Assert.Equal(1, workload.GetProperty("FollowUpCount").GetInt32());
        Assert.Equal(64, workload.GetProperty("WorkloadPoints").GetInt32());
        Assert.Equal(0, backup.GetProperty("workload").GetProperty("WorkloadPoints").GetInt32());
    }

    [Fact]
    public async Task Owner_options_honor_effective_governed_eligibility_and_capacity()
    {
        using var db = new TestDb();
        await SeedRoutingGraphAsync(db, includeIdentifier: false, includeOwnership: false);
        await using var context = db.ContextFor(71);
        var now = DateTime.UtcNow;
        context.SalesRepProfiles.AddRange(
            new SalesRepProfile
            {
                BusinessUnitId = 71, UserId = 7101, IsRoutingEligible = false,
                CapacityPercent = 100, DistributionWeight = 1, EffectiveFromUtc = now.AddDays(-1),
                Version = 1, UpdatedAtUtc = now, UpdatedBy = "test", LastMutationIdempotencyKey = "profile-ineligible"
            },
            new SalesRepProfile
            {
                BusinessUnitId = 71, UserId = 7102, IsRoutingEligible = true,
                CapacityPercent = 40, DistributionWeight = 1, EffectiveFromUtc = now.AddDays(-1),
                Version = 1, UpdatedAtUtc = now, UpdatedBy = "test", LastMutationIdempotencyKey = "profile-capacity"
            });
        await context.SaveChangesAsync();

        var options = await Service(context).GetOwnerOptionsAsync(71, CancellationToken.None);

        var eligible = options.Single(option => option.UserId == 7102);
        Assert.True(eligible.IsAvailable);
        Assert.True(eligible.HasGovernedProfile);
        Assert.Equal(40, eligible.CapacityPercent);
        var ineligible = options.Single(option => option.UserId == 7101);
        Assert.False(ineligible.IsAvailable);
        Assert.Contains("not routing eligible", ineligible.EligibilityReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Owner_options_reject_an_established_owner_without_a_governed_profile()
    {
        using var db = new TestDb();
        await SeedRoutingGraphAsync(db, includeIdentifier: true, includeOwnership: true);
        await using var context = db.ContextFor(71);

        var options = await Service(context).GetOwnerOptionsAsync(71, CancellationToken.None);

        Assert.All(options, option => Assert.False(option.IsAvailable));
        Assert.All(options, option => Assert.Contains("profile is required",
            option.EligibilityReason, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Manual_assignment_rejects_a_stale_expected_assignee()
    {
        using var db = new TestDb();
        await SeedRoutingGraphAsync(db, includeIdentifier: false, includeOwnership: false);
        await using var context = db.ContextFor(71);
        await AddEligibleProfileAsync(context, 7101);
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
    public async Task Manual_assignment_idempotency_is_bound_to_assignee_and_actor_content()
    {
        using var db = new TestDb();
        await SeedRoutingGraphAsync(db, includeIdentifier: false, includeOwnership: false);
        await using var context = db.ContextFor(71);
        await AddEligibleProfileAsync(context, 7101);
        var service = Service(context);
        await service.AssignLeadAsync(71, new ManualAssignLeadCommand(
            701, 7101, 7102, "manual-content-bound", "manual-correlation",
            AssignmentScope.LeadOnly, "first", true, null), CancellationToken.None);

        await Assert.ThrowsAsync<RoutingConflictException>(() => service.AssignLeadAsync(71,
            new ManualAssignLeadCommand(
                701, 7102, 7101, "manual-content-bound", "manual-correlation",
                AssignmentScope.LeadOnly, "changed", true, null), CancellationToken.None));

        Assert.Equal(7101, (await context.Leads.SingleAsync(l => l.Id == 701)).AssignTo);
        Assert.Single(await context.Set<LeadAssignment>().ToListAsync());
    }

    [Fact]
    public async Task Assign_to_me_is_manual_does_not_change_lifecycle_and_blocks_a_stale_version()
    {
        using var db = new TestDb();
        await SeedRoutingGraphAsync(db, includeIdentifier: false, includeOwnership: false);
        await using var context = db.ContextFor(71);
        await AddEligibleProfileAsync(context, 7101);
        var service = Service(context);
        var originalLifecycle = (await context.Leads.AsNoTracking().SingleAsync(l => l.Id == 701)).LeadStatusId;

        var assigned = await service.ChangeLeadOwnershipAsync(71, new ChangeLeadOwnershipCommand(
            701, LeadOwnershipAction.Assign, 7101, 7101, ActorIsManager: false, 1,
            "owner-command-1", "owner-correlation-1"), CancellationToken.None);

        Assert.Equal(7101, assigned.AssignedToUserId);
        Assert.Equal(LeadAssignmentMethods.Manual, assigned.AssignmentMethod);
        Assert.True(assigned.ManualOverride);
        Assert.Equal(originalLifecycle, (await context.Leads.AsNoTracking().SingleAsync(l => l.Id == 701)).LeadStatusId);
        await Assert.ThrowsAsync<RoutingConflictException>(() => service.ChangeLeadOwnershipAsync(71,
            new ChangeLeadOwnershipCommand(701, LeadOwnershipAction.Unassign, null, 7102, ActorIsManager: true, 1,
                "owner-command-stale", "owner-correlation-stale"), CancellationToken.None));
    }

    [Fact]
    public async Task Return_to_automatic_explicitly_releases_the_manual_fence_and_routes_again()
    {
        using var db = new TestDb();
        await SeedRoutingGraphAsync(db, includeIdentifier: true, includeOwnership: true);
        await using var context = db.ContextFor(71);
        await AddEligibleProfileAsync(context, 7101);
        await AddEligibleProfileAsync(context, 7102);
        var service = Service(context);

        await service.ChangeLeadOwnershipAsync(71, new ChangeLeadOwnershipCommand(
            701, LeadOwnershipAction.Assign, 7102, 7102, ActorIsManager: false, 1,
            "owner-self-assign", "owner-self-assign-correlation"), CancellationToken.None);
        var rerouted = await service.ChangeLeadOwnershipAsync(71, new ChangeLeadOwnershipCommand(
            701, LeadOwnershipAction.ReturnToAutomatic, null, 7102, ActorIsManager: false, 2,
            "owner-return-auto", "owner-return-auto-correlation"), CancellationToken.None);

        Assert.Equal(7101, rerouted.AssignedToUserId);
        Assert.Equal(LeadAssignmentMethods.Automatic, rerouted.AssignmentMethod);
        Assert.False(rerouted.ManualOverride);
    }

    [Fact]
    public async Task Automatic_routing_does_not_overwrite_a_manual_owner()
    {
        using var db = new TestDb();
        await SeedRoutingGraphAsync(db, includeIdentifier: true, includeOwnership: true);
        await using var context = db.ContextFor(71);
        await AddEligibleProfileAsync(context, 7101);
        await AddEligibleProfileAsync(context, 7102);
        var service = Service(context);
        await service.ChangeLeadOwnershipAsync(71, new ChangeLeadOwnershipCommand(
            701, LeadOwnershipAction.Assign, 7102, 7101, ActorIsManager: true, 1,
            "owner-manual-fence", "owner-manual-fence-correlation"), CancellationToken.None);

        await Assert.ThrowsAsync<RoutingConflictException>(() => service.RouteLeadAsync(71,
            new RouteLeadCommand(701, "automatic-after-manual", "automatic-after-manual-correlation"),
            CancellationToken.None));

        var lead = await context.Leads.SingleAsync(candidate => candidate.Id == 701);
        Assert.Equal(7102, lead.AssignTo);
        Assert.True(lead.ManualAssignmentOverride);
    }

    [Fact]
    public async Task Ownership_command_rejects_a_cross_tenant_assignee()
    {
        using var db = new TestDb();
        await SeedRoutingGraphAsync(db, includeIdentifier: false, includeOwnership: false);
        await using (var seed = db.ContextFor(null))
        {
            Seed.EnsureBusinessUnit(seed, 72);
            seed.Users.Add(User(7201, 72, "other-tenant"));
            await seed.SaveChangesAsync();
        }
        await using var context = db.ContextFor(71);

        await Assert.ThrowsAsync<RoutingConflictException>(() => Service(context).ChangeLeadOwnershipAsync(71,
            new ChangeLeadOwnershipCommand(701, LeadOwnershipAction.Assign, 7201, 7102, ActorIsManager: true, 1,
                "cross-tenant-owner", "cross-tenant-owner-correlation"), CancellationToken.None));
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
        await AddEligibleProfileAsync(context, 7101);
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
        await AddEligibleProfileAsync(context, 7101);
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
            // Normalised through the platform normaliser rather than hand-typed: organisation
            // names now share ONE normaliser with client resolution (CustomerNameNormalizer),
            // which strips legal/generic tokens such as "TRADING". Hard-coding the key here
            // would make this test assert an obsolete encoding rather than the routing
            // behaviour it is about.
            NormalizedValue = RoutingValueNormalizer.Normalize(CustomerIdentifierType.CustomerName, "Acme Trading"),
            DisplayValue = "Acme Trading",
            IsVerified = true,
            Confidence = confidence,
            Source = "test",
            EffectiveFrom = DateTime.UtcNow.AddDays(-1)
        };
    }

    [Fact]
    public async Task Ownership_creation_rejects_an_overlapping_active_customer_scope()
    {
        using var db = new TestDb();
        await SeedRoutingGraphAsync(db, includeIdentifier: false, includeOwnership: false);
        await using var context = db.ContextFor(71);
        var service = Service(context);
        var starts = DateTime.UtcNow.AddDays(-1);
        var command = new CreateCustomerOwnershipCommand(
            7201, 7101, 7102, OwnershipScope.GeneralCustomer, null, 100,
            starts, null, "test", "primary account owner");

        await service.CreateOwnershipAsync(71, command, CancellationToken.None);

        await Assert.ThrowsAsync<RoutingConflictException>(() => service.CreateOwnershipAsync(
            71, command with { PrimaryUserId = 7102, Reason = "competing owner" }, CancellationToken.None));
        Assert.Single(await context.Set<CustomerOwnership>().ToListAsync());
    }

    [Theory]
    [InlineData(CustomerIdentifierType.Email, " Buyer@Example.COM ", "buyer@example.com")]
    [InlineData(CustomerIdentifierType.Domain, "https://www.Example.com/path", "example.com")]
    [InlineData(CustomerIdentifierType.Phone, "+1 (212) 555-0199", "12125550199")]
    // Organisation names normalise through CustomerNameNormalizer.LooseKey: case and
    // whitespace fold, and legal/generic trade tokens ("TRADING", "LLC", "EST", "CO") are
    // stripped because they carry no identity. One normaliser, so the routing store and the
    // client resolver can never disagree about what "the same company" means.
    [InlineData(CustomerIdentifierType.CustomerName, "  Acme   Trading ", "ACME")]
    [InlineData(CustomerIdentifierType.CustomerName, "Al-Quraishi & Partners Est.", "QURAISHI")]
    [InlineData(CustomerIdentifierType.Alias, "Saudi Electricity Company", "SAUDI ELECTRICITY")]
    public void Normalizer_produces_stable_matching_values(
        CustomerIdentifierType type, string input, string expected) =>
        Assert.Equal(expected, RoutingValueNormalizer.Normalize(type, input));

    private static CommercialRoutingApplicationService Service(ErpRfqAutomationContext context) =>
        new(context, new DeterministicRoutingEngine(), new RoutingPolicy());

    private static async Task AddEligibleProfileAsync(ErpRfqAutomationContext context, long userId)
    {
        var now = DateTime.UtcNow;
        context.SalesRepProfiles.Add(new SalesRepProfile
        {
            BusinessUnitId = 71, UserId = userId, IsRoutingEligible = true,
            CapacityPercent = 100, DistributionWeight = 1, EffectiveFromUtc = now.AddDays(-1),
            Version = 1, UpdatedAtUtc = now, UpdatedBy = "test",
            LastMutationIdempotencyKey = $"eligible-profile-{userId}"
        });
        await context.SaveChangesAsync();
    }

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
