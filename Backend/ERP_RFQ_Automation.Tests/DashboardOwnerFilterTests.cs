using System.Security.Claims;
using System.Text.Json;
using ERP_RFQ_Automation.Authorization;
using ERP_RFQ_Automation.CommercialIntelligence.Sales;
using ERP_RFQ_Automation.CommercialRouting;
using ERP_RFQ_Automation.Controllers;
using ERP_RFQ_Automation.DTOs.Dashboard;
using ERP_RFQ_Automation.Interfaces;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Reporting;
using ERP_RFQ_Automation.Repositories;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// The dashboard's one-rep filter (<c>ownerUserId</c>). A manager picks one rep and every scoped
/// panel shows only that rep's work. Three rules are certified here:
/// <list type="number">
/// <item>Filtering to a rep inside the caller's scope narrows to exactly that rep — strictly: the
/// account-team / named-ownership branch must not pull in another rep's work.</item>
/// <item>Filtering to a rep OUTSIDE the caller's scope is 403 — the filter never widens.</item>
/// <item>No parameter = the resolved scope reaches the query unchanged.</item>
/// </list>
/// </summary>
public sealed class DashboardOwnerFilterTests
{
    private const long Bu = 8_700;
    private const long NorthTeam = 8_710;
    private const long SouthTeam = 8_711;
    private const long Manager = 8_720;   // manages North
    private const long Rep = 8_721;       // on North
    private const long SouthRep = 8_722;  // on South — outside the manager's scope
    private const long NorthCustomer = 8_730;
    private const long SouthCustomer = 8_731;

    private static readonly DateTime Now = new(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc);

    // One instance, so "passed through unchanged" can be asserted by reference-equal lists.
    private static readonly AccountTeamScope ManagerScopeInstance =
        new(AccountScopeTier.ManagedScope, Manager, [NorthTeam], [Manager, Rep]);

    private static AccountTeamScope ManagerScope() => ManagerScopeInstance;

    // ── the scope rule itself ─────────────────────────────────────────────────

    [Fact]
    public void Narrowing_to_a_rep_in_scope_yields_exactly_that_rep()
    {
        Assert.True(ManagerScope().TryNarrowToRep(Rep, out var narrowed));

        Assert.True(narrowed.IsSingleRep);
        Assert.Equal(AccountScopeTier.AssignedAccounts, narrowed.Tier);
        Assert.Equal(Rep, narrowed.UserId);
        Assert.Equal([Rep], narrowed.UserIds);
        Assert.Empty(narrowed.TeamIds);
        Assert.Equal("single_rep", narrowed.ScopeName);
    }

    [Fact]
    public void Narrowing_never_widens_a_scope()
    {
        Assert.False(ManagerScope().TryNarrowToRep(SouthRep, out var unchanged));
        Assert.Equal(ManagerScope().UserIds, unchanged.UserIds);

        var repScope = new AccountTeamScope(AccountScopeTier.AssignedAccounts, Rep, [NorthTeam], [Rep]);
        Assert.False(repScope.TryNarrowToRep(Manager, out _));
        Assert.True(repScope.TryNarrowToRep(Rep, out _));

        // Tenant-wide callers may pick anyone in their tenant.
        Assert.True(AccountTeamScope.TenantWide(Manager).TryNarrowToRep(SouthRep, out var fromTenant));
        Assert.Equal([SouthRep], fromTenant.UserIds);
        Assert.False(fromTenant.IsTenantWide);
    }

    // ── repository: strict per-rep reads ──────────────────────────────────────

    [Fact]
    public async Task Release01_filtered_to_a_team_rep_counts_only_leads_assigned_to_that_rep()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(Bu);
        await SeedAsync(context);
        var repository = new DashboardRepository(context);

        var unfiltered = await repository.GetRelease01Async(
            Bu, ManagerScope(), Now.AddDays(-30), Now, Now.AddMinutes(1));
        // Rep's lead, the manager's own lead on North, and the unassigned North lead.
        Assert.Equal(3, Received(unfiltered).Denominator);
        Assert.Equal("managed_scope", unfiltered.RoleScope.Scope);

