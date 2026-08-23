using System.Security.Claims;
using System.Text.Json;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.MultiTenancy;
using ERP_RFQ_Automation.Platform.Controllers;
using ERP_RFQ_Automation.Platform.Entitlements;
using ERP_RFQ_Automation.Platform.Models;
using ERP_RFQ_Automation.Platform.Services;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// The per-customer module grant endpoints added with 20260818013530.
///
/// <para>What these are really guarding is a change of AUTHORITY: module access used to be a
/// property of the plan, so two customers on one plan could not differ and revoking one module
/// from one customer meant re-planning them — which moved their seats, their quota and their
/// price with it. These assertions pin the new authority, the audit record that explains a
/// revoke, and the enforcement-cache eviction without which a revoke appears not to work for a
/// minute.</para>
/// </summary>
public sealed class TenantModuleGrantAdminTests
{
    [Fact]
    public async Task Reading_modules_reports_the_grant_the_runtime_uses_and_the_plan_it_differs_from()
    {
        using var db = new TestDb();
        var tenantId = await Seed(db, granted: [TypedEntitlementCatalog.Rfq, TypedEntitlementCatalog.Procurement],
            planFeatures: [TypedEntitlementCatalog.Rfq, TypedEntitlementCatalog.Inventory]);

        await using var context = db.ContextFor(null);
        var response = await Controller(context).GetModules(tenantId, default);
        var view = Assert.IsType<TenantModulesDto>(Assert.IsType<OkObjectResult>(response.Result).Value);

        var procurement = view.Modules.Single(m => m.Key == TypedEntitlementCatalog.Procurement);
        Assert.True(procurement.Enabled);
        // Granted beyond the plan. The console draws "Added beyond plan" from exactly this pair,
        // which is how a deliberate exception stays legible a year after somebody made it.
        Assert.False(procurement.FromPlanTemplate);

        var inventory = view.Modules.Single(m => m.Key == TypedEntitlementCatalog.Inventory);
        Assert.False(inventory.Enabled);
        Assert.True(inventory.FromPlanTemplate);

        // Five keys have no execution boundary; the console must not offer them as switches.
        Assert.False(view.Modules.Single(m => m.Key == TypedEntitlementCatalog.Sso).Available);
        Assert.True(view.Modules.Single(m => m.Key == TypedEntitlementCatalog.Orders).Available);

        // Presentation order is server-owned so the console never has to re-derive one and drift.
        Assert.Equal(TypedEntitlementCatalog.OrderedKeys, view.Modules.Select(m => m.Key).ToArray());
    }

    [Fact]
    public async Task Revoking_a_module_stores_the_whole_catalogue_and_audits_the_delta_with_its_reason()
    {
        using var db = new TestDb();
        var tenantId = await Seed(db,
            granted: [TypedEntitlementCatalog.Rfq, TypedEntitlementCatalog.Quotes, TypedEntitlementCatalog.Inventory],
            planFeatures: [TypedEntitlementCatalog.Rfq]);

        await using var context = db.ContextFor(null);
        var response = await Controller(context).UpdateModules(tenantId, new UpdateTenantModulesRequest
        {
            Modules = new Dictionary<string, bool>
            {
                [TypedEntitlementCatalog.Rfq] = true,
                [TypedEntitlementCatalog.Quotes] = true,
                [TypedEntitlementCatalog.Orders] = true,
                [TypedEntitlementCatalog.Inventory] = false
            },
            Reason = "Customer downgraded inventory and bought orders at renewal"
        }, default);
        Assert.IsType<OkObjectResult>(response.Result);

        await using var read = db.ContextFor(null);
        var tenant = await read.Set<Tenant>().IgnoreQueryFilters().SingleAsync(t => t.Id == tenantId);

        // Every catalogue key present and explicitly decided. An absent key reads the same as false
        // to IsEnabled but NOT to entitlements.typed-hard-limits, which requires presence — that
        // asymmetry is what left tenants permanently unactivatable behind a plan carrying {}.
        Assert.True(TypedEntitlementCatalog.TryParse(tenant.Entitlements, out var stored, out _));
        Assert.Equal(TypedEntitlementCatalog.Keys.Count, stored.Count);
        Assert.True(stored[TypedEntitlementCatalog.Orders]);
        Assert.False(stored[TypedEntitlementCatalog.Inventory]);

        var audit = await read.Set<PlatformAuditLog>().AsNoTracking()
            .SingleAsync(x => x.Action == "tenant.modules.update");
        Assert.Equal(tenantId.ToString(), audit.TargetId);

        using var metadata = JsonDocument.Parse(audit.Metadata!);
        Assert.Contains("renewal", metadata.RootElement.GetProperty("reason").GetString());
        Assert.Equal(TypedEntitlementCatalog.Orders,
            metadata.RootElement.GetProperty("granted").EnumerateArray().Single().GetString());
        Assert.Equal(TypedEntitlementCatalog.Inventory,
            metadata.RootElement.GetProperty("revoked").EnumerateArray().Single().GetString());
        // The full before AND after state, not just the delta: a dispute about what a customer was
        // entitled to on a given day is answered by state, and nobody reconstructs it from deltas.
        Assert.True(metadata.RootElement.GetProperty("before")
            .GetProperty(TypedEntitlementCatalog.Inventory).GetBoolean());
        Assert.False(metadata.RootElement.GetProperty("after")
            .GetProperty(TypedEntitlementCatalog.Inventory).GetBoolean());
    }

