using System.Security.Claims;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.MultiTenancy;
using ERP_RFQ_Automation.Platform.Controllers;
using ERP_RFQ_Automation.Platform.Models;
using ERP_RFQ_Automation.Platform.Services;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// Tenant lifecycle completion (Platform Admin 360): archive / restore endpoints,
/// the validated transition graph Active &lt;-&gt; Suspended &lt;-&gt; Archived, and
/// the audited plan-change endpoint.
/// </summary>
public sealed class PlatformTenantLifecycleAdminTests
{
    [Fact]
    public async Task Archive_moves_a_suspended_tenant_to_archived_and_audits()
    {
        using var db = new TestDb();
        var tenantId = await SeedTenant(db, "suspended-tenant", TenantStatus.Suspended);
        await using var context = db.ContextFor(null);
        var controller = Controller(context);

        var result = await controller.Archive(tenantId,
            new TenantStatusChangeRequest { Reason = "Contract ended" }, CancellationToken.None);

        var dto = Assert.IsType<TenantSummaryDto>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal(nameof(TenantStatus.Archived), dto.Status);

        await using var verification = db.ContextFor(null);
        var persisted = await verification.Set<Tenant>().IgnoreQueryFilters().SingleAsync(t => t.Id == tenantId);
        Assert.Equal(TenantStatus.Archived, persisted.Status);
        Assert.Equal("Contract ended", persisted.StatusReason);
        var audit = await verification.Set<PlatformAuditLog>().SingleAsync();
        Assert.Equal("tenant.archive", audit.Action);
        Assert.Equal(tenantId, audit.ActAsTenantId);
        Assert.Equal(PlatformAuditResults.Success, audit.Result);
    }

    [Theory]
    [InlineData(TenantStatus.Active)]
    [InlineData(TenantStatus.Provisioning)]
    [InlineData(TenantStatus.Archived)]
    public async Task Archive_rejects_any_non_suspended_tenant(TenantStatus current)
    {
        using var db = new TestDb();
        var tenantId = await SeedTenant(db, $"archive-invalid-{current}", current);
        await using var context = db.ContextFor(null);
        var controller = Controller(context);

        var result = await controller.Archive(tenantId,
            new TenantStatusChangeRequest { Reason = "Invalid" }, CancellationToken.None);

        Assert.IsType<ConflictObjectResult>(result.Result);
        await using var verification = db.ContextFor(null);
        Assert.Equal(current,
            (await verification.Set<Tenant>().IgnoreQueryFilters().SingleAsync(t => t.Id == tenantId)).Status);
        Assert.Empty(await verification.Set<PlatformAuditLog>().ToListAsync());
    }

