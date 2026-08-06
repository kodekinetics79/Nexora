using System.Security.Claims;
using ERP_RFQ_Automation.Authorization;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Repositories;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// Server-side module RBAC (mirrors the frontend PermissionGuard):
/// - PermissionHandler: allow with a RolePermissions row, deny without (fail-closed),
///   View = row-exists, Create/Edit/Delete = the row's flags, the RANK rule
///   (RoleRank >= Admin satisfies any module requirement), 60s IMemoryCache on the lookup.
/// - ManagerRoleHandler ([RequireManagerRole]): RoleRank >= Manager.
/// - ModulePermissionPolicyProvider: dynamic "ModulePermission:{module}:{action}"
///   policy names with fall-through to conventionally registered policies.
/// </summary>
public class RbacAuthorizationTests
{
    private const long Bu = 1;
    private const long ModuleLeadsId = 501;
    private const string ModuleLeads = "Leads";

    // ---------------- seeding helpers ----------------

    private static void SeedModule(ErpRfqAutomationContext ctx, long id, string name)
        => ctx.Modules.Add(new Module
        {
            Id = id,
            ModuleName = name,
            IsActive = true,
            CreatedBy = "seed",
            CreatedOn = DateTime.UtcNow
        });

    private static void SeedRole(
        ErpRfqAutomationContext ctx, long roleId, string roleName,
        long businessUnitId = Bu, short rank = RoleRanks.Member)
    {
        Seed.EnsureBusinessUnit(ctx, businessUnitId);
        ctx.SetupMasters.Add(new SetupMaster
        {
            SetupId = roleId,
            SetupType = "role",
            SetupCode = roleName,
            SetupValue = roleName,
            RoleRank = rank,
            BusinessUnitId = businessUnitId,
            IsActive = true,
            CreatedBy = "seed",
            CreatedOn = DateTime.UtcNow
        });
    }

    private static void SeedPermission(
        ErpRfqAutomationContext ctx, long id, long roleId, long moduleId,
        bool canCreate = false, bool canEdit = false, bool canDelete = false,
        long businessUnitId = Bu)
        => ctx.RolePermissions.Add(new RolePermission
        {
            Id = id,
            RoleId = roleId,
            ModuleId = moduleId,
            BusinessUnitId = businessUnitId,
            CanCreate = canCreate,
            CanEdit = canEdit,
            CanDelete = canDelete,
            CreatedBy = "seed",
            CreatedOn = DateTime.UtcNow
        });

    // ---------------- harness helpers ----------------

    private static ClaimsPrincipal UserWith(long? roleId, long? businessUnitId)
    {
        var claims = new List<Claim>();
        if (roleId.HasValue) claims.Add(new Claim("roleId", roleId.Value.ToString()));
        if (businessUnitId.HasValue) claims.Add(new Claim("businessUnitId", businessUnitId.Value.ToString()));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth"));
    }

    private static async Task<bool> RunPermissionCheck(
        ErpRfqAutomationContext ctx, IMemoryCache cache, ClaimsPrincipal user, string module, string action)
    {
        var handler = new PermissionHandler(
            new RolePermissionRepository(ctx),
            new RoleGate(ctx, cache),
            cache,
            NullLogger<PermissionHandler>.Instance);

        var requirement = new PermissionRequirement(module, action);
        var authContext = new AuthorizationHandlerContext(new[] { requirement }, user, null);
        await handler.HandleAsync(authContext);
        return authContext.HasSucceeded;
    }

