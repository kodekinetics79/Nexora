using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using ERP_RFQ_Automation.Authorization;
using ERP_RFQ_Automation.CommercialIntelligence.Sales;
using ERP_RFQ_Automation.CommercialRouting;
using ERP_RFQ_Automation.Controllers;
using ERP_RFQ_Automation.DTOs.AcceptedLeadDTOs;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Repositories;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Tests.CommercialRouting;

/// <summary>
/// The screens through which a human hands a lead to a sales rep.
///
/// The routing engine and the profile table it depends on were already correct and already
/// fail-closed; what was missing was every surface that lets someone see or change what it
/// decided. These tests pin the surfaces, not the engine — the engine has its own suite in
/// <see cref="CommercialRoutingApplicationServiceTests"/>.
/// </summary>
public sealed class LeadAssignmentSurfaceTests
{
    /// <summary>
    /// The dropdown behind every "assign this lead" dialog must say who governed routing will
    /// actually accept.
    ///
    /// It returns every active user, which is deliberate — narrowing it to the eligible set shows
    /// an empty list on a tenant that has not configured any profiles yet, which explains less
    /// than a wrong name does. What it may not do is present them all as equally pickable, because
    /// <c>AssignCoreAsync</c> throws <c>RoutingConflictException</c> for anyone without an
    /// effective, eligible profile. Before this, every name on a fresh tenant answered 409.
    /// </summary>
    [Fact]
    public async Task Assignment_dropdown_reports_who_governed_routing_will_actually_accept()
    {
        const long tenant = 91_001;
        using var database = new TestDb();
        await using var context = database.ContextFor(tenant);
        Seed.Lead(context, 91_010, tenant, buyersName: "Dropdown Buyer");
        context.Users.AddRange(User(91_002, tenant, "eligible@test"), User(91_003, tenant, "unprofiled@test"));
        context.SalesRepProfiles.Add(EligibleProfile(tenant, 91_002));
        await context.SaveChangesAsync();
        var controller = UnassignedLeads(context, tenant);

        var response = Assert.IsType<OkObjectResult>(
            (await controller.GetAssignmentUsers(null, default)).Result);
        var users = Assert.IsAssignableFrom<IEnumerable<UserDropdownDTO>>(response.Value)
            .ToDictionary(user => user.Id);

        Assert.True(users[91_002].IsEligibleForAssignment);
        Assert.Equal(RoutingEligibilityReasons.Eligible, users[91_002].EligibilityReason);
        Assert.False(users[91_003].IsEligibleForAssignment);
        Assert.Equal(RoutingEligibilityReasons.ProfileRequired, users[91_003].EligibilityReason);

        // The point of the flag: it agrees with what assignment does. The ineligible name is
        // still offered, and picking it is still refused.
        var routing = RoutingService(context);
        await Assert.ThrowsAsync<RoutingConflictException>(() => routing.AssignLeadAsync(tenant,
            new ManualAssignLeadCommand(91_010, 91_003, 91_002, "assign-unprofiled", "corr-unprofiled",
                AssignmentScope.LeadOnly, null, false, null), default));
        await routing.AssignLeadAsync(tenant, new ManualAssignLeadCommand(
            91_010, 91_002, null, "assign-eligible", "corr-eligible",
            AssignmentScope.LeadOnly, null, false, null), default);
        Assert.Equal(91_002, (await context.Leads.SingleAsync(lead => lead.Id == 91_010)).AssignTo);
    }

    /// <summary>
    /// Every other action on <c>UnAssignedLeadController</c> carries a module permission; this one
    /// carried none, so any authenticated user in the tenant could enumerate the tenant's staff.
    /// </summary>
    [Fact]
    public void Assignment_dropdown_is_gated_by_the_same_module_permission_as_its_siblings()
    {
        var action = typeof(UnAssignedLeadController)
            .GetMethod(nameof(UnAssignedLeadController.GetAssignmentUsers));

        var permission = Assert.Single(action!.GetCustomAttributes<RequireModulePermissionAttribute>());

        Assert.Equal("Leads", permission.ModuleName);
        Assert.Equal(PermissionAction.View, permission.Action);
    }