    [Fact]
    public async Task Archive_requires_a_reason()
    {
        using var db = new TestDb();
        var tenantId = await SeedTenant(db, "archive-no-reason", TenantStatus.Suspended);
        await using var context = db.ContextFor(null);

        var result = await Controller(context).Archive(tenantId,
            new TenantStatusChangeRequest { Reason = "   " }, CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task Restore_moves_an_archived_tenant_back_to_suspended_and_audits()
    {
        using var db = new TestDb();
        var tenantId = await SeedTenant(db, "archived-tenant", TenantStatus.Archived);
        await using var context = db.ContextFor(null);

        var result = await Controller(context).Restore(tenantId,
            new TenantStatusChangeRequest { Reason = "Customer returned" }, CancellationToken.None);

        var dto = Assert.IsType<TenantSummaryDto>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal(nameof(TenantStatus.Suspended), dto.Status);
        await using var verification = db.ContextFor(null);
        Assert.Equal("tenant.restore", (await verification.Set<PlatformAuditLog>().SingleAsync()).Action);
    }

    [Theory]
    [InlineData(TenantStatus.Active)]
    [InlineData(TenantStatus.Suspended)]
    [InlineData(TenantStatus.Provisioning)]
    public async Task Restore_rejects_any_non_archived_tenant(TenantStatus current)
    {
        using var db = new TestDb();
        var tenantId = await SeedTenant(db, $"restore-invalid-{current}", current);
        await using var context = db.ContextFor(null);

        var result = await Controller(context).Restore(tenantId,
            new TenantStatusChangeRequest { Reason = "Invalid" }, CancellationToken.None);

        Assert.IsType<ConflictObjectResult>(result.Result);
    }

    [Fact]
    public async Task Suspend_still_requires_an_active_tenant_after_the_lifecycle_refactor()
    {
        using var db = new TestDb();
        var tenantId = await SeedTenant(db, "suspend-archived", TenantStatus.Archived);
        await using var context = db.ContextFor(null);

        var result = await Controller(context).Suspend(tenantId,
            new TenantStatusChangeRequest { Reason = "Nope" }, CancellationToken.None);

        Assert.IsType<ConflictObjectResult>(result.Result);
    }

    [Fact]
    public async Task ChangePlan_assigns_an_active_plan_and_audits()
    {
        using var db = new TestDb();
        long tenantId, planId;
        await using (var seed = db.ContextFor(null))
        {
            var plan = new Plan { Code = "pro", Name = "Pro", MonthlyPriceUsd = 99.50m };
            seed.Set<Plan>().Add(plan);
            var tenant = NewTenant("plan-change-tenant", TenantStatus.Active);
            seed.Set<Tenant>().Add(tenant);
            await seed.SaveChangesAsync();
            tenantId = tenant.Id;
            planId = plan.Id;
        }

        await using var context = db.ContextFor(null);
        var result = await Controller(context).ChangePlan(tenantId,
            new ChangeTenantPlanRequest { PlanId = planId, Reason = "Upgrade" }, CancellationToken.None);

        var dto = Assert.IsType<TenantSummaryDto>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal(planId, dto.PlanId);
        Assert.Equal("pro", dto.PlanCode);

        await using var verification = db.ContextFor(null);
        var audit = await verification.Set<PlatformAuditLog>().SingleAsync();
        Assert.Equal("tenant.plan.change", audit.Action);
        Assert.Equal(tenantId, audit.ActAsTenantId);
        Assert.Contains("\"toPlanId\":" + planId, audit.Metadata);
    }

    [Fact]
    public async Task ChangePlan_rejects_a_nonexistent_plan()
    {
        using var db = new TestDb();
        var tenantId = await SeedTenant(db, "plan-missing-tenant", TenantStatus.Active);
        await using var context = db.ContextFor(null);

        var result = await Controller(context).ChangePlan(tenantId,
            new ChangeTenantPlanRequest { PlanId = 12345 }, CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result.Result);
        await using var verification = db.ContextFor(null);
        Assert.Null((await verification.Set<Tenant>().IgnoreQueryFilters().SingleAsync(t => t.Id == tenantId)).PlanId);
    }

    [Fact]
    public async Task ChangePlan_rejects_an_inactive_plan()
    {
        using var db = new TestDb();
        long tenantId, planId;
        await using (var seed = db.ContextFor(null))
        {
            var plan = new Plan { Code = "legacy", Name = "Legacy", IsActive = false };
            seed.Set<Plan>().Add(plan);
            var tenant = NewTenant("plan-inactive-tenant", TenantStatus.Active);
            seed.Set<Tenant>().Add(tenant);
            await seed.SaveChangesAsync();
            tenantId = tenant.Id;
            planId = plan.Id;
        }

        await using var context = db.ContextFor(null);
        var result = await Controller(context).ChangePlan(tenantId,
            new ChangeTenantPlanRequest { PlanId = planId }, CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task ChangePlan_returns_not_found_for_an_unknown_tenant()
    {
        using var db = new TestDb();
        long planId;
        await using (var seed = db.ContextFor(null))
        {
            var plan = new Plan { Code = "starter", Name = "Starter" };
            seed.Set<Plan>().Add(plan);
            await seed.SaveChangesAsync();
            planId = plan.Id;
        }

        await using var context = db.ContextFor(null);
        var result = await Controller(context).ChangePlan(99999,
            new ChangeTenantPlanRequest { PlanId = planId }, CancellationToken.None);

        Assert.IsType<NotFoundResult>(result.Result);
    }

    private static TenantsController Controller(ErpRfqAutomationContext context)
    {
        var services = new ServiceCollection().BuildServiceProvider();
        return new TenantsController(
            context,
            new PlatformAuditService(context, NullLogger<PlatformAuditService>.Instance),
            NullLogger<TenantsController>.Instance,
            services.GetRequiredService<IServiceScopeFactory>(),
            new TenantScopeAccessor())
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
        new Claim("email", "operator@example.test")
    ], "Platform"));

    private static async Task<long> SeedTenant(TestDb db, string slug, TenantStatus status)
    {
        await using var seed = db.ContextFor(null);
        var tenant = NewTenant(slug, status);
        seed.Set<Tenant>().Add(tenant);
        await seed.SaveChangesAsync();
        return tenant.Id;
    }

    private static Tenant NewTenant(string slug, TenantStatus status) => new()
    {
        Name = "Lifecycle Tenant",
        Slug = slug,
        Status = status,
        CreatedBy = "test",
        CreatedOn = DateTime.UtcNow
    };
}