    private static async Task<bool> RunManagerCheck(ErpRfqAutomationContext ctx, ClaimsPrincipal user)
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var handler = new ManagerRoleHandler(new RoleGate(ctx, cache), NullLogger<ManagerRoleHandler>.Instance);
        var requirement = new ManagerRoleRequirement();
        var authContext = new AuthorizationHandlerContext(new[] { requirement }, user, null);
        await handler.HandleAsync(authContext);
        return authContext.HasSucceeded;
    }

    // ---------------- module-permission checks ----------------

    [Fact]
    public async Task View_Allowed_WhenAnyPermissionRowExists()
    {
        using var db = new TestDb();
        using var ctx = db.ContextFor(Bu);
        SeedRole(ctx, 10, "Sales Executive");
        SeedModule(ctx, ModuleLeadsId, ModuleLeads);
        // Row exists but every action flag is false — view is still granted,
        // exactly like the frontend ("if they are in the list, they can at least view").
        SeedPermission(ctx, 9001, roleId: 10, moduleId: ModuleLeadsId);
        ctx.SaveChanges();

        using var cache = new MemoryCache(new MemoryCacheOptions());
        Assert.True(await RunPermissionCheck(ctx, cache, UserWith(10, Bu), ModuleLeads, "CanView"));
    }

    [Fact]
    public async Task Edit_Allowed_WhenCanEditTrue()
    {
        using var db = new TestDb();
        using var ctx = db.ContextFor(Bu);
        SeedRole(ctx, 10, "Sales Executive");
        SeedModule(ctx, ModuleLeadsId, ModuleLeads);
        SeedPermission(ctx, 9001, roleId: 10, moduleId: ModuleLeadsId, canEdit: true);
        ctx.SaveChanges();

        using var cache = new MemoryCache(new MemoryCacheOptions());
        Assert.True(await RunPermissionCheck(ctx, cache, UserWith(10, Bu), ModuleLeads, "CanEdit"));
    }

    [Fact]
    public async Task Edit_Denied_WhenCanEditFalse()
    {
        using var db = new TestDb();
        using var ctx = db.ContextFor(Bu);
        SeedRole(ctx, 10, "Sales Executive");
        SeedModule(ctx, ModuleLeadsId, ModuleLeads);
        SeedPermission(ctx, 9001, roleId: 10, moduleId: ModuleLeadsId, canCreate: true, canEdit: false);
        ctx.SaveChanges();

        using var cache = new MemoryCache(new MemoryCacheOptions());
        Assert.False(await RunPermissionCheck(ctx, cache, UserWith(10, Bu), ModuleLeads, "CanEdit"));
    }

    [Fact]
    public async Task Denied_WhenNoPermissionRow_FailClosed()
    {
        using var db = new TestDb();
        using var ctx = db.ContextFor(Bu);
        SeedRole(ctx, 10, "Sales Executive");
        SeedModule(ctx, ModuleLeadsId, ModuleLeads);
        // No RolePermissions row at all for this role/module.
        ctx.SaveChanges();

        using var cache = new MemoryCache(new MemoryCacheOptions());
        Assert.False(await RunPermissionCheck(ctx, cache, UserWith(10, Bu), ModuleLeads, "CanView"));
        Assert.False(await RunPermissionCheck(ctx, cache, UserWith(10, Bu), ModuleLeads, "CanDelete"));
    }

    [Fact]
    public async Task Denied_WhenRoleOrBusinessUnitClaimMissing()
    {
        using var db = new TestDb();
        using var ctx = db.ContextFor(Bu);
        SeedRole(ctx, 10, "Administrator");
        SeedModule(ctx, ModuleLeadsId, ModuleLeads);
        SeedPermission(ctx, 9001, roleId: 10, moduleId: ModuleLeadsId, canEdit: true);
        ctx.SaveChanges();

        using var cache = new MemoryCache(new MemoryCacheOptions());
        Assert.False(await RunPermissionCheck(ctx, cache, UserWith(null, Bu), ModuleLeads, "CanEdit"));
        Assert.False(await RunPermissionCheck(ctx, cache, UserWith(10, null), ModuleLeads, "CanEdit"));
    }

    /// <summary>
    /// The reason the deleted super-admin BYPASS existed: an administrator must never be locked out
    /// of their own tenant by a missing or partial RolePermissions row. The rank rule satisfies that
    /// directly — and, unlike the bypass, it says which tier it grants and gets that tier from a
    /// column instead of from the role's name.
    /// </summary>
    [Theory]
    [InlineData(RoleRanks.Admin)]
    [InlineData(RoleRanks.Owner)]
    public async Task RankAtOrAboveAdmin_SatisfiesModuleChecks_WithNoPermissionRows(short rank)
    {
        using var db = new TestDb();
        using var ctx = db.ContextFor(Bu);
        // A deliberately unremarkable name: the grant comes from the rank, nothing else.
        SeedRole(ctx, 99, "Procurement Lead", rank: rank);
        SeedModule(ctx, ModuleLeadsId, ModuleLeads);
        // Deliberately NO RolePermissions rows.
        ctx.SaveChanges();

        using var cache = new MemoryCache(new MemoryCacheOptions());
        Assert.True(await RunPermissionCheck(ctx, cache, UserWith(99, Bu), ModuleLeads, "CanDelete"));
        Assert.True(await RunPermissionCheck(ctx, cache, UserWith(99, Bu), ModuleLeads, "CanView"));
    }

    /// <summary>Manager is BELOW Admin, so it does not satisfy a module check by rank — it still
    /// needs a RolePermissions row. The old rule could not express this: "admin" and "manager"
    /// were the same bucket.</summary>
    [Fact]
    public async Task ManagerRank_DoesNotSatisfyModuleChecks_WithoutPermissionRows()
    {
        using var db = new TestDb();
        using var ctx = db.ContextFor(Bu);
        SeedRole(ctx, 20, "Sales Manager", rank: RoleRanks.Manager);
        SeedModule(ctx, ModuleLeadsId, ModuleLeads);
        ctx.SaveChanges();

        using var cache = new MemoryCache(new MemoryCacheOptions());
        Assert.False(await RunPermissionCheck(ctx, cache, UserWith(20, Bu), ModuleLeads, "CanDelete"));
    }

    /// <summary>
    /// THE REGRESSION THIS CHANGE EXISTS FOR. Every name below satisfied the deleted rule
    /// <c>contains "super" AND contains "admin"</c> and therefore owned the tenant outright. They
    /// are ordinary industrial/energy procurement job titles, not attacks. With rank stored
    /// explicitly and left at Member, they grant exactly nothing.
    /// </summary>
    [Theory]
    [InlineData("Supervisor Admin")]
    [InlineData("Superintendent Administrator")]
    [InlineData("Site Supervisor - Admin")]
    [InlineData("Super Admin")]              // even the canonical name is inert without the rank
    [InlineData("Super_Administrator")]
    public async Task PrivilegedLookingName_WithMemberRank_GrantsNothing(string roleName)
    {
        using var db = new TestDb();
        using var ctx = db.ContextFor(Bu);
        SeedRole(ctx, 77, roleName, rank: RoleRanks.Member);
        SeedModule(ctx, ModuleLeadsId, ModuleLeads);
        ctx.SaveChanges();

        using var cache = new MemoryCache(new MemoryCacheOptions());
        var gate = new RoleGate(ctx, cache);

        Assert.False(await gate.IsSuperAdminAsync(77, Bu));
        Assert.False(await gate.IsManagerOrAdminAsync(77, Bu));
        Assert.Equal(RoleRanks.Member, await gate.GetRoleRankAsync(77, Bu));

        // …and the two gates that used to be handed the whole tenant on the strength of the name.
        Assert.False(await RunPermissionCheck(ctx, cache, UserWith(77, Bu), ModuleLeads, "CanView"));
        Assert.False(await RunManagerCheck(ctx, UserWith(77, Bu)));
    }

    /// <summary>The same names at Owner rank DO grant — proving the assertions above fail because
    /// of the rank and not because the test harness is inert.</summary>
    [Theory]
    [InlineData("Supervisor Admin")]
    [InlineData("Site Supervisor - Admin")]
    public async Task SameName_AtOwnerRank_IsSuperAdmin(string roleName)
    {
        using var db = new TestDb();
        using var ctx = db.ContextFor(Bu);
        SeedRole(ctx, 77, roleName, rank: RoleRanks.Owner);
        SeedModule(ctx, ModuleLeadsId, ModuleLeads);
        ctx.SaveChanges();

        using var cache = new MemoryCache(new MemoryCacheOptions());
        var gate = new RoleGate(ctx, cache);

        Assert.True(await gate.IsSuperAdminAsync(77, Bu));
        Assert.True(await RunPermissionCheck(ctx, cache, UserWith(77, Bu), ModuleLeads, "CanDelete"));
    }

    /// <summary>Rank is TENANT-OWNED. An Owner-ranked role in business unit 2 grants nothing to a
    /// token that says business unit 1, even though the roleId resolves to a real row.</summary>
    [Fact]
    public async Task ForeignTenantOwnerRank_GrantsNothing()
    {
        using var db = new TestDb();
        using var ctx = db.ContextFor(Bu);
        SeedRole(ctx, 99, "Super Admin", businessUnitId: 2, rank: RoleRanks.Owner);
        SeedModule(ctx, ModuleLeadsId, ModuleLeads);
        ctx.SaveChanges();

        using var cache = new MemoryCache(new MemoryCacheOptions());
        var gate = new RoleGate(ctx, cache);

        Assert.Equal(RoleRanks.Member, await gate.GetRoleRankAsync(99, Bu));
        Assert.False(await gate.IsSuperAdminAsync(99, Bu));
        Assert.False(await gate.IsManagerOrAdminAsync(99, Bu));
        Assert.False(await RunPermissionCheck(ctx, cache, UserWith(99, Bu), ModuleLeads, "CanDelete"));
        Assert.False(await RunManagerCheck(ctx, UserWith(99, Bu)));

        // The very same role, read through a context scoped to ITS OWN tenant, is an owner — so the
        // denials above are the tenant boundary, not a missing rank.
        using var foreignCtx = db.ContextFor(2);
        using var foreignCache = new MemoryCache(new MemoryCacheOptions());
        Assert.True(await new RoleGate(foreignCtx, foreignCache).IsSuperAdminAsync(99, 2));
    }

    [Fact]
    public async Task ForeignTenantPermissionRow_DoesNotGrantAccess()
    {
        using var db = new TestDb();
        using var ctx = db.ContextFor(Bu);
        SeedRole(ctx, 10, "Sales Executive", businessUnitId: 2);
        SeedModule(ctx, ModuleLeadsId, ModuleLeads);
        SeedPermission(ctx, 9001, 10, ModuleLeadsId, canEdit: true, businessUnitId: 2);
        ctx.SaveChanges();

        using var cache = new MemoryCache(new MemoryCacheOptions());
        Assert.False(await RunPermissionCheck(ctx, cache, UserWith(10, Bu), ModuleLeads, "CanEdit"));
    }

    [Fact]
    public async Task PermissionLookup_IsCached_WithinTtl()
    {
        using var db = new TestDb();
        using var ctx = db.ContextFor(Bu);
        SeedRole(ctx, 10, "Sales Executive");
        SeedModule(ctx, ModuleLeadsId, ModuleLeads);
        SeedPermission(ctx, 9001, roleId: 10, moduleId: ModuleLeadsId, canEdit: true);
        ctx.SaveChanges();

        using var cache = new MemoryCache(new MemoryCacheOptions());
        Assert.True(await RunPermissionCheck(ctx, cache, UserWith(10, Bu), ModuleLeads, "CanEdit"));

        // Revoke the permission in the DB. The SAME cache still allows (60s TTL)…
        var row = ctx.RolePermissions.Single(rp => rp.Id == 9001);
        ctx.RolePermissions.Remove(row);
        ctx.SaveChanges();
        Assert.True(await RunPermissionCheck(ctx, cache, UserWith(10, Bu), ModuleLeads, "CanEdit"));

        // …while a fresh cache (i.e. after expiry) re-queries and denies.
        using var freshCache = new MemoryCache(new MemoryCacheOptions());
        Assert.False(await RunPermissionCheck(ctx, freshCache, UserWith(10, Bu), ModuleLeads, "CanEdit"));
    }

    // ---------------- manager/admin gate ----------------

    [Fact]
    public async Task ManagerGate_Allows_ManagerRank()
    {
        using var db = new TestDb();
        using var ctx = db.ContextFor(Bu);
        SeedRole(ctx, 20, "Sales Manager", rank: RoleRanks.Manager);
        ctx.SaveChanges();

        Assert.True(await RunManagerCheck(ctx, UserWith(20, Bu)));
    }

    [Fact]
    public async Task ManagerGate_Allows_OwnerRank()
    {
        using var db = new TestDb();
        using var ctx = db.ContextFor(Bu);
        SeedRole(ctx, 99, "Super Admin", rank: RoleRanks.Owner);
        ctx.SaveChanges();

        Assert.True(await RunManagerCheck(ctx, UserWith(99, Bu)));
    }

    [Fact]
    public async Task ManagerGate_Denies_MemberRank_AndUnknownRole()
    {
        using var db = new TestDb();
        using var ctx = db.ContextFor(Bu);
        SeedRole(ctx, 10, "Sales Executive");
        ctx.SaveChanges();

        Assert.False(await RunManagerCheck(ctx, UserWith(10, Bu)));
        Assert.False(await RunManagerCheck(ctx, UserWith(12345, Bu))); // no such role → fail-closed
        Assert.False(await RunManagerCheck(ctx, UserWith(null, Bu)));  // no roleId claim → fail-closed
    }

    // ---------------- dynamic policy plumbing ----------------

    [Fact]
    public void Attribute_PolicyName_RoundTrips()
    {
        var attr = new RequireModulePermissionAttribute("Supplier History", PermissionAction.Edit);
        Assert.Equal("ModulePermission:Supplier History:Edit", attr.Policy);

        Assert.True(RequireModulePermissionAttribute.TryParsePolicy(attr.Policy, out var module, out var action));
        Assert.Equal("Supplier History", module);
        Assert.Equal(PermissionAction.Edit, action);

        Assert.False(RequireModulePermissionAttribute.TryParsePolicy("CanDeleteRFQ", out _, out _));
        Assert.False(RequireModulePermissionAttribute.TryParsePolicy("ModulePermission::View", out _, out _));
    }

    [Fact]
    public async Task PolicyProvider_BuildsModulePolicy_AndFallsBackForOtherNames()
    {
        var options = new AuthorizationOptions();
        options.AddPolicy("Legacy", p => p.RequireAuthenticatedUser());
        var provider = new ModulePermissionPolicyProvider(Options.Create(options));

        var modulePolicy = await provider.GetPolicyAsync("ModulePermission:RFQ Management:Delete");
        Assert.NotNull(modulePolicy);
        var requirement = Assert.Single(modulePolicy!.Requirements.OfType<PermissionRequirement>());
        Assert.Equal("RFQ Management", requirement.ModuleName);
        Assert.Equal("CanDelete", requirement.Action);

        var managerPolicy = await provider.GetPolicyAsync(RequireManagerRoleAttribute.PolicyName);
        Assert.NotNull(managerPolicy);
        Assert.Single(managerPolicy!.Requirements.OfType<ManagerRoleRequirement>());

        Assert.NotNull(await provider.GetPolicyAsync("Legacy"));   // conventional policies still resolve
        Assert.Null(await provider.GetPolicyAsync("Unregistered")); // unknown names stay unknown
    }
}