    /// <summary>
    /// A claim is a lease, and nothing in the product ever flips <c>Status</c> back to Open when
    /// the lease runs out — <c>MutateLeaseAsync</c> lets a later claimant take over an expired
    /// lease, but only at the moment someone tries. A queue rendered from <c>Status</c> would
    /// therefore show "Claimed by X" for ever, which is why the projection reports the lease
    /// itself and whether it has passed.
    /// </summary>
    [Fact]
    public async Task Routing_queue_reports_the_lease_holder_and_that_an_expired_lease_is_free()
    {
        const long tenant = 91_101;
        using var database = new TestDb();
        await using var context = database.ContextFor(tenant);
        Seed.Lead(context, 91_110, tenant, buyersName: "Leased Buyer");
        context.Users.Add(User(91_102, tenant, "claimant@test"));
        await context.SaveChangesAsync();
        var routing = RoutingService(context);
        var decision = await routing.RouteLeadAsync(tenant,
            new RouteLeadCommand(91_110, "queue-lease-route", "queue-lease-corr"), default);
        var workItemId = Assert.NotNull(decision.WorkItemId);
        await routing.ClaimAsync(tenant, workItemId,
            new QueueLeaseCommand(1, 91_102, 20), default);
        var claimed = await context.Set<UnassignedWorkItem>().SingleAsync(item => item.Id == workItemId);
        claimed.ClaimedUntil = DateTime.UtcNow.AddMinutes(-5);
        await context.SaveChangesAsync();

        var row = (await QueueRowsAsync(context, tenant)).Single();

        Assert.Equal(91_102, row.GetProperty("claimedByUserId").GetInt64());
        Assert.Equal("Sales Owner", row.GetProperty("claimedByName").GetString());
        Assert.True(row.GetProperty("claimExpired").GetBoolean());
        // The status the row would otherwise have been rendered from still says Claimed. That is
        // the whole defect: the two disagree, and only one of them is true.
        Assert.Equal("Claimed", row.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Routing_queue_reports_a_live_lease_as_still_held()
    {
        const long tenant = 91_151;
        using var database = new TestDb();
        await using var context = database.ContextFor(tenant);
        Seed.Lead(context, 91_160, tenant, buyersName: "Held Buyer");
        context.Users.Add(User(91_152, tenant, "holder@test"));
        await context.SaveChangesAsync();
        var routing = RoutingService(context);
        var decision = await routing.RouteLeadAsync(tenant,
            new RouteLeadCommand(91_160, "queue-live-route", "queue-live-corr"), default);
        await routing.ClaimAsync(tenant, decision.WorkItemId!.Value,
            new QueueLeaseCommand(1, 91_152, 20), default);

        var row = (await QueueRowsAsync(context, tenant)).Single();

        Assert.Equal(91_152, row.GetProperty("claimedByUserId").GetInt64());
        Assert.False(row.GetProperty("claimExpired").GetBoolean());
    }

    /// <summary>
    /// The read path that makes the profile table administrable. <c>sales_rep_profiles</c> is
    /// fail-closed, so the people who matter on this screen are the ones with NO row: they are
    /// invisible to the routing engine and there was previously no way to see that, and no way to
    /// learn the <c>ExpectedVersion</c> the write endpoint demands.
    /// </summary>
    [Fact]
    public async Task Rep_routing_profiles_list_users_who_have_no_profile_with_the_create_version()
    {
        const long tenant = 91_201;
        using var database = new TestDb();
        await using var context = database.ContextFor(tenant);
        Seed.BusinessUnit(context, tenant);
        context.Users.AddRange(User(91_202, tenant, "profiled@test"), User(91_203, tenant, "bare@test"));
        var profile = EligibleProfile(tenant, 91_202);
        profile.Version = 4;
        context.SalesRepProfiles.Add(profile);
        await context.SaveChangesAsync();

        var rows = (await ProfileRowsAsync(context, tenant))
            .ToDictionary(row => row.GetProperty("userId").GetInt64());

        Assert.True(rows[91_202].GetProperty("hasProfile").GetBoolean());
        Assert.True(rows[91_202].GetProperty("isAvailable").GetBoolean());
        Assert.Equal(4, rows[91_202].GetProperty("version").GetInt64());

        Assert.False(rows[91_203].GetProperty("hasProfile").GetBoolean());
        Assert.False(rows[91_203].GetProperty("isAvailable").GetBoolean());
        Assert.Equal(RoutingEligibilityReasons.ProfileRequired, rows[91_203].GetProperty("eligibilityReason").GetString());
        // 0 is the create sentinel UpsertProfileAsync expects, so the row can be posted straight
        // back without the client inventing a version.
        Assert.Equal(0, rows[91_203].GetProperty("version").GetInt64());
    }

    /// <summary>
    /// A profile whose effective window has closed is filtered out before the availability
    /// projection computes a reason, so to the engine it looks identical to no profile at all.
    /// The maintenance screen reads the stored row and must tell the two apart, because "create a
    /// profile" and "extend the one you have" are different corrections.
    /// </summary>
    [Fact]
    public async Task Rep_routing_profiles_separate_a_lapsed_profile_from_a_missing_one()
    {
        const long tenant = 91_251;
        using var database = new TestDb();
        await using var context = database.ContextFor(tenant);
        Seed.BusinessUnit(context, tenant);
        context.Users.Add(User(91_252, tenant, "lapsed@test"));
        var lapsed = EligibleProfile(tenant, 91_252);
        lapsed.EffectiveFromUtc = DateTime.UtcNow.AddDays(-30);
        lapsed.EffectiveToUtc = DateTime.UtcNow.AddDays(-1);
        context.SalesRepProfiles.Add(lapsed);
        await context.SaveChangesAsync();

        var row = (await ProfileRowsAsync(context, tenant)).Single();

        Assert.True(row.GetProperty("hasProfile").GetBoolean());
        Assert.False(row.GetProperty("profileEffectiveNow").GetBoolean());
        Assert.False(row.GetProperty("isAvailable").GetBoolean());
        Assert.Equal(RoutingEligibilityReasons.ProfileNotEffective, row.GetProperty("eligibilityReason").GetString());
    }

    private static async Task<JsonElement[]> QueueRowsAsync(ErpRfqAutomationContext context, long tenant)
    {
        var response = Assert.IsType<OkObjectResult>(
            await Intelligence(context, tenant).RoutingQueue(null, default));
        return Elements(response.Value);
    }

    private static async Task<JsonElement[]> ProfileRowsAsync(ErpRfqAutomationContext context, long tenant)
    {
        var response = Assert.IsType<OkObjectResult>(
            await Intelligence(context, tenant).RepRoutingProfiles(default));
        return Elements(response.Value);
    }

    private static JsonElement[] Elements(object? value) =>
        JsonDocument.Parse(JsonSerializer.Serialize(value,
                new JsonSerializerOptions(JsonSerializerDefaults.Web)))
            .RootElement.EnumerateArray().ToArray();

    private static CommercialIntelligenceController Intelligence(ErpRfqAutomationContext context, long tenant) =>
        new(context, null!, RoutingService(context), new TestRoleGate())
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = Principal(tenant) }
            }
        };

    private static UnAssignedLeadController UnassignedLeads(ErpRfqAutomationContext context, long tenant) =>
        new(new LeadRepository(context), RoutingService(context))
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = Principal(tenant) }
            }
        };

    private static CommercialRoutingApplicationService RoutingService(ErpRfqAutomationContext context) =>
        new(context, new DeterministicRoutingEngine(), new RoutingPolicy());

    private static ClaimsPrincipal Principal(long tenant, long userId = 1, long roleId = 1) => new(new ClaimsIdentity(
    [
        new Claim("businessUnitId", tenant.ToString()),
        new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
        new Claim("roleId", roleId.ToString())
    ], "lead-assignment-test"));

    private static User User(long id, long tenant, string email) => new()
    {
        Id = id, FirstName = "Sales", LastName = "Owner", Email = email,
        PasswordHash = "not-used", ImageUrl = "n/a", Buid = tenant,
        IsActive = true, CreatedBy = "test", CreatedOn = DateTime.UtcNow
    };

    private static SalesRepProfile EligibleProfile(long tenant, long userId) => new()
    {
        BusinessUnitId = tenant, UserId = userId, IsRoutingEligible = true,
        CapacityPercent = 100, DistributionWeight = 1, EffectiveFromUtc = DateTime.UtcNow.AddDays(-1),
        Version = 1, UpdatedAtUtc = DateTime.UtcNow, UpdatedBy = "lead-assignment-test",
        LastMutationIdempotencyKey = $"lead-assignment-profile-{userId}"
    };

    private sealed class TestRoleGate : IRoleGate
    {
        public Task<bool> IsSuperAdminAsync(long roleId, long businessUnitId) => Task.FromResult(false);
        public Task<short> GetRoleRankAsync(long roleId, long businessUnitId) => Task.FromResult(RoleRanks.Manager);
        public Task<bool> IsManagerOrAdminAsync(long roleId, long businessUnitId) => Task.FromResult(true);
        public Task<bool> CanManageRoleAsync(long callerRoleId, long? targetRoleId, long businessUnitId) => Task.FromResult(true);
    }
}
