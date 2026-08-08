using System.Security.Claims;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.MultiTenancy;
using ERP_RFQ_Automation.Platform.Controllers;
using ERP_RFQ_Automation.Platform.Models;
using ERP_RFQ_Automation.Platform.Lifecycle;
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

    [Fact]
    public async Task Restore_atomically_cancels_a_scheduled_deletion()
    {
        using var db = new TestDb();
        var tenantId = await SeedTenant(db, "archived-pending-deletion", TenantStatus.Archived);
        await using (var seed = db.ContextFor(null))
        {
            seed.Set<TenantOffboarding>().Add(new TenantOffboarding
            {
                TenantId = tenantId,
                Stage = TenantOffboardingStage.PendingDeletion,
                RetentionDays = 30,
                DeletionScheduledOn = DateTime.UtcNow.AddDays(-1),
                PurgeEligibleOn = DateTime.UtcNow.AddDays(29),
                DeletionReason = "Customer requested offboarding",
                DeletionScheduledBy = "owner@example.test"
            });
            await seed.SaveChangesAsync();
        }

        await using var context = db.ContextFor(null);
        var result = await Controller(context).Restore(tenantId,
            new TenantStatusChangeRequest { Reason = "Customer returned" }, CancellationToken.None);

        Assert.IsType<OkObjectResult>(result.Result);
        await using var verification = db.ContextFor(null);
        var record = await verification.Set<TenantOffboarding>().SingleAsync();
        Assert.Equal(TenantOffboardingStage.NotScheduled, record.Stage);
        Assert.Null(record.DeletionScheduledOn);
        Assert.Null(record.PurgeEligibleOn);
        Assert.Contains("\"deletionCancelled\":true",
            (await verification.Set<PlatformAuditLog>().SingleAsync()).Metadata);
    }

    [Fact]
    public async Task Restore_is_refused_after_a_purge_has_claimed_the_tenant()
    {
        using var db = new TestDb();
        var tenantId = await SeedTenant(db, "archived-purge-started", TenantStatus.Archived);
        await using (var seed = db.ContextFor(null))
        {
            seed.Set<TenantOffboarding>().Add(new TenantOffboarding
            {
                TenantId = tenantId,
                Stage = TenantOffboardingStage.PendingDeletion,
                PurgeStartedOn = DateTime.UtcNow,
                DeletionScheduledOn = DateTime.UtcNow.AddDays(-31),
                PurgeEligibleOn = DateTime.UtcNow.AddDays(-1)
            });
            await seed.SaveChangesAsync();
        }

        await using var context = db.ContextFor(null);
        var result = await Controller(context).Restore(tenantId,
            new TenantStatusChangeRequest { Reason = "Customer returned" }, CancellationToken.None);

        var conflict = Assert.IsType<ConflictObjectResult>(result.Result);
        Assert.Contains("purge is already in progress", conflict.Value!.ToString(), StringComparison.OrdinalIgnoreCase);
        await using var verification = db.ContextFor(null);
        Assert.Equal(TenantStatus.Archived,
            (await verification.Set<Tenant>().IgnoreQueryFilters().SingleAsync(t => t.Id == tenantId)).Status);
        Assert.Empty(await verification.Set<PlatformAuditLog>().ToListAsync());
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

    [Fact]
    public async Task Legal_hold_is_attributable_unique_while_active_and_releasable()
    {
        using var db = new TestDb();
        var tenantId = await SeedTenant(db, "legal-hold-tenant", TenantStatus.Archived);
        await using var context = db.ContextFor(null);
        var service = LegalHolds(context);
        var actor = PlatformActor();

        var hold = await service.PlaceAsync(tenantId, new PlaceTenantLegalHoldRequest
        {
            Scope = "AllTenantData",
            Authority = "Litigation counsel",
            Reason = "Preserve all records for pending litigation.",
            EvidenceReference = "case://NEX-2026-1042"
        }, actor, null, CancellationToken.None);

        Assert.True(hold.IsActive);
        Assert.Equal("operator@example.test", hold.PlacedBy);
        await Assert.ThrowsAsync<TenantOffboardingRefusedException>(() =>
            service.PlaceAsync(tenantId, new PlaceTenantLegalHoldRequest
            {
                Scope = "AllTenantData",
                Authority = "Litigation counsel",
                Reason = "A duplicate active hold must be refused.",
                EvidenceReference = "case://NEX-2026-1043"
            }, actor, null, CancellationToken.None));

        var released = await service.ReleaseAsync(tenantId, hold.Id,
            new ReleaseTenantLegalHoldRequest
            {
                Reason = "Counsel confirmed the preservation duty has ended."
            }, actor, null, CancellationToken.None);

        Assert.False(released.IsActive);
        Assert.NotNull(released.ReleasedOn);
        await using var verify = db.ContextFor(null);
        Assert.Equal(2, await verify.Set<PlatformAuditLog>().CountAsync());
    }

    [Fact]
    public async Task Legal_hold_is_refused_after_destructive_execution_committed()
    {
        using var db = new TestDb();
        var tenantId = await SeedTenant(db, "legal-hold-too-late", TenantStatus.Archived);
        await using (var seed = db.ContextFor(null))
        {
            seed.Set<TenantOffboarding>().Add(new TenantOffboarding
            {
                TenantId = tenantId,
                Stage = TenantOffboardingStage.PendingDeletion,
                PurgeStartedOn = DateTime.UtcNow.AddMinutes(-1),
                PurgeAttemptId = Guid.NewGuid(),
                PurgeExecutedOn = DateTime.UtcNow,
                PurgeExecutedRowCount = 12,
                PurgeExecutionDetail = "[]"
            });
            await seed.SaveChangesAsync();
        }

        await using var context = db.ContextFor(null);
        var refusal = await Assert.ThrowsAsync<TenantOffboardingRefusedException>(() =>
            LegalHolds(context).PlaceAsync(tenantId, new PlaceTenantLegalHoldRequest
            {
                Scope = "AllTenantData",
                Authority = "Regulatory order",
                Reason = "Preserve all records under regulator instruction.",
                EvidenceReference = "regulator://order-77"
            }, PlatformActor(), null, CancellationToken.None));

        Assert.Equal(409, refusal.SuggestedStatusCode);
        Assert.Contains("destructive execution", refusal.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Legal_hold_is_refused_after_personal_data_erasure_committed()
    {
        using var db = new TestDb();
        var tenantId = await SeedTenant(db, "legal-hold-after-erasure", TenantStatus.Archived);
        await using (var seed = db.ContextFor(null))
        {
            seed.Set<TenantOffboarding>().Add(new TenantOffboarding
            {
                TenantId = tenantId,
                Stage = TenantOffboardingStage.PendingDeletion,
                PersonalDataErasedOn = DateTime.UtcNow,
                PersonalDataErasedBy = "privacy@nexora.test"
            });
            await seed.SaveChangesAsync();
        }

        await using var context = db.ContextFor(null);
        var refusal = await Assert.ThrowsAsync<TenantOffboardingRefusedException>(() =>
            LegalHolds(context).PlaceAsync(tenantId, new PlaceTenantLegalHoldRequest
            {
                Scope = "AllTenantData",
                Authority = "Regulatory order",
                Reason = "Preserve all records under regulator instruction.",
                EvidenceReference = "regulator://order-after-erasure"
            }, PlatformActor(), null, CancellationToken.None));

        Assert.Equal(409, refusal.SuggestedStatusCode);
        Assert.Contains("destructive execution", refusal.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(await context.Set<TenantLegalHold>().ToListAsync());
    }

    private static TenantsController Controller(ErpRfqAutomationContext context)
    {
        var services = new ServiceCollection().BuildServiceProvider();
        return new TenantsController(
            context,
            new PlatformAuditService(context, NullLogger<PlatformAuditService>.Instance),
            NullLogger<TenantsController>.Instance,
            services.GetRequiredService<IServiceScopeFactory>(),
            new TenantScopeAccessor(),
            ProvisioningFixture.Baseline(context),
            ProvisioningFixture.Invitations(context))
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = PlatformActor() }
            }
        };
    }

    private static TenantLegalHoldService LegalHolds(ErpRfqAutomationContext context) =>
        new(context, new PlatformAuditService(context, NullLogger<PlatformAuditService>.Instance));

    private static ClaimsPrincipal PlatformActor() => new(new ClaimsIdentity(
    [
        new Claim("sub", "7"),
        new Claim("email", "operator@example.test"), new Claim("platformRole", "Owner")
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
