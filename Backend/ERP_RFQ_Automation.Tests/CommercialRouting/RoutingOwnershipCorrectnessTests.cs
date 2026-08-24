using System.Security.Claims;
using System.Text.Json;
using ERP_RFQ_Automation.Authorization;
using ERP_RFQ_Automation.CommercialIntelligence.Sales;
using ERP_RFQ_Automation.CommercialRouting;
using ERP_RFQ_Automation.Controllers;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Tests.CommercialRouting;

/// <summary>
/// What happens to a lead when a human takes it, hands it back, or is refused — and what happens
/// to one nobody can be worked out for at all.
///
/// <para>Four defects, one theme: ownership changes that left a lead somewhere no person and no
/// process would ever look again.</para>
/// </summary>
public sealed class RoutingOwnershipCorrectnessTests
{
    private const long Tenant = 96_000;
    private const long Owner = 96_001;
    private const long Colleague = 96_002;
    private const long Manager = 96_003;
    private const long Lead = 96_010;

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // P0-1  "Unassign" must not strand a lead
    // ─────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The whole defect in one assertion. Unassign wrote a routing decision and NO queue row, so
    /// the lead had no owner, appeared on no queue, and the reconciliation worker skipped it
    /// (it only reconsiders leads with no routing decision at all). It existed in the database
    /// and nowhere else.
    /// </summary>
    [Fact]
    public async Task An_unassigned_lead_lands_on_the_queue_a_human_actually_looks_at()
    {
        using var database = new TestDb();
        await using var context = await SeedAsync(database);
        var routing = Routing(context);
        await TakeAsync(routing, Owner, version: 1);

        await routing.ChangeLeadOwnershipAsync(Tenant, new ChangeLeadOwnershipCommand(
            Lead, LeadOwnershipAction.Unassign, null, Owner, ActorIsManager: false,
            2, "release-1", "release-corr-1"), default);

        var queued = Assert.Single(await context.Set<UnassignedWorkItem>()
            .Where(item => item.LeadId == Lead && item.Status == WorkItemStatus.Open)
            .ToListAsync());
        Assert.Equal(DeterministicRoutingEngine.ManuallyUnassignedCode, queued.ReasonCode);
        // The line a person reads is a sentence, not the audit code beside it.
        Assert.Equal("Assign an eligible owner", queued.RequiredAction);

        // And it is genuinely on the queue the screens read, not merely in the table.
        var page = await routing.GetQueueAsync(Tenant, null, null, false, 1, 25, default);
        Assert.Contains(page.Items, item => item.LeadId == Lead);
    }

    /// <summary>
    /// Unassign used to set ManualAssignmentOverride, which permanently fences automatic routing
    /// off the lead — RouteLeadAsync throws on it and nothing ever clears it. A lead handed back to
    /// the pool could therefore never be routed again, by anyone, ever.
    /// </summary>
    [Fact]
    public async Task An_unassigned_lead_can_still_be_routed_again()
    {
        using var database = new TestDb();
        await using var context = await SeedAsync(database);
        var routing = Routing(context);
        await TakeAsync(routing, Owner, version: 1);

        await routing.ChangeLeadOwnershipAsync(Tenant, new ChangeLeadOwnershipCommand(
            Lead, LeadOwnershipAction.Unassign, null, Owner, ActorIsManager: false,
            2, "release-2", "release-corr-2"), default);

        Assert.False((await context.Leads.AsNoTracking().SingleAsync(x => x.Id == Lead))
            .ManualAssignmentOverride);
        // The proof that the fence is down: the router accepts the lead instead of refusing it.
        var rerouted = await routing.RouteLeadAsync(Tenant,
            new RouteLeadCommand(Lead, "reroute-after-release", "reroute-corr"), default);
        Assert.Equal(RoutingOutcome.Unassigned, rerouted.Outcome);
    }

