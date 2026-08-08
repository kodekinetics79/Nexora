using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using ERP_RFQ_Automation.Extraction;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Platform.Auth;
using ERP_RFQ_Automation.Platform.Controllers;
using ERP_RFQ_Automation.Platform.Models;
using ERP_RFQ_Automation.Platform.Services;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Abstractions;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// Fake-data fixes: /api/platform/plans serves the real MonthlyPriceUsd column,
/// plan create/update are Owner-gated + audited with unique codes, tier bucketing
/// never invents "pro" (absent plan = "none"), the overview no longer mislabels
/// the fleet-wide user count as seats, and /pipeline/jobs survives duplicate
/// primary-business-unit mappings.
/// </summary>
public sealed class PlatformPlansAndOverviewTests
{
    [Fact]
    public async Task Plans_endpoint_returns_the_persisted_monthly_price()
    {
        using var db = new TestDb();
        await using (var seed = db.ContextFor(null))
        {
            seed.Set<Plan>().AddRange(
                new Plan { Code = "free", Name = "Free", Weight = 1, MonthlyPriceUsd = null },
                new Plan { Code = "pro", Name = "Pro", Weight = 2, MonthlyPriceUsd = 149.99m });
            await seed.SaveChangesAsync();
        }

        await using var context = db.ContextFor(null);
        var rows = await Rows(OperationsController(context).Plans(CancellationToken.None));

        var free = rows.Single(r => r.GetProperty("code").GetString() == "free");
        var pro = rows.Single(r => r.GetProperty("code").GetString() == "pro");
        Assert.Equal(JsonValueKind.Null, free.GetProperty("priceMonthlyUsd").ValueKind);
        Assert.Equal(149.99m, pro.GetProperty("priceMonthlyUsd").GetDecimal());
    }

    [Fact]
    public async Task Plans_listing_includes_inactive_plans_with_their_isActive_flag()
    {
        // Platform console requirement: deactivated plans must remain visible (and
        // reactivatable) in the management UI — the listing returns ALL plans and the
        // isActive flag distinguishes them. Assignment paths still reject inactive
        // plans (TenantsController.ChangePlan).
        using var db = new TestDb();
        await using (var seed = db.ContextFor(null))
        {
            seed.Set<Plan>().AddRange(
                new Plan { Code = "live", Name = "Live", Weight = 1, IsActive = true },
                new Plan { Code = "retired", Name = "Retired", Weight = 2, IsActive = false });
            await seed.SaveChangesAsync();
        }

        await using var context = db.ContextFor(null);
        var rows = await Rows(OperationsController(context).Plans(CancellationToken.None));

        Assert.True(rows.Single(r => r.GetProperty("code").GetString() == "live")
            .GetProperty("isActive").GetBoolean());
        Assert.False(rows.Single(r => r.GetProperty("code").GetString() == "retired")
            .GetProperty("isActive").GetBoolean());
    }

    [Fact]
    public void Plan_mutations_require_the_owner_policy()
    {
        foreach (var name in new[]
                 {
                     nameof(PlatformOperationsController.CreatePlan),
                     nameof(PlatformOperationsController.UpdatePlan)
                 })
        {
            var authorize = typeof(PlatformOperationsController).GetMethods()
                .Single(m => m.Name == name)
                .GetCustomAttributes<AuthorizeAttribute>().Single();
            Assert.Equal(PlatformPolicies.Owner, authorize.Policy);
        }
    }

    [Fact]
    public async Task CreatePlan_persists_normalized_code_and_audits()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(null);

        var result = await OperationsController(context).CreatePlan(new UpsertPlanRequest
        {
            Code = "  Scale ",
            Name = "Scale",
            Weight = 4,
            MonthlyPriceUsd = 499.00m
        }, CancellationToken.None);

