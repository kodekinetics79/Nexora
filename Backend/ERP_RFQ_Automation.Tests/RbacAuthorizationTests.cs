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
///   View = row-exists, Create/Edit/Delete = the row's flags, super-admin bypass,
///   60s IMemoryCache on the lookup.
/// - ManagerRoleHandler ([RequireManagerRole]): role name contains admin/manager.
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

    private static void SeedRole(ErpRfqAutomationContext ctx, long roleId, string roleName)
    {
        Seed.EnsureBusinessUnit(ctx, Bu);
        ctx.SetupMasters.Add(new SetupMaster
        {
            SetupId = roleId,
            SetupType = "role",
            SetupCode = roleName,
            SetupValue = roleName,
            BusinessUnitId = Bu,
            IsActive = true,
            CreatedBy = "seed",
            CreatedOn = DateTime.UtcNow
        });
    }

    private static void SeedPermission(
        ErpRfqAutomationContext ctx, long id, long roleId, long moduleId,
        bool canCreate = false, bool canEdit = false, bool canDelete = false)
        => ctx.RolePermissions.Add(new RolePermission
        {
            Id = id,
            RoleId = roleId,
            ModuleId = moduleId,
            BusinessUnitId = Bu,
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

    [Fact]
    public async Task SuperAdmin_BypassesModuleChecks_EvenWithoutPermissionRows()
    {
        using var db = new TestDb();
        using var ctx = db.ContextFor(Bu);
        SeedRole(ctx, 99, "Super_Administrator");
        SeedModule(ctx, ModuleLeadsId, ModuleLeads);
        // Deliberately NO RolePermissions rows: the bypass must still grant access.
        ctx.SaveChanges();

        using var cache = new MemoryCache(new MemoryCacheOptions());
        Assert.True(await RunPermissionCheck(ctx, cache, UserWith(99, Bu), ModuleLeads, "CanDelete"));
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
    public async Task ManagerGate_Allows_ManagerRole()
    {
        using var db = new TestDb();
        using var ctx = db.ContextFor(Bu);
        SeedRole(ctx, 20, "Sales Manager");
        ctx.SaveChanges();

        Assert.True(await RunManagerCheck(ctx, UserWith(20, Bu)));
    }

    [Fact]
    public async Task ManagerGate_Allows_SuperAdminRole()
    {
        using var db = new TestDb();
        using var ctx = db.ContextFor(Bu);
        SeedRole(ctx, 99, "Super Admin");
        ctx.SaveChanges();

        Assert.True(await RunManagerCheck(ctx, UserWith(99, Bu)));
    }

    [Fact]
    public async Task ManagerGate_Denies_NonManagerRole_AndUnknownRole()
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
