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
        var payload = await Payload(OverviewController(context).Get(ct: CancellationToken.None));

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

        var payload = await Payload(OverviewController(context).Get(ct: CancellationToken.None));

        Assert.True(payload.TryGetProperty("activeUsersFleetWide", out _));
        Assert.False(payload.TryGetProperty("seatsInUse", out _));
    }

    // ---- The overview reports absence as absence -----------------------------
    //
    // Every test below exists because the console showed a confident number where it had
    // nothing to report, or reported the fleet in a way that hid the thing an operator
    // needed to act on.

    [Fact]
    public async Task Overview_reports_an_empty_denominator_as_null_never_as_zero_percent()
    {
        // A fleet that has never run a job used to read "Extraction Success 0.0%" — total
        // failure — because the server divided nothing by nothing and sent 0.
        using var db = new TestDb();
        await using var context = db.ContextFor(null);

        var payload = await Payload(OverviewController(context).Get(ct: CancellationToken.None));

        Assert.Equal(JsonValueKind.Null, payload.GetProperty("extractionSuccessRate").ValueKind);
        Assert.Equal(JsonValueKind.Null, payload.GetProperty("extractionSuccessRateWindow").ValueKind);
        Assert.Equal(JsonValueKind.Null, payload.GetProperty("oldestPendingMinutes").ValueKind);
        var commercial = payload.GetProperty("commercial");
        Assert.Equal(JsonValueKind.Null, commercial.GetProperty("rfqsQuotedPct").ValueKind);
        Assert.Equal(JsonValueKind.Null, commercial.GetProperty("quotesOrderedPct").ValueKind);
    }

    [Fact]
    public async Task Overview_refuses_a_window_it_does_not_serve_instead_of_rounding_it()
    {
        // Silently serving 14 days under a "30 days" caption is a lie the operator cannot see.
        using var db = new TestDb();
        await using var context = db.ContextFor(null);

        var result = await OverviewController(context).Get(13, CancellationToken.None);

        var problem = Assert.IsAssignableFrom<ObjectResult>(result);
        Assert.Equal(400, problem.StatusCode);
        var detail = Assert.IsType<ValidationProblemDetails>(problem.Value);
        Assert.Contains("windowDays", detail.Errors.Keys);
    }

    [Theory]
    [InlineData(7)]
    [InlineData(30)]
    [InlineData(90)]
    public async Task Overview_series_are_exactly_as_long_as_the_window_they_claim(int windowDays)
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(null);

        var payload = await Payload(OverviewController(context).Get(windowDays, CancellationToken.None));

        Assert.Equal(windowDays, payload.GetProperty("windowDays").GetInt32());
        Assert.Equal(windowDays, payload.GetProperty("throughput").GetArrayLength());
        Assert.Equal(windowDays, payload.GetProperty("costTrend").GetArrayLength());
    }

    [Fact]
    public async Task Overview_publishes_every_tenant_lifecycle_bucket_including_the_empty_ones()
    {
        // A fleet of five tenants where NONE is active is the most important thing that fleet
        // can be. "5 tenants / 0 active" buried it; the whole histogram is published now.
        using var db = new TestDb();
        await using (var seed = db.ContextFor(null))
        {
            seed.Set<Tenant>().AddRange(
                new Tenant { Name = "One", Slug = "one", Status = TenantStatus.Provisioning },
                new Tenant { Name = "Two", Slug = "two", Status = TenantStatus.Provisioning },
                new Tenant { Name = "Three", Slug = "three", Status = TenantStatus.PastDue });
            await seed.SaveChangesAsync();
        }

        await using var context = db.ContextFor(null);
        var payload = await Payload(OverviewController(context).Get(ct: CancellationToken.None));

        var buckets = payload.GetProperty("tenantsByStatus").EnumerateArray()
            .ToDictionary(e => e.GetProperty("status").GetString()!, e => e.GetProperty("count").GetInt32());
        Assert.Equal(2, buckets["Provisioning"]);
        Assert.Equal(1, buckets["PastDue"]);
        Assert.Equal(0, buckets["Active"]);
        Assert.Equal(0, buckets["Suspended"]);
        Assert.Equal(0, buckets["Archived"]);
        Assert.Equal(0, payload.GetProperty("activeTenants").GetInt32());
    }

    [Fact]
    public async Task Overview_never_blends_order_value_across_currencies()
    {
        // Adding SAR to USD because both are decimals produces a total nobody can reconcile.
        // The same stance FxConversionService takes on the tenant dashboard.
        using var db = new TestDb();
        await using (var seed = db.ContextFor(null))
        {
            SeedCommercialParents(seed, businessUnitId: 7);
            seed.Set<Currency>().AddRange(
                new Currency { Id = 1, Code = "SAR", CurrencyName = "Saudi Riyal", BusinessUnitId = 7, CreatedBy = "test", CreatedOn = DateTime.UtcNow },
                new Currency { Id = 2, Code = "USD", CurrencyName = "US Dollar", BusinessUnitId = 7, CreatedBy = "test", CreatedOn = DateTime.UtcNow });
            await seed.SaveChangesAsync();

            seed.Set<Order>().AddRange(
                NewOrder(businessUnitId: 7, currencyId: 1, total: 100m, number: "SO-1"),
                NewOrder(businessUnitId: 7, currencyId: 1, total: 50m, number: "SO-2"),
                NewOrder(businessUnitId: 7, currencyId: 2, total: 40m, number: "SO-3"),
                NewOrder(businessUnitId: 7, currencyId: null, total: 9m, number: "SO-4"));
            await seed.SaveChangesAsync();
        }

        await using var context = db.ContextFor(null);
        var payload = await Payload(OverviewController(context).Get(ct: CancellationToken.None));

        var byCurrency = payload.GetProperty("commercial").GetProperty("orderValueByCurrency")
            .EnumerateArray()
            .ToDictionary(e => e.GetProperty("currency").GetString()!, e => e.GetProperty("amount").GetDecimal());
        Assert.Equal(150m, byCurrency["SAR"]);
        Assert.Equal(40m, byCurrency["USD"]);
        // A currency-less order is reported as unknown, not folded into whichever code sorts first.
        Assert.Equal(9m, byCurrency["unknown"]);
        Assert.Equal(4, payload.GetProperty("commercial").GetProperty("ordersWon").GetInt32());
    }

    [Fact]
    public async Task Overview_measures_conversion_on_linked_records_not_on_two_divided_counts()
    {
        // Dividing "quotes this fortnight" by "RFQs this fortnight" produces a conversion rate
        // that moves when neither the RFQ nor the quote did. This is a cohort on real links.
        using var db = new TestDb();
        await using (var seed = db.ContextFor(null))
        {
            SeedCommercialParents(seed, businessUnitId: 7);
            var quoted = new Rfq { Rfqno = "RFQ-1", RecDate = DateTime.UtcNow, CreatedBy = "test", CreatedDate = DateTime.UtcNow, BusinessUnitId = 7 };
            var unquoted = new Rfq { Rfqno = "RFQ-2", RecDate = DateTime.UtcNow, CreatedBy = "test", CreatedDate = DateTime.UtcNow, BusinessUnitId = 7 };
            seed.Set<Rfq>().AddRange(quoted, unquoted);
            await seed.SaveChangesAsync();

            var ordered = new Quote { QuoteNo = "Q-1", Rfqid = quoted.Id, BusinessUnitId = 7, CreatedBy = "test", CreatedDate = DateTime.UtcNow, QuoteDate = DateTime.UtcNow };
            var unordered = new Quote { QuoteNo = "Q-2", Rfqid = quoted.Id, BusinessUnitId = 7, CreatedBy = "test", CreatedDate = DateTime.UtcNow, QuoteDate = DateTime.UtcNow };
            seed.Set<Quote>().AddRange(ordered, unordered);
            await seed.SaveChangesAsync();

            var order = NewOrder(businessUnitId: 7, currencyId: null, total: 1m, number: "SO-1");
            order.QuoteId = ordered.Id;
            order.SourceType = "LEGACY_QUOTE";
            seed.Set<Order>().Add(order);
            await seed.SaveChangesAsync();
        }

        await using var context = db.ContextFor(null);
        var commercial = (await Payload(OverviewController(context).Get(ct: CancellationToken.None)))
            .GetProperty("commercial");

        Assert.Equal(2, commercial.GetProperty("rfqsCaptured").GetInt32());
        Assert.Equal(2, commercial.GetProperty("quotesIssued").GetInt32());
        // One of two RFQs carries a quote; one of two quotes carries an order.
        Assert.Equal(0.5, commercial.GetProperty("rfqsQuotedPct").GetDouble(), 3);
        Assert.Equal(0.5, commercial.GetProperty("quotesOrderedPct").GetDouble(), 3);
    }

    [Fact]
    public async Task Overview_attributes_activity_to_every_tenant_claiming_the_business_unit()
    {
        // Two tenants sharing a primary business unit is a real state (see the /pipeline test
        // below). The leaderboard must not drop one of them or double the fleet totals.
        using var db = new TestDb();
        await using (var seed = db.ContextFor(null))
        {
            seed.Set<Tenant>().AddRange(
                new Tenant { Name = "Busy", Slug = "busy", Status = TenantStatus.Active, PrimaryBusinessUnitId = 42 },
                new Tenant { Name = "Twin", Slug = "twin", Status = TenantStatus.Active, PrimaryBusinessUnitId = 42 },
                new Tenant { Name = "Idle", Slug = "idle", Status = TenantStatus.Active, PrimaryBusinessUnitId = 43 });
            seed.Set<ExtractionJob>().AddRange(
                NewJob(42, ExtractionStatus.Succeeded),
                NewJob(42, ExtractionStatus.Succeeded),
                NewJob(42, ExtractionStatus.DeadLetter));
            await seed.SaveChangesAsync();
        }

        await using var context = db.ContextFor(null);
        var payload = await Payload(OverviewController(context).Get(ct: CancellationToken.None));

        var rows = payload.GetProperty("topTenants").EnumerateArray()
            .ToDictionary(e => e.GetProperty("name").GetString()!, e => e);
        Assert.Equal(3, rows.Count);
        Assert.Equal(2, rows["Busy"].GetProperty("docs").GetInt32());
        Assert.Equal(1, rows["Busy"].GetProperty("failures").GetInt32());
        Assert.Equal(2, rows["Twin"].GetProperty("docs").GetInt32());
        Assert.Equal(0, rows["Idle"].GetProperty("docs").GetInt32());
        // The fleet total counts the documents ONCE, however many tenants claim the unit.
        Assert.Equal(2, payload.GetProperty("docsProcessedInWindow").GetInt32());
        // Busiest first, and the idle tenant last.
        Assert.Equal("Idle", payload.GetProperty("topTenants")[2].GetProperty("name").GetString());
    }

    [Fact]
    public async Task Overview_throughput_series_agrees_with_the_headline_it_sits_under()
    {
        // The series keyed on CreatedOn while the headline counts keyed on UpdatedOn, so a
        // caption saying "3 failed" sat above a chart drawing a different number.
        using var db = new TestDb();
        await using (var seed = db.ContextFor(null))
        {
            var completedToday = DateTime.UtcNow;
            var createdLongAgo = completedToday.AddDays(-40);
            seed.Set<ExtractionJob>().AddRange(
                NewJob(42, ExtractionStatus.Succeeded, createdLongAgo, completedToday),
                NewJob(42, ExtractionStatus.Failed, createdLongAgo, completedToday),
                NewJob(42, ExtractionStatus.DeadLetter, createdLongAgo, completedToday));
            await seed.SaveChangesAsync();
        }

        await using var context = db.ContextFor(null);
        var payload = await Payload(OverviewController(context).Get(ct: CancellationToken.None));

        var seriesDocs = payload.GetProperty("throughput").EnumerateArray().Sum(e => e.GetProperty("docs").GetInt32());
        var seriesFailures = payload.GetProperty("throughput").EnumerateArray().Sum(e => e.GetProperty("failures").GetInt32());
        Assert.Equal(payload.GetProperty("docsProcessedInWindow").GetInt32(), seriesDocs);
        Assert.Equal(payload.GetProperty("failuresInWindow").GetInt32(), seriesFailures);
        Assert.Equal(2, seriesFailures);
    }

    [Fact]
    public async Task Pipeline_queue_reports_an_idle_window_as_null_not_as_total_failure()
    {
        // The same defect the overview had, on the screen an operator opens when the pipeline
        // looks wrong: a quiet 24 hours reported "0.0% success" and "0ms" average latency.
        using var db = new TestDb();
        await using var context = db.ContextFor(null);

        var payload = await Payload(OperationsController(context).Queue(CancellationToken.None));

        Assert.Equal(JsonValueKind.Null, payload.GetProperty("successRate").ValueKind);
        Assert.Equal(JsonValueKind.Null, payload.GetProperty("successfulClaimRate").ValueKind);
        Assert.Equal(JsonValueKind.Null, payload.GetProperty("retryRate").ValueKind);
        Assert.Equal(JsonValueKind.Null, payload.GetProperty("avgLatencyMs").ValueKind);
        // The counts are still real zeros: nothing IS queued, and that is a measurement.
        Assert.Equal(0, payload.GetProperty("queueDepth").GetInt32());
    }

    [Fact]
    public async Task Pipeline_queue_still_reports_a_real_rate_once_jobs_terminate()
    {
        using var db = new TestDb();
        await using (var seed = db.ContextFor(null))
        {
            seed.Set<ExtractionJob>().AddRange(
                NewJob(42, ExtractionStatus.Succeeded),
                NewJob(42, ExtractionStatus.Succeeded),
                NewJob(42, ExtractionStatus.Failed));
            await seed.SaveChangesAsync();
        }

        await using var context = db.ContextFor(null);
        var payload = await Payload(OperationsController(context).Queue(CancellationToken.None));

        Assert.Equal(2d / 3d, payload.GetProperty("successRate").GetDouble(), 3);
        Assert.Equal(JsonValueKind.Number, payload.GetProperty("avgLatencyMs").ValueKind);
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

    [Fact]
    public async Task Broad_platform_roles_do_not_receive_document_names_or_raw_failures()
    {
        using var db = new TestDb();
        await using (var seed = db.ContextFor(null))
        {
            seed.Set<ExtractionJob>().Add(new ExtractionJob
            {
                BatchId = Guid.NewGuid(), BusinessUnitId = 42, ContentHash = "hash",
                StoragePath = "path", FileName = "customer-secret-rfq.pdf",
                LastError = "provider leaked customer@example.test from parsed row",
                Status = ExtractionStatus.Failed, CreatedOn = DateTime.UtcNow, UpdatedOn = DateTime.UtcNow
            });
            await seed.SaveChangesAsync();
        }

        await using var context = db.ContextFor(null);
        var row = Assert.Single(await Rows(OperationsController(context).Jobs(null, null, default)));

        Assert.Equal("Restricted document", row.GetProperty("documentName").GetString());
        Assert.Equal("Processing failed; diagnostic details are restricted.",
            row.GetProperty("error").GetString());
        Assert.DoesNotContain("customer@example.test", row.GetRawText());
    }

    // ---- Helpers ------------------------------------------------------------

    /// <summary>The business unit and customer an Order cannot exist without.</summary>
    private static void SeedCommercialParents(ErpRfqAutomationContext seed, long businessUnitId)
    {
        seed.Set<BusinessUnit>().Add(new BusinessUnit
        {
            Id = businessUnitId,
            BusinessUnitCode = $"BU{businessUnitId}",
            BusinessUnitName = $"Unit {businessUnitId}",
            CreatedBy = "test",
            CreatedOn = DateTime.UtcNow
        });
        seed.Set<Customer>().Add(new Customer
        {
            Id = 1,
            Name = "Buyer",
            ImageUrl = string.Empty,
            Buid = businessUnitId,
            CreatedBy = "test",
            CreatedOn = DateTime.UtcNow
        });
        seed.Set<SetupMaster>().Add(new SetupMaster
        {
            SetupId = 1,
            SetupType = "OrderStatus",
            SetupValue = "Confirmed",
            BusinessUnitId = businessUnitId,
            CreatedBy = "test",
            CreatedOn = DateTime.UtcNow
        });
        seed.SaveChanges();
    }

    private static Order NewOrder(long businessUnitId, long? currencyId, decimal total, string number) => new()
    {
        OrderNo = number,
        CustomerId = 1,
        BusinessUnitId = businessUnitId,
        StatusId = 1,
        CurrencyId = currencyId,
        // CK_Orders_SourceIdentity: MANUAL is the only source type allowed to have no quote.
        SourceType = "MANUAL",
        PaidAmount = 0m,
        TotalAmount = total,
        OrderDate = DateTime.UtcNow,
        CreatedBy = "test",
        CreatedOn = DateTime.UtcNow,
        IsActive = true
    };

    private static ExtractionJob NewJob(
        long businessUnitId, ExtractionStatus status, DateTime? createdOn = null, DateTime? updatedOn = null) => new()
    {
        BatchId = Guid.NewGuid(),
        BusinessUnitId = businessUnitId,
        ContentHash = Guid.NewGuid().ToString("N"),
        StoragePath = "path",
        FileName = "doc.pdf",
        Status = status,
        CreatedOn = createdOn ?? DateTime.UtcNow,
        UpdatedOn = updatedOn ?? DateTime.UtcNow
    };

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