        Assert.IsType<CreatedAtActionResult>(result);
        await using var verification = db.ContextFor(null);
        var plan = await verification.Set<Plan>().SingleAsync();
        Assert.Equal("scale", plan.Code);
        Assert.Equal(499.00m, plan.MonthlyPriceUsd);
        Assert.Equal("plan.create", (await verification.Set<PlatformAuditLog>().SingleAsync()).Action);
    }

    [Fact]
    public async Task CreatePlan_rejects_a_duplicate_code()
    {
        using var db = new TestDb();
        await using (var seed = db.ContextFor(null))
        {
            seed.Set<Plan>().Add(new Plan { Code = "pro", Name = "Pro" });
            await seed.SaveChangesAsync();
        }

        await using var context = db.ContextFor(null);
        var result = await OperationsController(context).CreatePlan(new UpsertPlanRequest
        {
            Code = "PRO",
            Name = "Pro Again"
        }, CancellationToken.None);

        Assert.IsType<ConflictObjectResult>(result);
    }

    [Fact]
    public async Task UpdatePlan_changes_the_row_audits_and_keeps_codes_unique()
    {
        using var db = new TestDb();
        long planId;
        await using (var seed = db.ContextFor(null))
        {
            var plan = new Plan { Code = "pro", Name = "Pro", MonthlyPriceUsd = 99m };
            seed.Set<Plan>().AddRange(plan, new Plan { Code = "enterprise", Name = "Enterprise" });
            await seed.SaveChangesAsync();
            planId = plan.Id;
        }

        await using var context = db.ContextFor(null);
        var controller = OperationsController(context);

        var conflict = await controller.UpdatePlan(planId, new UpsertPlanRequest
        {
            Code = "enterprise",
            Name = "Renamed"
        }, CancellationToken.None);
        Assert.IsType<ConflictObjectResult>(conflict);

        var ok = await controller.UpdatePlan(planId, new UpsertPlanRequest
        {
            Code = "pro",
            Name = "Pro v2",
            Weight = 3,
            MonthlyPriceUsd = 129.50m
        }, CancellationToken.None);
        Assert.IsType<OkObjectResult>(ok);

        await using var verification = db.ContextFor(null);
        var updated = await verification.Set<Plan>().SingleAsync(p => p.Id == planId);
        Assert.Equal("Pro v2", updated.Name);
        Assert.Equal(129.50m, updated.MonthlyPriceUsd);
        Assert.Equal("plan.update", (await verification.Set<PlatformAuditLog>().SingleAsync()).Action);
    }

    [Fact]
    public async Task Overview_buckets_absent_plans_as_none_and_never_invents_pro()
    {
        using var db = new TestDb();
        await using (var seed = db.ContextFor(null))
        {
            var custom = new Plan { Code = "Scale", Name = "Scale" };
            seed.Set<Plan>().Add(custom);
            await seed.SaveChangesAsync();
            seed.Set<Tenant>().AddRange(
                new Tenant { Name = "No Plan", Slug = "no-plan", Status = TenantStatus.Active },
                new Tenant { Name = "Custom Plan", Slug = "custom-plan", Status = TenantStatus.Active, PlanId = custom.Id });
            await seed.SaveChangesAsync();
        }

        await using var context = db.ContextFor(null);
        var payload = await Payload(OverviewController(context).Get(CancellationToken.None));

        var buckets = payload.GetProperty("tenantsByPlan").EnumerateArray()
            .ToDictionary(
                e => e.GetProperty("tier").GetString()!,
                e => e.GetProperty("count").GetInt32());
        Assert.Equal(1, buckets["none"]);
        Assert.Equal(1, buckets["scale"]);
        Assert.DoesNotContain("pro", buckets.Keys);
    }

    [Fact]
    public async Task Overview_reports_a_clearly_labeled_fleet_total_instead_of_seatsInUse()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(null);

        var payload = await Payload(OverviewController(context).Get(CancellationToken.None));

        Assert.True(payload.TryGetProperty("activeUsersFleetWide", out _));
        Assert.False(payload.TryGetProperty("seatsInUse", out _));
    }

    [Fact]
    public async Task Pipeline_jobs_survives_two_tenants_sharing_a_primary_business_unit()
    {
        using var db = new TestDb();
        await using (var seed = db.ContextFor(null))
        {
            seed.Set<Tenant>().AddRange(
                new Tenant { Name = "First", Slug = "first", Status = TenantStatus.Active, PrimaryBusinessUnitId = 42 },
                new Tenant { Name = "Second", Slug = "second", Status = TenantStatus.Active, PrimaryBusinessUnitId = 42 });
            seed.Set<ExtractionJob>().Add(new ExtractionJob
            {
                BatchId = Guid.NewGuid(),
                BusinessUnitId = 42,
                ContentHash = "hash",
                StoragePath = "path",
                FileName = "doc.pdf",
                Status = ExtractionStatus.Pending,
                CreatedOn = DateTime.UtcNow,
                UpdatedOn = DateTime.UtcNow
            });
            await seed.SaveChangesAsync();
        }

        await using var context = db.ContextFor(null);
        var rows = await Rows(OperationsController(context).Jobs(null, null, CancellationToken.None));

        var job = Assert.Single(rows);
        // Deterministically attributed to the earliest tenant claiming the unit.
        Assert.Equal("First", job.GetProperty("tenantName").GetString());
    }

    // ---- Helpers ------------------------------------------------------------

    private static PlatformOperationsController OperationsController(ErpRfqAutomationContext context) => new(
        context, new PlatformAuditService(context, NullLogger<PlatformAuditService>.Instance),
        PlatformSupportFixture.Authorization())
    {
        ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(
                [
                    new Claim("sub", "7"),
                    new Claim("email", "operator@example.test")
                ], "Platform"))
            }
        }
    };

    private static OverviewController OverviewController(ErpRfqAutomationContext context)
    {
        var services = new ServiceCollection().AddLogging().AddOptions();
        services.AddHealthChecks();
        var provider = services.BuildServiceProvider();
        return new OverviewController(context, provider.GetRequiredService<HealthCheckService>());
    }

    private static async Task<List<JsonElement>> Rows(Task<IActionResult> pending)
    {
        var payload = await Payload(pending);
        return payload.EnumerateArray().Select(e => e.Clone()).ToList();
    }

    private static async Task<JsonElement> Payload(Task<IActionResult> pending)
    {
        var ok = Assert.IsType<OkObjectResult>(await pending);
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(ok.Value));
        return document.RootElement.Clone();
    }
}