        Assert.True(ManagerScope().TryNarrowToRep(Rep, out var narrowed));
        var filtered = await repository.GetRelease01Async(
            Bu, narrowed, Now.AddDays(-30), Now, Now.AddMinutes(1));

        // Only lead 1. Lead 4 is on South Account, which Rep is the NAMED OWNER of, but it is
        // assigned to SouthRep — the account branch would have counted it under Rep's name.
        Assert.Equal(1, Received(filtered).Denominator);
        Assert.Equal("single_rep", filtered.RoleScope.Scope);
        Assert.Equal(Rep, filtered.RoleScope.OwnerUserId);
        Assert.Equal([Rep], filtered.RoleScope.ScopedUserIds);
        Assert.Empty(filtered.RoleScope.AccountTeamIds);
    }

    [Fact]
    public async Task Deadline_board_filtered_to_a_team_rep_lists_only_that_reps_leads()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(Bu);
        await SeedAsync(context);
        var repository = new DashboardRepository(context);

        var unfiltered = await repository.GetDeadlineBoardAsync(Bu, 200, default, ManagerScope());
        Assert.Equal([8_741L, 8_742L], unfiltered.Leads.Select(l => l.LeadId).Order().ToArray());

        Assert.True(ManagerScope().TryNarrowToRep(Rep, out var narrowed));
        var filtered = await repository.GetDeadlineBoardAsync(Bu, 200, default, narrowed);

        Assert.Equal([8_741L], filtered.Leads.Select(l => l.LeadId).ToArray());
        Assert.Equal(1, filtered.OpenLeads);
    }

    // ── controllers: 403 outside scope, unchanged without the parameter ──────

    [Fact]
    public async Task Release01_endpoint_narrows_forbids_and_passes_through()
    {
        var repository = new CapturingDashboardRepository();
        var controller = DashboardControllerFor(repository);

        Assert.IsType<OkObjectResult>((await controller.GetRelease01(null, null, default, Rep)).Result);
        Assert.Equal("single_rep", repository.Scope!.ScopeName);
        Assert.Equal([Rep], repository.Scope.UserIds);

        repository.Scope = null;
        Assert.IsType<ForbidResult>((await controller.GetRelease01(null, null, default, SouthRep)).Result);
        Assert.Null(repository.Scope);

        Assert.IsType<OkObjectResult>((await controller.GetRelease01(null, null, default)).Result);
        Assert.Equal(ManagerScope(), repository.Scope);
    }

    [Fact]
    public async Task Deadline_board_endpoint_narrows_forbids_and_passes_through()
    {
        var repository = new CapturingDashboardRepository();
        var controller = DashboardControllerFor(repository);

        var ok = Assert.IsType<OkObjectResult>((await controller.GetDeadlineBoard(200, default, Rep)).Result);
        var board = Assert.IsType<DeadlineBoardDTO>(ok.Value);
        Assert.Equal("single_rep", board.Scope);
        Assert.Equal(Rep, board.OwnerUserId);
        Assert.Equal([Rep], repository.Scope!.UserIds);

        repository.Scope = null;
        Assert.IsType<ForbidResult>((await controller.GetDeadlineBoard(200, default, SouthRep)).Result);
        Assert.Null(repository.Scope);

        var unfiltered = Assert.IsType<OkObjectResult>((await controller.GetDeadlineBoard(200, default)).Result);
        Assert.Equal(ManagerScope(), repository.Scope);
        // Additive-only contract: the new fields are absent from an unfiltered payload.
        var json = JsonSerializer.Serialize(unfiltered.Value, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.DoesNotContain("\"scope\"", json);
        Assert.DoesNotContain("\"ownerUserId\"", json);
    }

    [Fact]
    public async Task Pipeline_analytics_endpoint_narrows_forbids_and_passes_through()
    {
        var repository = new CapturingDashboardRepository();
        var controller = new WorkloadController(repository, null!, new FixedScopeResolver(ManagerScope()))
        {
            ControllerContext = Context(Manager)
        };

        Assert.IsType<OkObjectResult>((await controller.GetPipelineAnalytics(null, null, default, Rep)).Result);
        Assert.True(repository.Scope!.IsSingleRep);
        Assert.Equal([Rep], repository.Scope.UserIds);

        repository.Scope = null;
        Assert.IsType<ForbidResult>((await controller.GetPipelineAnalytics(null, null, default, SouthRep)).Result);
        Assert.Null(repository.Scope);

        Assert.IsType<OkObjectResult>((await controller.GetPipelineAnalytics(null, null, default)).Result);
        Assert.Equal(ManagerScope(), repository.Scope);
    }

    [Fact]
    public async Task Performance_endpoint_queries_only_the_chosen_rep_and_refuses_an_outsider()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(Bu);
        await SeedUsersAsync(context);
        var sales = new CapturingSalesService();
        var controller = new CommercialIntelligenceController(
            context, sales, null!, new ManagerRoleGate(), new FixedScopeResolver(ManagerScope()))
        {
            ControllerContext = Context(Manager)
        };
        var from = DateTime.UtcNow.AddDays(-7);
        var to = DateTime.UtcNow.AddDays(1);

        var ok = Assert.IsType<OkObjectResult>(await controller.Performance(from, to, default, Rep));
        Assert.Equal([Rep], sales.QueriedUserIds);
        using (var document = Json(ok.Value))
        {
            Assert.Equal("single_rep", document.RootElement.GetProperty("scope").GetString());
            var reps = document.RootElement.GetProperty("representatives").EnumerateArray()
                .Select(r => r.GetProperty("userId").GetInt64()).ToArray();
            Assert.Equal([Rep], reps);
        }

        sales.QueriedUserIds.Clear();
        Assert.IsType<ForbidResult>(await controller.Performance(from, to, default, SouthRep));
        Assert.Empty(sales.QueriedUserIds);

        Assert.IsType<OkObjectResult>(await controller.Performance(from, to, default));
        Assert.Equal([Manager, Rep], sales.QueriedUserIds);
    }

    [Fact]
    public async Task Sales_today_endpoint_shows_only_the_chosen_reps_follow_ups()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(Bu);
        await SeedUsersAsync(context);
        var now = DateTime.UtcNow;
        context.FollowUpTasks.AddRange(
            FollowUp(8_761, Manager, now.AddHours(-1), "MANAGER_TASK"),
            FollowUp(8_762, Rep, now.AddHours(-1), "REP_TASK"),
            FollowUp(8_763, SouthRep, now.AddHours(-1), "SOUTH_TASK"));
        await context.SaveChangesAsync();
        var controller = new CommercialIntelligenceController(
            context, null!, null!, new ManagerRoleGate(), new FixedScopeResolver(ManagerScope()))
        {
            ControllerContext = Context(Manager)
        };

        var ok = Assert.IsType<OkObjectResult>(await controller.SalesToday(default, Rep));
        using (var document = Json(ok.Value))
        {
            Assert.Equal("single_rep", document.RootElement.GetProperty("scope").GetString());
            var reasons = document.RootElement.GetProperty("attentionItems").EnumerateArray()
                .Select(x => x.GetProperty("reason").GetString()).ToArray();
            Assert.Equal(["REP_TASK"], reasons);
        }

        Assert.IsType<ForbidResult>(await controller.SalesToday(default, SouthRep));

        var unfiltered = Assert.IsType<OkObjectResult>(await controller.SalesToday(default));
        using (var document = Json(unfiltered.Value))
        {
            Assert.Equal("managed_scope", document.RootElement.GetProperty("scope").GetString());
            Assert.Equal(2, document.RootElement.GetProperty("attentionItems").GetArrayLength());
        }
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static DashboardRelease01KpiDTO Received(DashboardRelease01DTO result)
        => result.Kpis.Single(k => k.Key == "leads_received");

    private static JsonDocument Json(object? value) => JsonDocument.Parse(
        JsonSerializer.Serialize(value, new JsonSerializerOptions(JsonSerializerDefaults.Web)));

    private static DashboardController DashboardControllerFor(IDashboardRepository repository)
        => new(repository, new FixedScopeResolver(ManagerScope())) { ControllerContext = Context(Manager) };

    private static ControllerContext Context(long userId) => new()
    {
        HttpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim("businessUnitId", Bu.ToString()),
                new Claim("roleId", "1"),
                new Claim(ClaimTypes.NameIdentifier, userId.ToString())
            ], "test"))
        }
    };

    private static async Task SeedUsersAsync(ErpRfqAutomationContext context)
    {
        Seed.EnsureBusinessUnit(context, Bu);
        context.Users.AddRange(User(Manager), User(Rep), User(SouthRep));
        await context.SaveChangesAsync();
    }

    /// <summary>
    /// Five leads. Only lead 8_741 is Rep's. 8_744 is the trap: it sits on South Account, which
    /// Rep is the named primary owner of, but it is assigned to SouthRep.
    /// </summary>
    private static async Task SeedAsync(ErpRfqAutomationContext context)
    {
        await SeedUsersAsync(context);
        context.Teams.AddRange(
            new Team { Id = NorthTeam, TeamName = "North", BusinessUnitId = Bu, ManagerId = Manager, CreatedBy = "seed", CreatedOn = Now },
            new Team { Id = SouthTeam, TeamName = "South", BusinessUnitId = Bu, CreatedBy = "seed", CreatedOn = Now });
        await context.SaveChangesAsync();

        Seed.Customer(context, NorthCustomer, Bu, "North Account");
        Seed.Customer(context, SouthCustomer, Bu, "South Account");
        await context.SaveChangesAsync();
        (await context.Customers.SingleAsync(c => c.Id == NorthCustomer)).AccountTeamId = NorthTeam;
        (await context.Customers.SingleAsync(c => c.Id == SouthCustomer)).AccountTeamId = SouthTeam;
        context.Set<CustomerOwnership>().Add(new CustomerOwnership
        {
            BusinessUnitId = Bu, CustomerId = SouthCustomer, PrimaryUserId = Rep,
            Scope = default, EffectiveFrom = Now.AddYears(-1), IsActive = true, Source = "seed", Version = 1
        });
        await context.SaveChangesAsync();

        var leads = new[]
        {
            (Id: 8_741L, AssignTo: (long?)Rep, Customer: (long?)NorthCustomer),
            (Id: 8_742L, AssignTo: (long?)Manager, Customer: (long?)NorthCustomer),
            (Id: 8_743L, AssignTo: (long?)null, Customer: (long?)NorthCustomer),
            (Id: 8_744L, AssignTo: (long?)SouthRep, Customer: (long?)SouthCustomer),
            (Id: 8_745L, AssignTo: (long?)SouthRep, Customer: (long?)null),
        };
        var seeded = leads.Select(l => (l, Seed.Lead(context, l.Id, Bu))).ToList();
        await context.SaveChangesAsync();
        foreach (var (spec, lead) in seeded)
        {
            lead.AssignTo = spec.AssignTo;
            if (spec.Customer.HasValue)
                lead.ResolveCommercialIdentity(spec.Customer.Value, null, "CONFIRMED");
        }
        await context.SaveChangesAsync();
    }

    private static User User(long id) => new()
    {
        Id = id, FirstName = "Synthetic", LastName = $"User {id}",
        Email = $"user{id}@example.test", PasswordHash = "x", ImageUrl = "n/a",
        Buid = Bu, IsActive = true, CreatedBy = "seed", CreatedOn = Now
    };

    private static FollowUpTask FollowUp(long id, long userId, DateTime dueAt, string purpose) => new()
    {
        Id = id, BusinessUnitId = Bu, AssignedToUserId = userId,
        AggregateType = "Other", AggregateId = id, DueAtUtc = dueAt,
        PurposeCode = purpose, CreatedAtUtc = DateTime.UtcNow,
        UpdatedAtUtc = DateTime.UtcNow, CreatedBy = "test",
        CorrelationId = $"follow-up-{id}", CreationIdempotencyKey = $"follow-up-{id}"
    };

    private sealed class FixedScopeResolver(AccountTeamScope scope) : IAccountTeamScopeResolver
    {
        public Task<AccountTeamScope> ResolveAsync(
            long userId, long roleId, long businessUnitId, DateTime asOfUtc, CancellationToken ct = default)
            => Task.FromResult(scope);
    }

    private sealed class ManagerRoleGate : IRoleGate
    {
        public Task<bool> IsSuperAdminAsync(long roleId, long businessUnitId) => Task.FromResult(false);
        public Task<short> GetRoleRankAsync(long roleId, long businessUnitId) => Task.FromResult(RoleRanks.Manager);
        public Task<bool> IsManagerOrAdminAsync(long roleId, long businessUnitId) => Task.FromResult(true);
        public Task<bool> CanManageRoleAsync(long callerRoleId, long? targetRoleId, long businessUnitId) => Task.FromResult(false);
    }

    private sealed class CapturingSalesService : ISalesApplicationService
    {
        public List<long> QueriedUserIds { get; } = [];

        public Task<IReadOnlyList<SalesRepPerformance>> GetPerformanceAsync(
            long businessUnitId, SalesPerformanceQuery query, CancellationToken ct)
        {
            if (query.SalesRepUserId.HasValue) QueriedUserIds.Add(query.SalesRepUserId.Value);
            return Task.FromResult<IReadOnlyList<SalesRepPerformance>>([]);
        }

        public WeightedRoutingResult ScoreNewCustomer(NewCustomerRoutingRequest request) => throw new NotSupportedException();
        public Task<SalesRepProfile> UpsertProfileAsync(long businessUnitId, UpsertSalesRepProfileCommand command, CancellationToken ct) => throw new NotSupportedException();
        public Task<CommercialActivity> AppendActivityAsync(long businessUnitId, AppendCommercialActivityCommand command, CancellationToken ct) => throw new NotSupportedException();
        public Task<FollowUpTask> CreateFollowUpAsync(long businessUnitId, CreateFollowUpTaskCommand command, CancellationToken ct) => throw new NotSupportedException();
        public Task<FollowUpTransitionEvent> TransitionFollowUpAsync(long businessUnitId, long taskId, TransitionFollowUpTaskCommand command, CancellationToken ct) => throw new NotSupportedException();
        public Task<SalesContribution> RecordContributionAsync(long businessUnitId, RecordSalesContributionCommand command, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class CapturingDashboardRepository : IDashboardRepository
    {
        public AccountTeamScope? Scope { get; set; }

        public Task<DashboardRelease01DTO> GetRelease01Async(
            long businessUnitId, AccountTeamScope scope, DateTime from, DateTime to, DateTime generatedAt,
            CancellationToken cancellationToken = default)
        {
            Scope = scope;
            return Task.FromResult(new DashboardRelease01DTO());
        }

        public Task<PipelineAnalyticsDTO> GetPipelineAnalyticsAsync(
            long businessUnitId, AccountTeamScope scope, DateTime? from = null, DateTime? to = null,
            CancellationToken cancellationToken = default)
        {
            Scope = scope;
            return Task.FromResult(new PipelineAnalyticsDTO());
        }

        public Task<DeadlineBoardDTO> GetDeadlineBoardAsync(
            long businessUnitId, int maxLeads = 200, CancellationToken cancellationToken = default,
            AccountTeamScope? accessScope = null)
        {
            Scope = accessScope;
            return Task.FromResult(new DeadlineBoardDTO(DateTime.UtcNow, 0, 0, 0, 0, [], []));
        }

        public Task<DashboardDataDTO> GetDashboardDataAsync(long businessUnitId) => throw new NotSupportedException();
        public Task<TeamWorkloadDTO> GetTeamWorkloadAsync(long businessUnitId, IReadOnlyCollection<long>? visibleUserIds = null) => throw new NotSupportedException();
        public Task<TeamWorkloadDTO> GetTeamWorkloadAsync(long businessUnitId) => throw new NotSupportedException();
        public Task<DocumentYieldDTO> GetDocumentYieldAsync(
            long businessUnitId, DateTime from, DateTime to, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}