    /// <summary>
    /// The queue carries a unique index over (tenant, lead) for live rows, so a second row is not
    /// merely untidy — it is a 23505 at the moment a user clicks Unassign. Releasing a lead that is
    /// already queued must supersede, not duplicate.
    /// </summary>
    [Fact]
    public async Task Releasing_a_lead_that_is_already_queued_replaces_its_queue_row()
    {
        using var database = new TestDb();
        await using var context = await SeedAsync(database);
        var routing = Routing(context);
        await routing.RouteLeadAsync(Tenant, new RouteLeadCommand(Lead, "route-first", "corr-first"), default);
        var lead = await context.Leads.AsNoTracking().SingleAsync(x => x.Id == Lead);

        await routing.ChangeLeadOwnershipAsync(Tenant, new ChangeLeadOwnershipCommand(
            Lead, LeadOwnershipAction.Unassign, null, Owner, ActorIsManager: true,
            lead.AssignmentVersion, "release-3", "release-corr-3"), default);

        var rows = await context.Set<UnassignedWorkItem>().Where(item => item.LeadId == Lead).ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.Single(rows, item => item.Status == WorkItemStatus.Open);
        Assert.Single(rows, item => item.Status == WorkItemStatus.Cancelled);
    }

    /// <summary>
    /// The control that keeps the two release actions distinguishable. "Return to automatic
    /// routing" re-runs the engine in the same call, so it must NOT also park a row on the queue —
    /// only Unassign waits for a person.
    /// </summary>
    [Fact]
    public async Task Returning_a_lead_to_automatic_routing_does_not_park_it_on_the_queue_first()
    {
        using var database = new TestDb();
        await using var context = await SeedAsync(database, withOwnershipFor: Owner);
        var routing = Routing(context);
        await TakeAsync(routing, Owner, version: 1);

        var result = await routing.ChangeLeadOwnershipAsync(Tenant, new ChangeLeadOwnershipCommand(
            Lead, LeadOwnershipAction.ReturnToAutomatic, null, Owner, ActorIsManager: false,
            2, "return-1", "return-corr-1"), default);

        Assert.Equal(Owner, result.AssignedToUserId);
        Assert.Equal(LeadAssignmentMethods.Automatic, result.AssignmentMethod);
        Assert.Empty(await context.Set<UnassignedWorkItem>()
            .Where(item => item.LeadId == Lead && item.Status == WorkItemStatus.Open).ToListAsync());
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // P0-2  the same act needs the same authority whichever screen it is done from
    // ─────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_rep_can_take_a_lead_that_nobody_owns()
    {
        using var database = new TestDb();
        await using var context = await SeedAsync(database);

        var result = await Routing(context).ChangeLeadOwnershipAsync(Tenant, new ChangeLeadOwnershipCommand(
            Lead, LeadOwnershipAction.Assign, Owner, Owner, ActorIsManager: false,
            1, "take-1", "take-corr-1"), default);

        Assert.Equal(Owner, result.AssignedToUserId);
    }

    [Fact]
    public async Task A_rep_can_hand_back_a_lead_they_own()
    {
        using var database = new TestDb();
        await using var context = await SeedAsync(database);
        var routing = Routing(context);
        await TakeAsync(routing, Owner, version: 1);

        var result = await routing.ChangeLeadOwnershipAsync(Tenant, new ChangeLeadOwnershipCommand(
            Lead, LeadOwnershipAction.Unassign, null, Owner, ActorIsManager: false,
            2, "handback-1", "handback-corr-1"), default);

        Assert.Null(result.AssignedToUserId);
    }

    [Fact]
    public async Task A_rep_cannot_take_a_lead_that_belongs_to_a_colleague()
    {
        using var database = new TestDb();
        await using var context = await SeedAsync(database);
        var routing = Routing(context);
        await TakeAsync(routing, Owner, version: 1);

        var refusal = await Assert.ThrowsAsync<RoutingForbiddenException>(() =>
            routing.ChangeLeadOwnershipAsync(Tenant, new ChangeLeadOwnershipCommand(
                Lead, LeadOwnershipAction.Assign, Colleague, Colleague, ActorIsManager: false,
                2, "steal-1", "steal-corr-1"), default));

        Assert.Equal(Owner, (await context.Leads.AsNoTracking().SingleAsync(x => x.Id == Lead)).AssignTo);
        AssertSpeaksEnglish(refusal.Message);
    }

    [Fact]
    public async Task A_rep_cannot_release_a_lead_that_belongs_to_a_colleague()
    {
        using var database = new TestDb();
        await using var context = await SeedAsync(database);
        var routing = Routing(context);
        await TakeAsync(routing, Owner, version: 1);

        await Assert.ThrowsAsync<RoutingForbiddenException>(() =>
            routing.ChangeLeadOwnershipAsync(Tenant, new ChangeLeadOwnershipCommand(
                Lead, LeadOwnershipAction.Unassign, null, Colleague, ActorIsManager: false,
                2, "release-theirs", "release-theirs-corr"), default));

        Assert.Equal(Owner, (await context.Leads.AsNoTracking().SingleAsync(x => x.Id == Lead)).AssignTo);
    }

    [Fact]
    public async Task A_rep_cannot_hand_an_unowned_lead_to_a_colleague()
    {
        using var database = new TestDb();
        await using var context = await SeedAsync(database);

        await Assert.ThrowsAsync<RoutingForbiddenException>(() =>
            Routing(context).ChangeLeadOwnershipAsync(Tenant, new ChangeLeadOwnershipCommand(
                Lead, LeadOwnershipAction.Assign, Colleague, Owner, ActorIsManager: false,
                1, "give-away", "give-away-corr"), default));
    }

    [Fact]
    public async Task A_manager_can_move_a_lead_between_two_other_people()
    {
        using var database = new TestDb();
        await using var context = await SeedAsync(database);
        var routing = Routing(context);
        await TakeAsync(routing, Owner, version: 1);

        var result = await routing.ChangeLeadOwnershipAsync(Tenant, new ChangeLeadOwnershipCommand(
            Lead, LeadOwnershipAction.Assign, Colleague, Manager, ActorIsManager: true,
            2, "manager-move", "manager-move-corr", "Owner is on leave this week"), default);

        Assert.Equal(Colleague, result.AssignedToUserId);
    }

    /// <summary>
    /// The wiring, not the unit. The rule lives in the application service, but it is only worth
    /// anything if the ENDPOINT the lead detail screen calls actually resolves the caller's rank
    /// and passes it in — the defect was an endpoint that never asked the question at all. This
    /// drives the controller with a non-manager role gate and expects 403.
    /// </summary>
    [Fact]
    public async Task The_owner_endpoint_refuses_a_rep_moving_someone_elses_lead()
    {
        using var database = new TestDb();
        await using var context = await SeedAsync(database);
        await TakeAsync(Routing(context), Owner, version: 1);

        var response = await Controller(context, isManager: false, callerUserId: Colleague)
            .ChangeOwner(Lead, new ChangeLeadOwnerRequest(
                LeadOwnershipAction.Assign, Colleague, 2, "http-steal", "http-steal-corr"), default);

        var refusal = Assert.IsType<ObjectResult>(response.Result);
        Assert.Equal(StatusCodes.Status403Forbidden, refusal.StatusCode);
        Assert.Equal(Owner, (await context.Leads.AsNoTracking().SingleAsync(x => x.Id == Lead)).AssignTo);
    }

    [Fact]
    public async Task The_owner_endpoint_lets_a_manager_move_the_same_lead()
    {
        using var database = new TestDb();
        await using var context = await SeedAsync(database);
        await TakeAsync(Routing(context), Owner, version: 1);

        var response = await Controller(context, isManager: true, callerUserId: Manager)
            .ChangeOwner(Lead, new ChangeLeadOwnerRequest(
                LeadOwnershipAction.Assign, Colleague, 2, "http-move", "http-move-corr",
                "Reassigned during handover"), default);

        Assert.IsType<OkObjectResult>(response.Result);
        Assert.Equal(Colleague, (await context.Leads.AsNoTracking().SingleAsync(x => x.Id == Lead)).AssignTo);
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // P1-3  taking work off a named person is explained
    // ─────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Moving_a_lead_away_from_its_owner_without_a_reason_is_refused()
    {
        using var database = new TestDb();
        await using var context = await SeedAsync(database);
        var routing = Routing(context);
        await TakeAsync(routing, Owner, version: 1);

        var refusal = await Assert.ThrowsAsync<ArgumentException>(() =>
            routing.ChangeLeadOwnershipAsync(Tenant, new ChangeLeadOwnershipCommand(
                Lead, LeadOwnershipAction.Assign, Colleague, Manager, ActorIsManager: true,
                2, "no-reason", "no-reason-corr"), default));

        Assert.Equal(Owner, (await context.Leads.AsNoTracking().SingleAsync(x => x.Id == Lead)).AssignTo);
        AssertSpeaksEnglish(refusal.Message);
    }

    [Fact]
    public async Task A_token_reason_is_not_a_reason()
    {
        using var database = new TestDb();
        await using var context = await SeedAsync(database);
        var routing = Routing(context);
        await TakeAsync(routing, Owner, version: 1);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            routing.ChangeLeadOwnershipAsync(Tenant, new ChangeLeadOwnershipCommand(
                Lead, LeadOwnershipAction.Assign, Colleague, Manager, ActorIsManager: true,
                2, "tiny-reason", "tiny-reason-corr", "  x  "), default));
    }

    /// <summary>
    /// The other half of the rule, and the reason it is worth stating: picking up work nobody owns
    /// is not a conflict, so it must not be made to feel like one.
    /// </summary>
    [Fact]
    public async Task Taking_an_unowned_lead_needs_no_reason()
    {
        using var database = new TestDb();
        await using var context = await SeedAsync(database);

        var result = await Routing(context).ChangeLeadOwnershipAsync(Tenant, new ChangeLeadOwnershipCommand(
            Lead, LeadOwnershipAction.Assign, Owner, Owner, ActorIsManager: false,
            1, "no-conflict", "no-conflict-corr"), default);

        Assert.Equal(Owner, result.AssignedToUserId);
    }

    [Fact]
    public async Task The_reason_given_is_the_reason_stored_against_the_move()
    {
        using var database = new TestDb();
        await using var context = await SeedAsync(database);
        var routing = Routing(context);
        await TakeAsync(routing, Owner, version: 1);

        await routing.ChangeLeadOwnershipAsync(Tenant, new ChangeLeadOwnershipCommand(
            Lead, LeadOwnershipAction.Assign, Colleague, Manager, ActorIsManager: true,
            2, "stored-reason", "stored-reason-corr", "Owner left the company"), default);

        var move = await context.Set<LeadAssignment>().AsNoTracking()
            .SingleAsync(x => x.LeadId == Lead && x.ToUserId == Colleague);
        Assert.Equal("Owner left the company", move.Comment);
        Assert.Equal(Owner, move.FromUserId);
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // P1-4  the tenant's one fallback owner
    // ─────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A tenant with no per-customer ownership rows — which is every tenant on its first day —
    /// used to park EVERY inquiry on the queue forever. One setting changes that.
    /// </summary>
    [Fact]
    public async Task A_fallback_owner_takes_an_inquiry_nothing_else_can_place()
    {
        using var database = new TestDb();
        await using var context = await SeedAsync(database, fallbackOwner: Owner);

        var result = await Routing(context).RouteLeadAsync(Tenant,
            new RouteLeadCommand(Lead, "fallback-route", "fallback-corr"), default);

        Assert.Equal(Owner, result.SelectedUserId);
        Assert.Equal(DeterministicRoutingEngine.DefaultOwnerAssignedCode, result.DecisionCode);
        Assert.Null(result.WorkItemId);
        Assert.Equal(Owner, (await context.Leads.AsNoTracking().SingleAsync(x => x.Id == Lead)).AssignTo);
    }

    /// <summary>
    /// "Why did this person get it?" has to stay answerable, and the answer is not "because a rule
    /// named them" — it is "because nothing could be worked out, and this is who you told us to
    /// give those to". Both halves are on the decision.
    /// </summary>
    [Fact]
    public async Task A_fallback_assignment_records_what_it_stood_in_for()
    {
        using var database = new TestDb();
        await using var context = await SeedAsync(database, fallbackOwner: Owner);

        var result = await Routing(context).RouteLeadAsync(Tenant,
            new RouteLeadCommand(Lead, "fallback-explain", "fallback-explain-corr"), default);

        using var explanation = JsonDocument.Parse(result.Explanation);
        Assert.Equal("NO_MATCH_EVIDENCE",
            explanation.RootElement.GetProperty("fallbackForDecisionCode").GetString());
        var assignment = await context.Set<LeadAssignment>().AsNoTracking()
            .SingleAsync(x => x.LeadId == Lead);
        Assert.Equal(DeterministicRoutingEngine.DefaultOwnerAssignedCode, assignment.ReasonCode);
        Assert.Equal(Owner, assignment.ToUserId);
        Assert.Null(assignment.OwnershipId);
    }

    /// <summary>
    /// A fallback owner who cannot be given governed work is not a fallback. The inquiry goes to
    /// the queue, which is precisely the behaviour that existed before the setting.
    /// </summary>
    [Fact]
    public async Task An_ineligible_fallback_owner_changes_nothing()
    {
        using var database = new TestDb();
        await using var context = await SeedAsync(database, fallbackOwner: Colleague, profiledUsers: [Owner]);

        var result = await Routing(context).RouteLeadAsync(Tenant,
            new RouteLeadCommand(Lead, "inert-fallback", "inert-fallback-corr"), default);

        Assert.Equal(RoutingOutcome.Unassigned, result.Outcome);
        Assert.Equal("NO_MATCH_EVIDENCE", result.DecisionCode);
        Assert.NotNull(result.WorkItemId);
    }

    [Fact]
    public async Task With_no_fallback_owner_an_unplaceable_inquiry_still_goes_to_the_queue()
    {
        using var database = new TestDb();
        await using var context = await SeedAsync(database);

        var result = await Routing(context).RouteLeadAsync(Tenant,
            new RouteLeadCommand(Lead, "no-fallback", "no-fallback-corr"), default);

        Assert.Equal(RoutingOutcome.Unassigned, result.Outcome);
        Assert.NotNull(result.WorkItemId);
    }

    /// <summary>
    /// A real ownership rule still wins. The fallback is reached only where the engine would
    /// otherwise have given up.
    /// </summary>
    [Fact]
    public async Task A_real_owner_still_beats_the_fallback()
    {
        using var database = new TestDb();
        await using var context = await SeedAsync(database,
            withOwnershipFor: Colleague, fallbackOwner: Owner, profiledUsers: [Owner, Colleague]);

        var result = await Routing(context).RouteLeadAsync(Tenant,
            new RouteLeadCommand(Lead, "rule-wins", "rule-wins-corr"), default);

        Assert.Equal(Colleague, result.SelectedUserId);
        Assert.Equal("PRIMARY_OWNER_ASSIGNED", result.DecisionCode);
    }

    [Fact]
    public async Task The_fallback_owner_setting_round_trips_through_the_api()
    {
        using var database = new TestDb();
        await using var context = await SeedAsync(database);
        var controller = Controller(context, isManager: true, callerUserId: Manager);

        var empty = Assert.IsType<OkObjectResult>((await controller.DefaultOwner(default)).Result);
        Assert.Null(Assert.IsType<DefaultLeadOwnerResponse>(empty.Value).DefaultOwnerUserId);

        var saved = Assert.IsType<OkObjectResult>(
            (await controller.SetDefaultOwner(new SetDefaultLeadOwnerRequest(Owner), default)).Result);
        var value = Assert.IsType<DefaultLeadOwnerResponse>(saved.Value);
        Assert.Equal(Owner, value.DefaultOwnerUserId);
        Assert.True(value.IsEligible);
        Assert.Equal(Manager, value.SetByUserId);

        var cleared = Assert.IsType<OkObjectResult>(
            (await controller.SetDefaultOwner(new SetDefaultLeadOwnerRequest(null), default)).Result);
        Assert.Null(Assert.IsType<DefaultLeadOwnerResponse>(cleared.Value).DefaultOwnerUserId);
    }

    /// <summary>
    /// A setting that is saved but inert is the worst of both worlds, so the read says so — in the
    /// routing engine's own words, not a code.
    /// </summary>
    [Fact]
    public async Task The_setting_admits_when_the_person_chosen_will_never_be_used()
    {
        using var database = new TestDb();
        await using var context = await SeedAsync(database, profiledUsers: [Owner]);
        var controller = Controller(context, isManager: true, callerUserId: Manager);

        var saved = Assert.IsType<OkObjectResult>(
            (await controller.SetDefaultOwner(new SetDefaultLeadOwnerRequest(Colleague), default)).Result);

        var value = Assert.IsType<DefaultLeadOwnerResponse>(saved.Value);
        Assert.Equal(Colleague, value.DefaultOwnerUserId);
        Assert.False(value.IsEligible);
        Assert.Equal(RoutingEligibilityReasons.ProfileRequired, value.EligibilityReason);
        AssertSpeaksEnglish(value.EligibilityReason);
    }

    [Fact]
    public async Task A_user_from_another_tenant_cannot_be_the_fallback_owner()
    {
        using var database = new TestDb();
        await using var context = await SeedAsync(database);
        await using (var other = database.ContextFor(null))
        {
            Seed.EnsureBusinessUnit(other, Tenant + 1);
            other.Users.Add(User(96_900, Tenant + 1));
            await other.SaveChangesAsync();
        }

        await Assert.ThrowsAsync<RoutingConflictException>(() => Routing(context)
            .SetDefaultOwnerAsync(Tenant, new SetDefaultLeadOwnerCommand(96_900, Manager), default));
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Nothing a user is shown may be an internal code. The engine's codes are audit keys — they
    /// belong on the decision row, never in the sentence someone has to act on.
    /// </summary>
    private static void AssertSpeaksEnglish(string message)
    {
        Assert.DoesNotContain('_', message);
        Assert.Contains(' ', message);
    }

    private static Task TakeAsync(ICommercialRoutingApplicationService routing, long userId, long version) =>
        routing.ChangeLeadOwnershipAsync(Tenant, new ChangeLeadOwnershipCommand(
            Lead, LeadOwnershipAction.Assign, userId, userId, ActorIsManager: false,
            version, $"take-{userId}-{version}", $"take-corr-{userId}-{version}"), default);

    private static async Task<ErpRfqAutomationContext> SeedAsync(
        TestDb database,
        long? withOwnershipFor = null,
        long? fallbackOwner = null,
        long[]? profiledUsers = null)
    {
        await using (var seed = database.ContextFor(null))
        {
            Seed.Lead(seed, Lead, Tenant, buyersName: "Fallback Buyer");
            Seed.Customer(seed, 96_500, Tenant, "Fallback Buyer");
            seed.Users.AddRange(User(Owner, Tenant), User(Colleague, Tenant), User(Manager, Tenant));
            await seed.SaveChangesAsync();

            if (withOwnershipFor is long ownerId)
            {
                // A verified e-mail identifier is the ordinary way a lead reaches ownership
                // selection, so the fixture matches the shape production actually produces
                // rather than short-circuiting to a customer the engine never proved.
                var lead = await seed.Leads.SingleAsync(x => x.Id == Lead);
                lead.Clientemail = "buyer@fallback.example";
                seed.Set<CustomerIdentifier>().Add(new CustomerIdentifier
                {
                    BusinessUnitId = Tenant, CustomerId = 96_500,
                    IdentifierType = CustomerIdentifierType.Email,
                    NormalizedValue = RoutingValueNormalizer.Normalize(
                        CustomerIdentifierType.Email, "buyer@fallback.example"),
                    DisplayValue = "buyer@fallback.example", IsVerified = true,
                    Confidence = 0.99m, Source = "test",
                    EffectiveFrom = DateTime.UtcNow.AddDays(-1)
                });
                seed.Set<CustomerOwnership>().Add(new CustomerOwnership
                {
                    BusinessUnitId = Tenant, CustomerId = 96_500, PrimaryUserId = ownerId,
                    Scope = OwnershipScope.GeneralCustomer, Priority = 100,
                    EffectiveFrom = DateTime.UtcNow.AddDays(-1), IsActive = true,
                    Source = "test", Version = 1
                });
            }
            if (fallbackOwner is long fallbackId)
            {
                var unit = await seed.BusinessUnits.SingleAsync(x => x.Id == Tenant);
                unit.DefaultLeadOwnerUserId = fallbackId;
                unit.DefaultLeadOwnerSetByUserId = Manager;
                unit.DefaultLeadOwnerSetOn = DateTime.UtcNow;
            }
            foreach (var userId in profiledUsers ?? [Owner, Colleague, Manager])
                seed.SalesRepProfiles.Add(EligibleProfile(userId));
            await seed.SaveChangesAsync();
        }
        return database.ContextFor(Tenant);
    }

    private static CommercialRoutingApplicationService Routing(ErpRfqAutomationContext context) =>
        new(context, new DeterministicRoutingEngine(), new RoutingPolicy());

    private static CommercialRoutingController Controller(
        ErpRfqAutomationContext context, bool isManager, long callerUserId) =>
        new(Routing(context), new FixedRoleGate(isManager))
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                    [
                        new Claim("businessUnitId", Tenant.ToString()),
                        new Claim(ClaimTypes.NameIdentifier, callerUserId.ToString()),
                        new Claim("roleId", "1")
                    ], "routing-correctness-test"))
                }
            }
        };

    private static User User(long id, long tenant) => new()
    {
        Id = id, FirstName = $"User{id}", LastName = "Routing", Email = $"user{id}@test",
        PasswordHash = "not-used", ImageUrl = "n/a", Buid = tenant,
        IsActive = true, CreatedBy = "test", CreatedOn = DateTime.UtcNow
    };

    private static SalesRepProfile EligibleProfile(long userId) => new()
    {
        BusinessUnitId = Tenant, UserId = userId, IsRoutingEligible = true,
        CapacityPercent = 100, DistributionWeight = 1,
        EffectiveFromUtc = DateTime.UtcNow.AddDays(-1), Version = 1,
        UpdatedAtUtc = DateTime.UtcNow, UpdatedBy = "routing-correctness-test",
        LastMutationIdempotencyKey = $"routing-correctness-profile-{userId}"
    };

    private sealed class FixedRoleGate(bool isManager) : IRoleGate
    {
        public Task<bool> IsSuperAdminAsync(long roleId, long businessUnitId) => Task.FromResult(false);
        public Task<short> GetRoleRankAsync(long roleId, long businessUnitId) =>
            Task.FromResult(isManager ? RoleRanks.Manager : RoleRanks.Member);
        public Task<bool> IsManagerOrAdminAsync(long roleId, long businessUnitId) => Task.FromResult(isManager);
        public Task<bool> CanManageRoleAsync(long callerRoleId, long? targetRoleId, long businessUnitId) =>
            Task.FromResult(isManager);
    }
}