    [Fact]
    public async Task A_revoke_takes_effect_immediately_rather_than_after_the_cache_window()
    {
        using var db = new TestDb();
        const long businessUnitId = 4_401;
        var tenantId = await Seed(db, granted: [TypedEntitlementCatalog.Orders],
            planFeatures: [TypedEntitlementCatalog.Orders], businessUnitId: businessUnitId);

        await using var context = db.ContextFor(null);
        var access = new TenantAccessService(context, new MemoryCache(new MemoryCacheOptions()),
            NullLogger<TenantAccessService>.Instance);
        var entitlements = new EntitlementService(access, context);

        // Warm the ~60s snapshot cache, exactly as a live request would have.
        Assert.True((await entitlements.CheckFeatureAsync(businessUnitId, TypedEntitlementCatalog.Orders)).Allowed);

        await Controller(context, access).UpdateModules(tenantId, new UpdateTenantModulesRequest
        {
            Modules = new Dictionary<string, bool> { [TypedEntitlementCatalog.Orders] = false },
            Reason = "Contract terminated for the orders module"
        }, default);

        // Without the Evict this stays Allowed for up to a minute — which is precisely the window
        // in which the operator checks whether the revoke worked, sees that it did not, and
        // presses it again.
        Assert.False((await entitlements.CheckFeatureAsync(businessUnitId, TypedEntitlementCatalog.Orders)).Allowed);
    }

    [Fact]
    public async Task An_unknown_key_is_refused_by_name_rather_than_silently_dropped()
    {
        using var db = new TestDb();
        var tenantId = await Seed(db, granted: [], planFeatures: []);

        await using var context = db.ContextFor(null);
        var response = await Controller(context).UpdateModules(tenantId, new UpdateTenantModulesRequest
        {
            Modules = new Dictionary<string, bool> { ["module.reporting"] = true },
            Reason = "Enabling the reporting module for this customer"
        }, default);

        var problem = Assert.IsType<BadRequestObjectResult>(response.Result);
        Assert.Contains("module.reporting", problem.Value!.ToString());
    }

    [Fact]
    public async Task A_reason_too_short_to_explain_a_revoke_is_refused()
    {
        using var db = new TestDb();
        var tenantId = await Seed(db, granted: [TypedEntitlementCatalog.Rfq], planFeatures: []);

        await using var context = db.ContextFor(null);
        var response = await Controller(context).UpdateModules(tenantId, new UpdateTenantModulesRequest
        {
            Modules = new Dictionary<string, bool> { [TypedEntitlementCatalog.Rfq] = false },
            Reason = "cleanup"
        }, default);

        Assert.IsType<BadRequestObjectResult>(response.Result);

        // ...and nothing was written. A refused request that half-applied would be worse than one
        // that failed outright.
        await using var read = db.ContextFor(null);
        var tenant = await read.Set<Tenant>().IgnoreQueryFilters().SingleAsync(t => t.Id == tenantId);
        Assert.True(TypedEntitlementCatalog.IsEnabled(tenant.Entitlements, TypedEntitlementCatalog.Rfq));
    }

    // ---- helpers ---------------------------------------------------------

    private static async Task<long> Seed(
        TestDb db, string[] granted, string[] planFeatures, long businessUnitId = 4_400)
    {
        await using var seed = db.ContextFor(null);
        Support.Seed.EnsureBusinessUnit(seed, businessUnitId);

        var plan = new Plan
        {
            Code = $"plan-{Guid.NewGuid():N}",
            Name = "Growth",
            MaxSeats = 25,
            MaxDocsPerMonth = 5_000,
            MaxConcurrentExtractionJobs = 4,
            Weight = 3,
            Features = Serialize(planFeatures)
        };
        seed.Set<Plan>().Add(plan);
        await seed.SaveChangesAsync();

        var tenant = new Tenant
        {
            Name = "Module Tenant",
            Slug = $"module-tenant-{Guid.NewGuid():N}"[..24],
            Status = TenantStatus.Active,
            PlanId = plan.Id,
            PrimaryBusinessUnitId = businessUnitId,
            Entitlements = Serialize(granted),
            CreatedBy = "test",
            CreatedOn = DateTime.UtcNow
        };
        seed.Set<Tenant>().Add(tenant);
        await seed.SaveChangesAsync();
        return tenant.Id;
    }

    private static string Serialize(string[] enabled) => JsonSerializer.Serialize(
        TypedEntitlementCatalog.OrderedKeys.ToDictionary(key => key, enabled.Contains));

    private static TenantsController Controller(
        ErpRfqAutomationContext context, ITenantAccessService? access = null)
    {
        var services = new ServiceCollection().BuildServiceProvider();
        return new TenantsController(
            context,
            new PlatformAuditService(context, NullLogger<PlatformAuditService>.Instance),
            NullLogger<TenantsController>.Instance,
            services.GetRequiredService<IServiceScopeFactory>(),
            new TenantScopeAccessor(),
            ProvisioningFixture.Baseline(context),
            ProvisioningFixture.Invitations(context),
            access)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = PlatformActor() }
            }
        };
    }

    private static ClaimsPrincipal PlatformActor() => new(new ClaimsIdentity(
    [
        new Claim("sub", "7"),
        new Claim("email", "billing@example.test"),
        new Claim("platformRole", "BillingAdmin")
    ], "Platform"));
}
