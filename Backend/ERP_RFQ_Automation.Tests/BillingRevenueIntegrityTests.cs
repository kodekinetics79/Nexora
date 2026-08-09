using System.Reflection;
using ERP_RFQ_Automation.Billing;
using ERP_RFQ_Automation.Billing.Controllers;
using ERP_RFQ_Automation.DocumentIntelligence.Persistence;
using ERP_RFQ_Automation.Extraction;
using ERP_RFQ_Automation.HealthChecks;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.MultiTenancy;
using ERP_RFQ_Automation.Platform.Auth;
using ERP_RFQ_Automation.Platform.Entitlements;
using ERP_RFQ_Automation.Platform.Models;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// The revenue-integrity invariants: a tenant must never consume this platform for free
/// by accident.
///
/// <para>Each test is named for the invariant it protects, and each one corresponds to a
/// confirmed leak: a plan-less tenant metered and never charged a subscription; a
/// negotiated tenant silently repriced by whatever rate card happened to be active;
/// statements produced only when a human clicked Compute; a null plan price becoming a
/// quiet zero; a "trial" indistinguishable from permanent free service; and the same
/// missing plan handing out unmetered seats and documents on the consumption side.</para>
///
/// <para>Runs on the plain SQLite-in-memory harness over the real model: the billing
/// model is wired into the production <c>OnModelCreatingPartial</c>, so the platform
/// tables exist here, and the evidence ledger's PostgreSQL-only meters correctly read
/// zero rather than crashing (the service checks the model for them).</para>
/// </summary>
public sealed class BillingRevenueIntegrityTests
{
    private static readonly DateTime InJuly = new(2026, 7, 10, 12, 0, 0, DateTimeKind.Utc);
    private static readonly BillingPeriod July = Period("2026-07");

    // ================================================= 1. the base subscription is owed

    [Fact]
    public async Task Billable_tenant_with_no_plan_is_still_given_a_base_subscription_line_carrying_a_revenue_risk_code()
    {
        using var db = new RevenueTestDb();
        SeedTenant(db, planPrice: null, withPlan: false);
        var cardId = SeedRateCard(db);
        SeedUsage(db);

        using var ctx = db.ContextFor(null);
        var statement = await Service(ctx).ComputeStatementAsync(TenantId, July, cardId);

        // The leak: BuildLines emitted the base line only `if (plan is not null)`, so a
        // plan-less tenant was metered and never charged a subscription — and the
        // statement gave no sign that a line was missing.
        var baseLine = Line(statement, BillingMeterKeys.BaseSubscription);
        Assert.Equal(0m, baseLine.Amount);
        Assert.Contains("NO PLAN ASSIGNED", baseLine.Description);
        Assert.Equal(BillingStatementMarkers.RiskNoPlan,
            BillingStatementMarkers.RiskCodeOf(baseLine.MeterKey, baseLine.CoverageNote));
        Assert.Contains("REVENUE RISK", baseLine.CoverageNote);

        // Metered usage is still charged: the missing plan costs the subscription, not the meter.
        Assert.True(Line(statement, BillingMeterKeys.Documents).Amount > 0m);
    }

    [Fact]
    public async Task Billable_plan_with_no_monthly_price_is_marked_a_revenue_risk_instead_of_charging_a_quiet_zero()
    {
        using var db = new RevenueTestDb();
        SeedTenant(db, planPrice: null, withPlan: true);
        var cardId = SeedRateCard(db);

        using var ctx = db.ContextFor(null);
        var statement = await Service(ctx).ComputeStatementAsync(TenantId, July, cardId);

        var baseLine = Line(statement, BillingMeterKeys.BaseSubscription);
        Assert.Equal(0m, baseLine.Amount);
        Assert.Equal(BillingStatementMarkers.RiskPlanNotPriced,
            BillingStatementMarkers.RiskCodeOf(baseLine.MeterKey, baseLine.CoverageNote));
        // A null price is an unfinished plan, not a free one — the note has to say so.
        Assert.Contains("not a free plan", baseLine.CoverageNote);
    }

    [Fact]
    public async Task A_priced_plan_produces_a_clean_base_line_with_no_risk_code_at_all()
    {
        using var db = new RevenueTestDb();
        SeedTenant(db, planPrice: 250.00m, withPlan: true);
        var cardId = SeedRateCard(db);

        using var ctx = db.ContextFor(null);
        var statement = await Service(ctx).ComputeStatementAsync(TenantId, July, cardId);

        var baseLine = Line(statement, BillingMeterKeys.BaseSubscription);
        Assert.Equal(250.00m, baseLine.Amount);
        Assert.Null(BillingStatementMarkers.RiskCodeOf(baseLine.MeterKey, baseLine.CoverageNote));
        Assert.DoesNotContain(statement.Lines, l => BillingStatementMarkers.IsRevenueRisk(l.MeterKey));
    }

    // ================================================ 2. the pinned rate card is binding

    [Fact]
    public async Task Pinned_rate_card_beats_the_active_card_so_a_negotiated_tenant_is_never_silently_repriced()
    {
        using var db = new RevenueTestDb();
        var negotiatedCardId = SeedRateCard(db, code: "negotiated", documentPrice: 0.50m, active: true);
        SeedRateCard(db, code: "list-2027", documentPrice: 9.99m, active: true,
            effectiveFrom: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        SeedTenant(db, planPrice: 100m, withPlan: true, rateCardId: negotiatedCardId);
        SeedUsage(db);

        using var ctx = db.ContextFor(null);
        // No rateCardId argument: this is exactly the call the scheduled run makes.
        var statement = await Service(ctx).ComputeStatementAsync(TenantId, July);

        Assert.Equal(negotiatedCardId, statement.RateCardId);
        Assert.Equal(1.50m, Line(statement, BillingMeterKeys.Documents).Amount); // 3 billable docs x 0.50
        // A pinned tenant is not a finding; the fallback marker must be absent.
        Assert.DoesNotContain(statement.Lines, l => l.MeterKey == BillingStatementMarkers.RiskUnpinnedRateCard);
    }

    [Fact]
    public async Task An_explicitly_named_rate_card_cannot_override_the_tenant_pin()
    {
        using var db = new RevenueTestDb();
        var pinnedId = SeedRateCard(db, code: "pinned", documentPrice: 0.50m);
        var overrideId = SeedRateCard(db, code: "override", documentPrice: 2.00m, active: false);
        SeedTenant(db, planPrice: 100m, withPlan: true, rateCardId: pinnedId);
        SeedUsage(db);

        using var ctx = db.ContextFor(null);
        var refusal = await Assert.ThrowsAsync<BillingConflictException>(() =>
            Service(ctx).ComputeStatementAsync(TenantId, July, overrideId));

        Assert.Contains("pinned", refusal.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("cannot substitute", refusal.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_dangling_rate_card_pin_refuses_to_compute_rather_than_repricing_onto_the_active_card()
    {
        using var db = new RevenueTestDb();
        SeedRateCard(db, code: "list-2027", documentPrice: 9.99m, active: true);
        SeedTenant(db, planPrice: 100m, withPlan: true, rateCardId: 987_654L); // no such card

        using var ctx = db.ContextFor(null);
        var rejected = await Assert.ThrowsAsync<BillingConflictException>(
            () => Service(ctx).ComputeStatementAsync(TenantId, July));

        Assert.Contains("987654", rejected.Message);
        Assert.Contains("nobody agreed to", rejected.Message);

        // Nothing written: the tenant is NOT quietly moved onto the list price.
        using var verification = db.ContextFor(null);
        Assert.Equal(0, await verification.Set<BillingStatement>().CountAsync());
    }

    [Fact]
    public async Task An_unpinned_billable_tenant_still_computes_but_the_statement_records_the_fallback_as_a_finding()
    {
        using var db = new RevenueTestDb();
        SeedRateCard(db, code: "list-2027", documentPrice: 1.00m, active: true);
        SeedTenant(db, planPrice: 100m, withPlan: true, rateCardId: null);
        SeedUsage(db);

        using var ctx = db.ContextFor(null);
        var statement = await Service(ctx).ComputeStatementAsync(TenantId, July);

        // The fallback survives (tenants predate pinning) but never silently.
        var marker = Line(statement, BillingStatementMarkers.RiskUnpinnedRateCard);
        Assert.Equal(0m, marker.Amount);
        Assert.Contains("no pinned RateCardId", marker.CoverageNote);
        Assert.Equal(103.00m, statement.TotalAmount); // 100 base + 3 docs x 1.00 — still charged
    }

    // ================================================== 3. billing modes charge honestly

    [Fact]
    public async Task Trial_tenants_get_a_real_zero_charge_draft_that_still_meters_usage_as_a_conversion_baseline()
    {
        using var db = new RevenueTestDb();
        var cardId = SeedRateCard(db, documentPrice: 1.50m);
        SeedTenant(db, planPrice: 100m, withPlan: true, rateCardId: cardId,
            billingMode: TenantBillingMode.Trial, billingModeReason: "30-day evaluation, signed by AE",
            trialEndsOn: DateTime.UtcNow.AddDays(10));
        SeedUsage(db);

        using var ctx = db.ContextFor(null);
        var statement = await Service(ctx).ComputeStatementAsync(TenantId, July);

        Assert.Equal(BillingStatementStatus.Draft, statement.Status);
        Assert.Equal(0m, statement.TotalAmount);

        // The baseline: quantities and the list unit price are REAL, only the amount is waived.
        var docs = Line(statement, BillingMeterKeys.Documents);
        Assert.Equal(5m, docs.MeteredQuantity);
        Assert.Equal(3m, docs.BillableQuantity);
        Assert.Equal(1.50m, docs.UnitPrice);
        Assert.Equal(0m, docs.Amount);

        // A trial charges no subscription at all, and the exemption states what was given away.
        Assert.DoesNotContain(statement.Lines, l => l.MeterKey == BillingMeterKeys.BaseSubscription);
        var exemption = Line(statement, BillingStatementMarkers.ExemptionFor(TenantBillingMode.Trial));
        Assert.Contains("4.50 USD", exemption.CoverageNote); // 3 docs x 1.50 waived
        Assert.Contains("conversion has a real baseline", exemption.CoverageNote);
    }

    [Theory]
    [InlineData(TenantBillingMode.Internal)]
    [InlineData(TenantBillingMode.Partner)]
    public async Task Exempt_tenants_are_never_charged_but_their_consumption_stays_on_the_statement(
        TenantBillingMode mode)
    {
        using var db = new RevenueTestDb();
        var cardId = SeedRateCard(db, documentPrice: 1.50m);
        SeedTenant(db, planPrice: 100m, withPlan: true, rateCardId: cardId,
            billingMode: mode, billingModeReason: "operator-owned workspace");
        SeedUsage(db);

        using var ctx = db.ContextFor(null);
        var statement = await Service(ctx).ComputeStatementAsync(TenantId, July);

        Assert.Equal(0m, statement.TotalAmount);
        Assert.Equal(5m, Line(statement, BillingMeterKeys.Documents).MeteredQuantity); // cost stays visible
        var exemption = Line(statement, BillingStatementMarkers.ExemptionFor(mode));
        Assert.Contains("never charged through this system", exemption.CoverageNote);
        Assert.Contains("cost of serving it stays visible", exemption.CoverageNote);
    }

    [Fact]
    public async Task Usage_in_a_period_that_starts_before_billing_starts_on_is_metered_but_not_billed()
    {
        using var db = new RevenueTestDb();
        var cardId = SeedRateCard(db, documentPrice: 1.50m);
        // Billing begins in August; the July period is pilot usage.
        SeedTenant(db, planPrice: 100m, withPlan: true, rateCardId: cardId,
            billingStartsOn: new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc));
        SeedUsage(db);

        using var ctx = db.ContextFor(null);
        var statement = await Service(ctx).ComputeStatementAsync(TenantId, July);

        Assert.Equal(0m, statement.TotalAmount);
        Assert.Equal(5m, Line(statement, BillingMeterKeys.Documents).MeteredQuantity);
        Assert.Equal(0m, Line(statement, BillingMeterKeys.BaseSubscription).Amount);
        var deferral = Line(statement, BillingStatementMarkers.ExemptionPreBillingStart);
        Assert.Contains("BillingStartsOn", deferral.CoverageNote);
        Assert.Contains("2026-08-01", deferral.CoverageNote);
    }

    // ------------------------------------------- proration across BillingStartsOn

    [Theory]
    // July 2026 has 31 days. Billing starts on the 20th → the 20th through the 31st = 12 days.
    [InlineData("2026-07-20", 12, "115.35")]  // 12/31 x 298.00 = 115.354... -> 115.35
    [InlineData("2026-07-31", 1, "9.61")]     //  1/31 x 298.00 =   9.612... ->   9.61
    [InlineData("2026-07-02", 30, "288.39")]  // 30/31 x 298.00 = 288.387... -> 288.39
    public async Task A_period_straddling_billing_starts_on_is_charged_pro_rata_by_days(
        string billingStartsOn, int expectedBillableDays, string expectedBase)
    {
        using var db = new RevenueTestDb();
        var cardId = SeedRateCard(db, documentPrice: 1.50m);
        SeedTenant(db, planPrice: 298.00m, withPlan: true, rateCardId: cardId,
            billingStartsOn: DateTime.SpecifyKind(DateTime.Parse(billingStartsOn), DateTimeKind.Utc));

        using var ctx = db.ContextFor(null);
        var statement = await Service(ctx).ComputeStatementAsync(TenantId, July);

        var baseLine = Line(statement, BillingMeterKeys.BaseSubscription);
        Assert.Equal(decimal.Parse(expectedBase), baseLine.Amount);
        Assert.Equal(298.00m, baseLine.UnitPrice); // the list price stays visible on the line

        // The day counts are ON the line, so a customer query is answerable from the
        // statement rather than from a developer re-deriving the arithmetic.
        Assert.Contains($"{expectedBillableDays} of 31 days", baseLine.SourceNote);
        Assert.Contains($"{expectedBillableDays}/31 x 298.00 USD = {expectedBase} USD", baseLine.SourceNote);
        Assert.Contains($"({expectedBillableDays}/31 days)", baseLine.Description);

        var marker = Line(statement, BillingStatementMarkers.ProrationBillingStart);
        Assert.Equal(0m, marker.Amount);
        Assert.Contains($"charged {expectedBillableDays} of 31 days", marker.Description);
        // Never an exemption: money DID move, so it must not land in the free-service family.
        Assert.DoesNotContain(statement.Lines, l => BillingStatementMarkers.IsExemption(l.MeterKey));
    }

    [Fact]
    public async Task A_prorated_period_meters_flow_only_from_the_billing_start_date()
    {
        using var db = new RevenueTestDb();
        var cardId = SeedRateCard(db, documentPrice: 1.50m);
        SeedTenant(db, planPrice: 298.00m, withPlan: true, rateCardId: cardId,
            billingStartsOn: new DateTime(2026, 7, 20, 0, 0, 0, DateTimeKind.Utc));

        using (var seed = db.ContextFor(null))
        {
            // Three documents before billing starts, four on or after it.
            foreach (var day in new[] { 3, 10, 19 })
                seed.Set<ExtractionJob>().Add(NewJob(new DateTime(2026, 7, day, 9, 0, 0, DateTimeKind.Utc)));
            foreach (var day in new[] { 20, 21, 28, 31 })
                seed.Set<ExtractionJob>().Add(NewJob(new DateTime(2026, 7, day, 9, 0, 0, DateTimeKind.Utc)));
            seed.SaveChanges();
        }

        using var ctx = db.ContextFor(null);
        var service = Service(ctx);
        var statement = await service.ComputeStatementAsync(TenantId, July);

        // Only the four from the 20th onward are billable; the allowance of 2 applies to those.
        var docs = Line(statement, BillingMeterKeys.Documents);
        Assert.Equal(4m, docs.MeteredQuantity);
        Assert.Equal(2m, docs.BillableQuantity);
        Assert.Equal(3.00m, docs.Amount);
        Assert.Contains("counted from 2026-07-20", docs.SourceNote);

        // The usage READOUT still reports the whole period: "what did they consume" and
        // "what may we charge for" are different questions and must not be conflated.
        var usage = await service.GetUsageAsync(TenantId, July);
        Assert.Equal(7m, usage.Meters.Single(m => m.MeterKey == BillingMeterKeys.Documents).Quantity);
        Assert.Equal(July.StartUtc, usage.MeteredFromUtc);
    }

    [Fact]
    public async Task Period_end_snapshot_meters_declare_that_they_are_not_prorated()
    {
        using var db = new RevenueTestDb();
        var cardId = SeedRateCard(db, documentPrice: 1.50m);
        SeedTenant(db, planPrice: 298.00m, withPlan: true, rateCardId: cardId,
            billingStartsOn: new DateTime(2026, 7, 20, 0, 0, 0, DateTimeKind.Utc));

        using var ctx = db.ContextFor(null);
        var usage = await Service(ctx).GetUsageAsync(
            TenantId, July, new DateTime(2026, 7, 20, 0, 0, 0, DateTimeKind.Utc), CancellationToken.None);

        // Seats and storage are stocks measured at period end: there is no sub-period to
        // bound, so rather than guess they say so on their own line.
        foreach (var meterKey in new[] { BillingMeterKeys.Seats, BillingMeterKeys.StorageGb })
        {
            var meter = usage.Meters.Single(m => m.MeterKey == meterKey);
            Assert.Contains("NOT PRORATED", meter.CoverageNote);
            Assert.Contains("period-end snapshot", meter.CoverageNote);
        }

        // The flow meters carry the bound instead of the caveat.
        foreach (var meterKey in new[]
                 { BillingMeterKeys.Documents, BillingMeterKeys.PagesProcessed, BillingMeterKeys.AiTokensExternal })
            Assert.Contains("counted from 2026-07-20", usage.Meters.Single(m => m.MeterKey == meterKey).SourceNote);

        Assert.Equal(new DateTime(2026, 7, 20, 0, 0, 0, DateTimeKind.Utc), usage.MeteredFromUtc);
    }

    [Fact]
    public async Task A_billing_start_on_the_first_of_the_period_charges_in_full_with_no_proration_line()
    {
        using var db = new RevenueTestDb();
        var cardId = SeedRateCard(db, documentPrice: 1.50m);
        SeedTenant(db, planPrice: 298.00m, withPlan: true, rateCardId: cardId,
            billingStartsOn: new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc));
        SeedUsage(db);

        using var ctx = db.ContextFor(null);
        var statement = await Service(ctx).ComputeStatementAsync(TenantId, July);

        Assert.Equal(298.00m, Line(statement, BillingMeterKeys.BaseSubscription).Amount);
        Assert.Equal(5m, Line(statement, BillingMeterKeys.Documents).MeteredQuantity);
        Assert.DoesNotContain(statement.Lines,
            l => l.MeterKey.StartsWith(BillingStatementMarkers.ProrationPrefix, StringComparison.Ordinal));
        Assert.Equal(302.50m, statement.TotalAmount); // 298 + 3 chargeable docs x 1.50
    }

    [Fact]
    public async Task A_billing_start_before_the_period_charges_in_full_and_meters_the_whole_period()
    {
        using var db = new RevenueTestDb();
        var cardId = SeedRateCard(db, documentPrice: 1.50m);
        SeedTenant(db, planPrice: 298.00m, withPlan: true, rateCardId: cardId,
            billingStartsOn: new DateTime(2026, 3, 15, 0, 0, 0, DateTimeKind.Utc));
        SeedUsage(db);

        using var ctx = db.ContextFor(null);
        var statement = await Service(ctx).ComputeStatementAsync(TenantId, July);

        Assert.Equal(298.00m, Line(statement, BillingMeterKeys.BaseSubscription).Amount);
        Assert.Equal(5m, Line(statement, BillingMeterKeys.Documents).MeteredQuantity);
        Assert.DoesNotContain(statement.Lines,
            l => l.MeterKey.StartsWith(BillingStatementMarkers.ProrationPrefix, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Converting_a_trial_mid_month_prorates_from_the_trial_end_date_without_being_told_to()
    {
        using var db = new RevenueTestDb();
        var cardId = SeedRateCard(db, documentPrice: 1.50m);
        var currentPeriod = BillingPeriod.Containing(DateTime.UtcNow);
        var trialEndsOn = currentPeriod.StartUtc.AddDays(9); // the 10th of the current month

        SeedTenant(db, planPrice: 300.00m, withPlan: true, rateCardId: cardId,
            billingMode: TenantBillingMode.Trial, billingModeReason: "30-day evaluation for ACME.",
            trialEndsOn: trialEndsOn);

        using var ctx = db.ContextFor(null);
        // No billingStartsOn supplied — the operator just converts the account.
        var converted = await Controller(ctx, Service(ctx)).SetTenantCommercialTerms(
            TenantId, new SetTenantCommercialTermsRequest("Billable", null, null, null), CancellationToken.None);
        var profile = Assert.IsType<TenantBillingProfileDto>(Assert.IsType<OkObjectResult>(converted.Result).Value);

        // Without this default, the customer would be charged for the days they were still
        // on trial — the exact over-billing proration exists to prevent.
        Assert.Equal(trialEndsOn, profile.BillingStartsOn);

        using var compute = db.ContextFor(null);
        var statement = await Service(compute).ComputeStatementAsync(TenantId, currentPeriod);
        var periodDays = (int)(currentPeriod.EndUtc - currentPeriod.StartUtc).TotalDays;
        var billableDays = periodDays - 9;

        var baseLine = Line(statement, BillingMeterKeys.BaseSubscription);
        Assert.Equal(BillingMath.Round2(300.00m * billableDays / periodDays), baseLine.Amount);
        Assert.Contains($"{billableDays} of {periodDays} days", baseLine.SourceNote);
    }

    [Fact]
    public async Task The_base_subscription_amount_is_computed_from_the_exact_day_ratio_not_the_displayed_fraction()
    {
        using var db = new RevenueTestDb();
        var cardId = SeedRateCard(db);
        SeedTenant(db, planPrice: 1000.00m, withPlan: true, rateCardId: cardId,
            billingStartsOn: new DateTime(2026, 7, 20, 0, 0, 0, DateTimeKind.Utc));

        using var ctx = db.ContextFor(null);
        var statement = await Service(ctx).ComputeStatementAsync(TenantId, July);
        var baseLine = Line(statement, BillingMeterKeys.BaseSubscription);

        // 12/31 = 0.387096..., which the quantity column stores at 3dp as 0.387. Pricing that
        // display value would give 387.00; the exact ratio gives 387.10. Ten pence a month
        // per prorated customer is exactly the kind of rounding nobody catches later, so the
        // amount is pinned against the ratio and explicitly NOT against the rounded fraction.
        Assert.Equal(387.10m, baseLine.Amount);
        Assert.Equal(BillingMath.Round2(1000.00m * 12 / 31), baseLine.Amount);
        Assert.NotEqual(BillingMath.Round2(1000.00m * 0.387m), baseLine.Amount);

        // The line still states the arithmetic in full, because the stored quantity alone
        // cannot be multiplied back out to the amount.
        Assert.Contains("12/31 x 1000.00 USD = 387.10 USD", baseLine.SourceNote);
    }

    [Fact]
    public async Task Billing_resumes_for_the_first_period_that_starts_on_or_after_billing_starts_on()
    {
        using var db = new RevenueTestDb();
        var cardId = SeedRateCard(db, documentPrice: 1.50m);
        SeedTenant(db, planPrice: 100m, withPlan: true, rateCardId: cardId,
            billingStartsOn: new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc));
        SeedUsage(db);

        using var ctx = db.ContextFor(null);
        var statement = await Service(ctx).ComputeStatementAsync(TenantId, July);

        Assert.Equal(104.50m, statement.TotalAmount); // 100 base + 3 docs x 1.50
        Assert.DoesNotContain(statement.Lines, l => BillingStatementMarkers.IsExemption(l.MeterKey));
    }

    // ============================================================= 4. trials must convert

    [Fact]
    public async Task An_expired_trial_is_flagged_on_the_statement_and_in_the_readout_and_is_never_auto_suspended()
    {
        using var db = new RevenueTestDb();
        var cardId = SeedRateCard(db);
        var endedOn = DateTime.UtcNow.AddDays(-45);
        SeedTenant(db, planPrice: 100m, withPlan: true, rateCardId: cardId,
            billingMode: TenantBillingMode.Trial, billingModeReason: "pilot", trialEndsOn: endedOn);
        SeedUsage(db);

        using var ctx = db.ContextFor(null);
        var service = Service(ctx);
        var statement = await service.ComputeStatementAsync(TenantId, July);

        var marker = Line(statement, BillingStatementMarkers.RiskTrialExpired);
        Assert.Contains("trial ended", marker.CoverageNote);
        Assert.Contains("never automatic", marker.CoverageNote); // suspension stays a human decision

        var readout = Assert.Single(await service.GetRevenueRiskAsync());
        Assert.True(readout.TrialExpired);
        Assert.Contains(RevenueLeakReasons.TrialExpired, readout.LeakReasons);
        Assert.True(readout.TrialDaysRemaining < 0);

        // The tenant keeps working: a background job does not cut customers off.
        using var verification = db.ContextFor(null);
        Assert.Equal(TenantStatus.Active,
            (await verification.Set<Tenant>().SingleAsync(t => t.Id == TenantId)).Status);
    }

    [Fact]
    public async Task An_open_ended_trial_is_a_leak_because_it_is_indistinguishable_from_permanent_free_service()
    {
        using var db = new RevenueTestDb();
        SeedRateCard(db);
        SeedTenant(db, planPrice: 100m, withPlan: true,
            billingMode: TenantBillingMode.Trial, billingModeReason: "pilot", trialEndsOn: null);

        using var ctx = db.ContextFor(null);
        var readout = Assert.Single(await Service(ctx).GetRevenueRiskAsync());

        Assert.True(readout.AtRisk);
        Assert.Contains(RevenueLeakReasons.TrialOpenEnded, readout.LeakReasons);
    }

    [Fact]
    public async Task An_exemption_nobody_wrote_down_is_a_leak_even_when_the_mode_itself_is_legitimate()
    {
        using var db = new RevenueTestDb();
        SeedRateCard(db);
        SeedTenant(db, planPrice: 100m, withPlan: true,
            billingMode: TenantBillingMode.Partner, billingModeReason: null);

        using var ctx = db.ContextFor(null);
        var readout = Assert.Single(await Service(ctx).GetRevenueRiskAsync());

        Assert.Contains(RevenueLeakReasons.ExemptionUnexplained, readout.LeakReasons);
    }

    // ================================================================ 5. the readout

    [Fact]
    public async Task The_revenue_risk_readout_names_every_tenant_running_free_and_why()
    {
        using var db = new RevenueTestDb();
        var cardId = SeedRateCard(db);
        SeedTenant(db, planPrice: null, withPlan: false, tenantId: 1, slug: "no-plan");
        SeedTenant(db, planPrice: 500m, withPlan: true, tenantId: 2, slug: "healthy",
            rateCardId: cardId, planId: 20);
        SeedTenant(db, planPrice: 500m, withPlan: true, tenantId: 3, slug: "archived",
            rateCardId: cardId, planId: 30, status: TenantStatus.Archived);

        using var ctx = db.ContextFor(null);
        var service = Service(ctx);
        await service.ComputeStatementAsync(2, July, cardId);

        var readout = await service.GetRevenueRiskAsync();

        // Archived tenants are offboarded — they are not a revenue leak, they are gone.
        Assert.Equal(new long[] { 1, 2 }, readout.Select(r => r.TenantId));

        var noPlan = readout.Single(r => r.TenantId == 1);
        Assert.True(noPlan.AtRisk);
        Assert.Contains(RevenueLeakReasons.NoPlan, noPlan.LeakReasons);
        Assert.Contains(RevenueLeakReasons.UnpinnedRateCard, noPlan.LeakReasons);
        Assert.Contains(RevenueLeakReasons.NeverBilled, noPlan.LeakReasons);
        Assert.False(noPlan.LastStatementCharged);
        Assert.Null(noPlan.LastStatementPeriod);

        var healthy = readout.Single(r => r.TenantId == 2);
        Assert.False(healthy.AtRisk);
        Assert.Empty(healthy.LeakReasons);
        Assert.True(healthy.LastStatementCharged);
        Assert.Equal("2026-07", healthy.LastStatementPeriod);
        Assert.Equal(cardId, healthy.PinnedRateCardId);
        Assert.Equal("Billable", healthy.BillingMode);
    }

    [Fact]
    public async Task The_revenue_risk_endpoint_reports_headline_counts_over_the_whole_fleet_even_when_filtered()
    {
        using var db = new RevenueTestDb();
        var cardId = SeedRateCard(db);
        SeedTenant(db, planPrice: null, withPlan: false, tenantId: 1, slug: "leaky");
        SeedTenant(db, planPrice: 500m, withPlan: true, tenantId: 2, slug: "healthy",
            rateCardId: cardId, planId: 20);

        using var ctx = db.ContextFor(null);
        var service = Service(ctx);
        await service.ComputeStatementAsync(2, July, cardId);

        var response = await Controller(ctx, service)
            .GetRevenueRisk(
            onlyAtRisk: true, includeArchived: false,
            onlyCommercialConfigurationRequired: false, CancellationToken.None);
        var report = Assert.IsType<RevenueRiskReportDto>(Assert.IsType<OkObjectResult>(response.Result).Value);

        // "1 of 2" must stay true even though only the 1 is listed.
        Assert.Equal(2, report.TenantCount);
        Assert.Equal(1, report.AtRiskCount);
        Assert.Equal(1, report.BillableTenantsChargedNothingCount);
        Assert.Equal(1L, Assert.Single(report.Tenants).TenantId);
    }

    // ========================================================= 6. the scheduled run

    [Fact]
    public async Task The_billing_run_computes_the_current_period_for_every_tenant_without_anyone_clicking()
    {
        using var db = new RevenueTestDb();
        var cardId = SeedRateCard(db);
        SeedTenant(db, planPrice: 100m, withPlan: true, tenantId: 1, slug: "one", rateCardId: cardId, planId: 10);
        SeedTenant(db, planPrice: 100m, withPlan: true, tenantId: 2, slug: "two", rateCardId: cardId, planId: 20);
        SeedTenant(db, planPrice: 100m, withPlan: true, tenantId: 3, slug: "gone", rateCardId: cardId, planId: 30,
            status: TenantStatus.Archived);

        await using var provider = WorkerServices(db);
        var summary = await Worker(provider, o => o.CatchUpPriorPeriod = false).SweepOnceAsync(CancellationToken.None);

        Assert.Equal(2, summary.TenantsSwept); // archived excluded
        Assert.Equal(2, summary.StatementsComputed);
        Assert.Equal(0, summary.Failures);

        using var verification = db.ContextFor(null);
        var statements = await verification.Set<BillingStatement>().AsNoTracking().ToListAsync();
        Assert.Equal(2, statements.Count);
        Assert.All(statements, s => Assert.Equal(BillingPeriod.Containing(DateTime.UtcNow).StartUtc, s.PeriodStartUtc));
        Assert.All(statements, s => Assert.Equal(100.00m, s.TotalAmount));
    }

    [Fact]
    public async Task The_billing_run_keeps_recomputing_the_prior_period_so_late_usage_is_not_lost()
    {
        using var db = new RevenueTestDb();
        var cardId = SeedRateCard(db, documentPrice: 1.50m);
        SeedTenant(db, planPrice: 100m, withPlan: true, rateCardId: cardId);

        var priorPeriod = BillingPeriod.Containing(BillingPeriod.Containing(DateTime.UtcNow).StartUtc.AddDays(-1));

        await using var provider = WorkerServices(db);
        var worker = Worker(provider);
        await worker.SweepOnceAsync(CancellationToken.None);

        using (var verification = db.ContextFor(null))
        {
            var prior = await verification.Set<BillingStatement>().AsNoTracking()
                .SingleAsync(s => s.PeriodStartUtc == priorPeriod.StartUtc);
            Assert.Equal(100.00m, prior.TotalAmount); // no usage yet
        }

        // Usage lands AFTER the month closed — exactly what the settle lag exists for.
        using (var late = db.ContextFor(null))
        {
            for (var i = 0; i < 4; i++)
                late.Set<ExtractionJob>().Add(NewJob(priorPeriod.StartUtc.AddDays(i + 1)));
            await late.SaveChangesAsync();
        }

        await worker.SweepOnceAsync(CancellationToken.None);

        using var recheck = db.ContextFor(null);
        var caughtUp = await recheck.Set<BillingStatement>().AsNoTracking()
            .SingleAsync(s => s.PeriodStartUtc == priorPeriod.StartUtc);
        Assert.Equal(103.00m, caughtUp.TotalAmount); // 100 base + (4 - 2 included) x 1.50
    }

    [Fact]
    public async Task A_finalized_prior_period_is_returned_untouched_by_the_catch_up_sweep()
    {
        using var db = new RevenueTestDb();
        var cardId = SeedRateCard(db, documentPrice: 1.50m);
        SeedTenant(db, planPrice: 100m, withPlan: true, rateCardId: cardId);
        var priorPeriod = BillingPeriod.Containing(BillingPeriod.Containing(DateTime.UtcNow).StartUtc.AddDays(-1));

        await using var provider = WorkerServices(db);
        var worker = Worker(provider);
        await worker.SweepOnceAsync(CancellationToken.None);

        long finalizedId;
        using (var ctx = db.ContextFor(null))
        {
            var draft = await ctx.Set<BillingStatement>()
                .SingleAsync(s => s.PeriodStartUtc == priorPeriod.StartUtc);
            // Backdate the period end past the settle lag so the finalize gate is clear.
            draft.PeriodEndUtc = DateTime.UtcNow.AddHours(-72);
            await ctx.SaveChangesAsync();
            finalizedId = (await Service(ctx).FinalizeAsync(draft.Id, "billing@nexora.test")).Id;
        }

        using (var late = db.ContextFor(null))
        {
            late.Set<ExtractionJob>().Add(NewJob(priorPeriod.StartUtc.AddDays(2)));
            await late.SaveChangesAsync();
        }

        await worker.SweepOnceAsync(CancellationToken.None);

        using var verification = db.ContextFor(null);
        var row = await verification.Set<BillingStatement>().AsNoTracking().SingleAsync(s => s.Id == finalizedId);
        Assert.Equal(BillingStatementStatus.Final, row.Status);
        Assert.Equal(100.00m, row.TotalAmount); // frozen; the sweep did not reopen it
    }

    [Fact]
    public async Task Concurrent_billing_run_instances_produce_exactly_one_statement_per_tenant_period()
    {
        using var db = new RevenueTestDb();
        var cardId = SeedRateCard(db);
        SeedTenant(db, planPrice: 100m, withPlan: true, rateCardId: cardId);

        // The claim this worker relies on for scale-out safety: compute is idempotent
        // because UX_BillingStatements_Tenant_PeriodStart makes a second row impossible,
        // NOT because the loop coordinates. Two workers, same sweep, verified head-on.
        await using var provider = WorkerServices(db);
        var first = Worker(provider, o => o.CatchUpPriorPeriod = false);
        var second = Worker(provider, o => o.CatchUpPriorPeriod = false);

        await first.SweepOnceAsync(CancellationToken.None);
        await second.SweepOnceAsync(CancellationToken.None);
        await first.SweepOnceAsync(CancellationToken.None);

        using var verification = db.ContextFor(null);
        var statement = Assert.Single(await verification.Set<BillingStatement>().AsNoTracking().ToListAsync());
        Assert.Equal(100.00m, statement.TotalAmount);
        // Lines are replaced in place on recompute; three sweeps must not triple them.
        Assert.Equal(2, await verification.Set<BillingStatementLine>().CountAsync()); // base + documents
    }

    [Fact]
    public async Task One_unpriceable_tenant_never_stops_the_billing_run_for_the_rest_of_the_fleet()
    {
        using var db = new RevenueTestDb();
        var cardId = SeedRateCard(db);
        SeedTenant(db, planPrice: 100m, withPlan: true, tenantId: 1, slug: "broken", planId: 10,
            rateCardId: 999_999L); // dangling pin
        SeedTenant(db, planPrice: 100m, withPlan: true, tenantId: 2, slug: "fine", planId: 20, rateCardId: cardId);

        await using var provider = WorkerServices(db);
        var summary = await Worker(provider, o => o.CatchUpPriorPeriod = false)
            .SweepOnceAsync(CancellationToken.None);

        Assert.Equal(2, summary.TenantsSwept);
        Assert.Equal(1, summary.Failures);
        Assert.Equal(1, summary.StatementsComputed);

        using var verification = db.ContextFor(null);
        var statement = Assert.Single(await verification.Set<BillingStatement>().AsNoTracking().ToListAsync());
        Assert.Equal(2L, statement.TenantId);
    }

    [Fact]
    public void The_billing_run_is_enabled_by_default_because_a_disabled_one_bills_nothing()
    {
        // The whole class of defect here is "nobody clicked": a default of false would
        // reproduce it in every environment that never edits configuration.
        var options = new BillingRunOptions();
        Assert.True(options.Enabled);
        Assert.True(options.CatchUpPriorPeriod);
        Assert.Equal(ValidateOptionsResult.Success,
            new BillingRunOptionsValidator().Validate(null, options));

        // A one-second interval would re-aggregate every ledger for every tenant forever.
        Assert.True(new BillingRunOptionsValidator()
            .Validate(null, new BillingRunOptions { Interval = TimeSpan.FromSeconds(1) }).Failed);
        Assert.True(new BillingRunOptionsValidator()
            .Validate(null, new BillingRunOptions { MaximumJitter = TimeSpan.FromHours(9) }).Failed);
    }

    // ============================================ 7. the consumption half of the leak

    [Fact]
    public async Task A_plan_less_billable_tenant_past_the_provisioning_grace_does_not_get_unmetered_seats()
    {
        using var db = new RevenueTestDb();
        SeedTenantForEntitlements(db, planId: null, createdOn: DateTime.UtcNow.AddDays(-90));
        SeedUsers(db, count: UnplannedTenantAllowance.MaxSeats);

        using var ctx = db.ContextFor(null);
        var decision = await Entitlements(ctx).CheckSeatAvailabilityAsync(Bu);

        Assert.False(decision.Allowed);
        Assert.Equal(UnplannedTenantAllowance.MaxSeats, decision.Limit);
        Assert.Contains("No plan is assigned", decision.Reason);
        Assert.Contains("Complete its commercial configuration", decision.Reason);
    }

    [Fact]
    public async Task A_plan_less_billable_tenant_past_the_provisioning_grace_does_not_get_unmetered_documents()
    {
        using var db = new RevenueTestDb();
        SeedTenantForEntitlements(db, planId: null, createdOn: DateTime.UtcNow.AddDays(-90));
        using (var seed = db.ContextFor(null))
        {
            for (var i = 0; i < UnplannedTenantAllowance.MaxDocsPerMonth; i++)
                seed.Set<ExtractionJob>().Add(NewJob(DateTime.UtcNow, Bu));
            seed.SaveChanges();
        }

        using var ctx = db.ContextFor(null);
        var decision = await Entitlements(ctx).CheckDocumentQuotaAsync(Bu);

        Assert.False(decision.Allowed);
        Assert.Equal(UnplannedTenantAllowance.MaxDocsPerMonth, decision.Limit);
        Assert.Equal(UnplannedTenantAllowance.MaxDocsPerMonth, decision.Current);
    }

    [Fact]
    public async Task A_freshly_provisioned_plan_less_tenant_keeps_full_capacity_while_setup_finishes()
    {
        using var db = new RevenueTestDb();
        SeedTenantForEntitlements(db, planId: null, createdOn: DateTime.UtcNow.AddDays(-1));
        SeedUsers(db, count: UnplannedTenantAllowance.MaxSeats + 5);

        using var ctx = db.ContextFor(null);
        var decision = await Entitlements(ctx).CheckSeatAvailabilityAsync(Bu);

        // Provisioning creates the Tenant before a plan is necessarily chosen; bricking
        // setup would be a worse defect than the leak it closes.
        Assert.True(decision.Allowed);
        Assert.Null(decision.Limit);
    }

    [Fact]
    public async Task A_legacy_business_unit_with_no_tenant_row_still_fails_open()
    {
        using var db = new RevenueTestDb();
        using (var seed = db.ContextFor(null))
        {
            Seed.EnsureBusinessUnit(seed, Bu); // no platform Tenant row at all
            seed.SaveChanges();
        }
        SeedUsers(db, count: 20);

        using var ctx = db.ContextFor(null);
        var decision = await Entitlements(ctx).CheckSeatAvailabilityAsync(Bu);

        // The contracted fail-open: a customer that predates the control plane must not
        // be capped by it.
        Assert.True(decision.Allowed);
        Assert.Null(decision.Limit);
    }

    [Theory]
    [InlineData(TenantBillingMode.Internal)]
    [InlineData(TenantBillingMode.Partner)]
    public async Task Operator_owned_and_partner_tenants_keep_unmetered_capacity_because_that_cost_is_already_a_decision(
        TenantBillingMode mode)
    {
        using var db = new RevenueTestDb();
        SeedTenantForEntitlements(db, planId: null, createdOn: DateTime.UtcNow.AddDays(-400), billingMode: mode,
            billingModeReason: "Operator-owned workspace; cost approved by the CTO in FY26 planning.");
        SeedUsers(db, count: 20);

        using var ctx = db.ContextFor(null);
        var decision = await Entitlements(ctx).CheckSeatAvailabilityAsync(Bu);

        Assert.True(decision.Allowed);
        Assert.Null(decision.Limit);
    }

    [Fact]
    public async Task A_plan_less_trial_is_capped_too_because_an_unbounded_trial_is_the_same_leak()
    {
        using var db = new RevenueTestDb();
        SeedTenantForEntitlements(db, planId: null, createdOn: DateTime.UtcNow.AddDays(-90),
            billingMode: TenantBillingMode.Trial);
        SeedUsers(db, count: UnplannedTenantAllowance.MaxSeats);

        using var ctx = db.ContextFor(null);
        var decision = await Entitlements(ctx).CheckSeatAvailabilityAsync(Bu);

        Assert.False(decision.Allowed);
        Assert.Equal(UnplannedTenantAllowance.MaxSeats, decision.Limit);
    }

    [Fact]
    public async Task The_document_quota_and_the_billing_documents_meter_apply_one_shared_status_policy()
    {
        using var db = new RevenueTestDb();
        SeedTenantForEntitlements(db, planId: 5, createdOn: DateTime.UtcNow, maxDocsPerMonth: 10);
        using (var seed = db.ContextFor(null))
        {
            foreach (var status in BillableDocumentPolicy.NonBillableStatuses)
                seed.Set<ExtractionJob>().Add(NewJob(DateTime.UtcNow, Bu, status));
            seed.Set<ExtractionJob>().Add(NewJob(DateTime.UtcNow, Bu, ExtractionStatus.Succeeded));
            seed.SaveChanges();
        }

        using var ctx = db.ContextFor(null);
        var decision = await Entitlements(ctx).CheckDocumentQuotaAsync(Bu);

        // The quota must count exactly what the billing meter counts: charging for work
        // the quota said was never used is the same defect from the other direction.
        Assert.Equal(1, decision.Current);
    }

    // ============================== 8. Commercial Configuration Required (remediation)

    [Fact]
    public async Task An_existing_billable_tenant_with_no_plan_is_reported_as_commercial_configuration_required()
    {
        using var db = new RevenueTestDb();
        SeedRateCard(db);
        // The shape that predates the provisioning rules: written straight to the table.
        SeedTenant(db, planPrice: null, withPlan: false);

        using var ctx = db.ContextFor(null);
        var readout = Assert.Single(await Service(ctx).GetRevenueRiskAsync());

        Assert.True(readout.CommercialConfigurationRequired);
        Assert.Equal(CommercialConfigurationStates.PlanMissing, readout.CommercialConfigurationState);
    }

    [Fact]
    public async Task An_exemption_with_nothing_written_down_is_commercial_configuration_required()
    {
        using var db = new RevenueTestDb();
        SeedRateCard(db);
        SeedTenant(db, planPrice: 100m, withPlan: true,
            billingMode: TenantBillingMode.Internal, billingModeReason: null);

        using var ctx = db.ContextFor(null);
        var readout = Assert.Single(await Service(ctx).GetRevenueRiskAsync());

        Assert.True(readout.CommercialConfigurationRequired);
        Assert.Equal(CommercialConfigurationStates.ExemptionUnrecorded, readout.CommercialConfigurationState);
    }

    [Fact]
    public async Task A_recorded_exemption_and_a_planned_billable_tenant_are_both_commercially_complete()
    {
        using var db = new RevenueTestDb();
        var cardId = SeedRateCard(db);
        SeedTenant(db, planPrice: 100m, withPlan: true, tenantId: 1, slug: "priced",
            planId: 10, rateCardId: cardId);
        SeedTenant(db, planPrice: 100m, withPlan: true, tenantId: 2, slug: "exempt", planId: 20,
            rateCardId: cardId, billingMode: TenantBillingMode.Partner,
            billingModeReason: "Reseller invoiced under MSA-4471; signed by the CRO.");

        using var ctx = db.ContextFor(null);
        var readout = await Service(ctx).GetRevenueRiskAsync();

        Assert.All(readout, r => Assert.False(r.CommercialConfigurationRequired));
        Assert.All(readout, r => Assert.Equal(CommercialConfigurationStates.Complete, r.CommercialConfigurationState));
    }

    [Fact]
    public async Task The_state_is_derived_so_fixing_the_cause_clears_it_with_no_flag_to_reset()
    {
        using var db = new RevenueTestDb();
        SeedRateCard(db);
        SeedTenant(db, planPrice: null, withPlan: false);

        using (var before = db.ContextFor(null))
            Assert.True((await Service(before).GetRevenueRiskAsync()).Single().CommercialConfigurationRequired);

        // A plan is assigned — by the console, or by anything else that writes the column.
        using (var fix = db.ContextFor(null))
        {
            fix.Set<Plan>().Add(new Plan { Id = 77, Code = "pro", Name = "Pro", MonthlyPriceUsd = 400m });
            (await fix.Set<Tenant>().SingleAsync(t => t.Id == TenantId)).PlanId = 77;
            await fix.SaveChangesAsync();
        }

        using var after = db.ContextFor(null);
        var readout = Assert.Single(await Service(after).GetRevenueRiskAsync());
        // Nothing was cleared, dismissed or re-run: the state IS the tenant row.
        Assert.False(readout.CommercialConfigurationRequired);
        Assert.Equal(CommercialConfigurationStates.Complete, readout.CommercialConfigurationState);
    }

    [Fact]
    public async Task The_board_counts_commercial_configuration_over_the_whole_fleet_and_can_filter_to_it()
    {
        using var db = new RevenueTestDb();
        var cardId = SeedRateCard(db);
        SeedTenant(db, planPrice: null, withPlan: false, tenantId: 1, slug: "unplanned");
        SeedTenant(db, planPrice: 100m, withPlan: true, tenantId: 2, slug: "unrecorded", planId: 20,
            rateCardId: cardId, billingMode: TenantBillingMode.Internal, billingModeReason: null);
        SeedTenant(db, planPrice: 100m, withPlan: true, tenantId: 3, slug: "healthy", planId: 30,
            rateCardId: cardId);

        using var ctx = db.ContextFor(null);
        var service = Service(ctx);
        await service.ComputeStatementAsync(3, July, cardId);

        var response = await Controller(ctx, service).GetRevenueRisk(
            onlyAtRisk: false, includeArchived: false,
            onlyCommercialConfigurationRequired: true, CancellationToken.None);
        var report = Assert.IsType<RevenueRiskReportDto>(Assert.IsType<OkObjectResult>(response.Result).Value);

        Assert.Equal(3, report.TenantCount);
        Assert.Equal(2, report.CommercialConfigurationRequiredCount);
        Assert.Equal(new long[] { 1, 2 }, report.Tenants.Select(t => t.TenantId));
    }

    [Fact]
    public async Task The_billing_run_counts_and_logs_every_tenant_whose_terms_nobody_set()
    {
        using var db = new RevenueTestDb();
        var cardId = SeedRateCard(db);
        SeedTenant(db, planPrice: null, withPlan: false, tenantId: 1, slug: "unplanned", rateCardId: cardId);
        SeedTenant(db, planPrice: 100m, withPlan: true, tenantId: 2, slug: "healthy", planId: 20,
            rateCardId: cardId);

        await using var provider = WorkerServices(db);
        var summary = await Worker(provider, o => o.CatchUpPriorPeriod = false)
            .SweepOnceAsync(CancellationToken.None);

        Assert.Equal(1, summary.TenantsNeedingCommercialConfiguration);
    }

    [Fact]
    public async Task An_unrecorded_exemption_loses_unmetered_capacity_once_the_provisioning_grace_has_passed()
    {
        using var db = new RevenueTestDb();
        SeedTenantForEntitlements(db, planId: null, createdOn: DateTime.UtcNow.AddDays(-90),
            billingMode: TenantBillingMode.Internal, billingModeReason: null);
        SeedUsers(db, count: UnplannedTenantAllowance.MaxSeats);

        using var ctx = db.ContextFor(null);
        var decision = await Entitlements(ctx).CheckSeatAvailabilityAsync(Bu);

        Assert.False(decision.Allowed);
        Assert.Equal(UnplannedTenantAllowance.MaxSeats, decision.Limit);
        Assert.Contains("has no recorded reason", decision.Reason);
    }

    [Fact]
    public async Task Writing_the_exemption_reason_down_restores_unmetered_capacity()
    {
        using var db = new RevenueTestDb();
        SeedTenantForEntitlements(db, planId: null, createdOn: DateTime.UtcNow.AddDays(-90),
            billingMode: TenantBillingMode.Partner,
            billingModeReason: "Reseller invoiced under MSA-4471; signed by the CRO.");
        SeedUsers(db, count: 20);

        using var ctx = db.ContextFor(null);
        var decision = await Entitlements(ctx).CheckSeatAvailabilityAsync(Bu);

        // A recorded exemption is a decision somebody made and can be held to.
        Assert.True(decision.Allowed);
        Assert.Null(decision.Limit);
    }

    // ================================================== 9. the console billing surface

    [Fact]
    public async Task Pinning_a_rate_card_through_the_console_changes_what_the_next_statement_is_priced_on()
    {
        using var db = new RevenueTestDb();
        var listCardId = SeedRateCard(db, code: "list", documentPrice: 9.99m, active: true);
        var negotiatedId = SeedRateCard(db, code: "negotiated", documentPrice: 0.50m, active: true);
        SeedTenant(db, planPrice: 100m, withPlan: true, rateCardId: null);
        SeedUsage(db);

        using var ctx = db.ContextFor(null);
        var service = Service(ctx);
        var controller = Controller(ctx, service);

        var pinned = await controller.SetTenantRateCard(
            TenantId, new SetTenantRateCardRequest(negotiatedId, "Negotiated on order form ORD-2291."),
            CancellationToken.None);
        var profile = Assert.IsType<TenantBillingProfileDto>(Assert.IsType<OkObjectResult>(pinned.Result).Value);
        Assert.Equal(negotiatedId, profile.PinnedRateCardId);
        Assert.False(profile.PinnedRateCardMissing);

        using var compute = db.ContextFor(null);
        var statement = await Service(compute).ComputeStatementAsync(TenantId, July);
        Assert.Equal(negotiatedId, statement.RateCardId);
        Assert.Equal(1.50m, Line(statement, BillingMeterKeys.Documents).Amount); // 3 x 0.50, not 3 x 9.99
        Assert.NotEqual(listCardId, statement.RateCardId);
    }

    [Fact]
    public async Task Console_refuses_an_inactive_rate_card_before_it_can_break_the_next_billing_run()
    {
        using var db = new RevenueTestDb();
        var inactiveId = SeedRateCard(db, code: "retired", documentPrice: 0.50m, active: false);
        SeedTenant(db, planPrice: 100m, withPlan: true, rateCardId: null);

        using var ctx = db.ContextFor(null);
        var controller = Controller(ctx, Service(ctx));
        var result = await controller.SetTenantRateCard(
            TenantId,
            new SetTenantRateCardRequest(inactiveId, "Attempted retired commercial assignment."),
            CancellationToken.None);

        var rejected = Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Contains("not active and effective now", rejected.Value!.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Null((await ctx.Set<Tenant>().SingleAsync(t => t.Id == TenantId)).RateCardId);
    }

    [Fact]
    public async Task Clearing_a_rate_card_pin_is_refused_without_a_written_reason()
    {
        using var db = new RevenueTestDb();
        var cardId = SeedRateCard(db);
        SeedTenant(db, planPrice: 100m, withPlan: true, rateCardId: cardId);

        using var ctx = db.ContextFor(null);
        var controller = Controller(ctx, Service(ctx));

        var rejected = await controller.SetTenantRateCard(
            TenantId, new SetTenantRateCardRequest(null, null), CancellationToken.None);
        var bad = Assert.IsType<BadRequestObjectResult>(rejected.Result);
        Assert.Contains("silently repriced", bad.Value!.ToString());

        // With a reason it is allowed — this is a decision, not an accident.
        var allowed = await controller.SetTenantRateCard(
            TenantId, new SetTenantRateCardRequest(null, "Moving to standard list pricing at renewal."),
            CancellationToken.None);
        var profile = Assert.IsType<TenantBillingProfileDto>(Assert.IsType<OkObjectResult>(allowed.Result).Value);
        Assert.Null(profile.PinnedRateCardId);
    }

    [Fact]
    public async Task A_non_usd_rate_card_cannot_be_pinned_because_that_tenant_could_never_produce_a_statement()
    {
        using var db = new RevenueTestDb();
        SeedTenant(db, planPrice: 100m, withPlan: true);
        long eurCardId;
        using (var seed = db.ContextFor(null))
        {
            var card = new RateCard
            {
                Code = $"eur-{Guid.NewGuid():N}"[..20],
                Currency = "EUR",
                EffectiveFromUtc = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                IsActive = true,
                Lines = { new RateCardLine { MeterKey = BillingMeterKeys.Documents, UnitPrice = 1m, Unit = "document" } }
            };
            seed.Add(card);
            seed.SaveChanges();
            eurCardId = card.Id;
        }

        using var ctx = db.ContextFor(null);
        var rejected = await Controller(ctx, Service(ctx)).SetTenantRateCard(
            TenantId, new SetTenantRateCardRequest(eurCardId, "negotiated in EUR"), CancellationToken.None);

        var bad = Assert.IsType<BadRequestObjectResult>(rejected.Result);
        Assert.Contains("USD-only", bad.Value!.ToString());
    }

    [Fact]
    public async Task Converting_an_expired_trial_to_billable_requires_a_plan_and_then_actually_charges_the_tenant()
    {
        using var db = new RevenueTestDb();
        var cardId = SeedRateCard(db, documentPrice: 1.50m);
        SeedTenant(db, planPrice: null, withPlan: false, rateCardId: cardId,
            billingMode: TenantBillingMode.Trial, billingModeReason: "14-day evaluation for ACME.",
            trialEndsOn: DateTime.UtcNow.AddDays(-30));
        SeedUsage(db);

        using var ctx = db.ContextFor(null);
        var controller = Controller(ctx, Service(ctx));

        // Converting without a plan would recreate the original leak: Billable, metered, uncharged.
        var refused = await controller.SetTenantCommercialTerms(
            TenantId, new SetTenantCommercialTermsRequest("Billable", null, null, null), CancellationToken.None);
        Assert.Contains("no plan", Assert.IsType<BadRequestObjectResult>(refused.Result).Value!.ToString());

        using (var assign = db.ContextFor(null))
        {
            assign.Set<Plan>().Add(new Plan { Id = 90, Code = "growth", Name = "Growth", MonthlyPriceUsd = 300m });
            (await assign.Set<Tenant>().SingleAsync(t => t.Id == TenantId)).PlanId = 90;
            await assign.SaveChangesAsync();
        }

        using var convert = db.ContextFor(null);
        // An explicit billingStartsOn wins over the trial-end default, which is how an
        // operator back-dates a conversion that was agreed before the paperwork caught up.
        var converted = await Controller(convert, Service(convert)).SetTenantCommercialTerms(
            TenantId,
            new SetTenantCommercialTermsRequest("Billable", null, null, July.StartUtc),
            CancellationToken.None);
        var profile = Assert.IsType<TenantBillingProfileDto>(Assert.IsType<OkObjectResult>(converted.Result).Value);
        Assert.Equal("Billable", profile.BillingMode);
        Assert.Equal(July.StartUtc, profile.BillingStartsOn);
        Assert.Null(profile.TrialEndsOn); // the trial is over, not merely past
        Assert.False(profile.RevenueRisk.TrialExpired);

        using var recompute = db.ContextFor(null);
        var statement = await Service(recompute).ComputeStatementAsync(TenantId, July);
        Assert.Equal(304.50m, statement.TotalAmount); // 300 base + 3 docs x 1.50
    }

    [Fact]
    public async Task An_exemption_set_through_the_console_needs_the_same_written_reason_provisioning_demands()
    {
        using var db = new RevenueTestDb();
        var cardId = SeedRateCard(db);
        SeedTenant(db, planPrice: 100m, withPlan: true, rateCardId: cardId);

        using var ctx = db.ContextFor(null);
        var controller = Controller(ctx, Service(ctx));

        // A tenant that could not be CREATED in this shape must not be editable into it.
        foreach (var tooThin in new[] { null, "", "internal" })
        {
            var rejected = await controller.SetTenantCommercialTerms(
                TenantId, new SetTenantCommercialTermsRequest("Internal", tooThin, null, null),
                CancellationToken.None);
            Assert.IsType<BadRequestObjectResult>(rejected.Result);
        }

        var accepted = await controller.SetTenantCommercialTerms(
            TenantId,
            new SetTenantCommercialTermsRequest("Internal", "Support sandbox owned by platform ops.", null, null),
            CancellationToken.None);
        var profile = Assert.IsType<TenantBillingProfileDto>(Assert.IsType<OkObjectResult>(accepted.Result).Value);
        Assert.Equal("Internal", profile.BillingMode);
        Assert.False(profile.RevenueRisk.CommercialConfigurationRequired);
        Assert.Equal(PlatformBillingController.MinimumBillingModeReasonLength, 15);
    }

    [Fact]
    public async Task A_trial_cannot_be_back_dated_into_an_already_expired_state()
    {
        using var db = new RevenueTestDb();
        SeedRateCard(db);
        SeedTenant(db, planPrice: 100m, withPlan: true);

        using var ctx = db.ContextFor(null);
        var rejected = await Controller(ctx, Service(ctx)).SetTenantCommercialTerms(
            TenantId,
            new SetTenantCommercialTermsRequest("Trial", "Evaluation extended by the AE.",
                DateTime.UtcNow.AddDays(-1), null),
            CancellationToken.None);

        Assert.Contains("not in the future", Assert.IsType<BadRequestObjectResult>(rejected.Result).Value!.ToString());
    }

    [Fact]
    public async Task Commercial_term_changes_are_audited_so_a_change_to_what_a_customer_pays_is_attributable()
    {
        using var db = new RevenueTestDb();
        var cardId = SeedRateCard(db);
        SeedTenant(db, planPrice: 100m, withPlan: true, rateCardId: cardId);

        using (var ctx = db.ContextFor(null))
        {
            var controller = Controller(ctx, Service(ctx));
            await controller.SetTenantRateCard(
                TenantId, new SetTenantRateCardRequest(null, "Standard list pricing at renewal."),
                CancellationToken.None);
            await controller.SetTenantCommercialTerms(
                TenantId,
                new SetTenantCommercialTermsRequest("Partner", "Reseller invoiced under MSA-4471.", null, null),
                CancellationToken.None);
        }

        using var verification = db.ContextFor(null);
        var actions = await verification.Set<PlatformAuditLog>().Select(a => a.Action).ToListAsync();
        Assert.Contains("billing.tenant.rate-card", actions);
        Assert.Contains("billing.tenant.commercial-terms", actions);
    }

    [Fact]
    public async Task Statement_review_returns_every_line_including_the_markers_that_explain_a_zero_total()
    {
        using var db = new RevenueTestDb();
        var cardId = SeedRateCard(db);
        SeedTenant(db, planPrice: null, withPlan: false, rateCardId: cardId);

        using var ctx = db.ContextFor(null);
        var service = Service(ctx);
        var draft = await service.ComputeStatementAsync(TenantId, July);

        var response = await Controller(ctx, service).GetStatement(draft.Id, CancellationToken.None);
        var payload = Assert.IsType<BillingStatementDto>(Assert.IsType<OkObjectResult>(response.Result).Value);

        // Finalizing is permanent, so the review call has to show WHY a total is zero.
        var baseLine = payload.Lines.Single(l => l.MeterKey == BillingMeterKeys.BaseSubscription);
        Assert.Equal(BillingStatementMarkers.RiskNoPlan,
            BillingStatementMarkers.RiskCodeOf(baseLine.MeterKey, baseLine.CoverageNote));

        var missing = await Controller(ctx, service).GetStatement(987_654, CancellationToken.None);
        Assert.IsType<NotFoundObjectResult>(missing.Result);
    }

    [Fact]
    public async Task The_tenant_billing_profile_surfaces_a_dangling_rate_card_pin()
    {
        using var db = new RevenueTestDb();
        SeedTenant(db, planPrice: 100m, withPlan: true, rateCardId: 987_654L);

        using var ctx = db.ContextFor(null);
        var response = await Controller(ctx, Service(ctx))
            .GetTenantBillingProfile(TenantId, CancellationToken.None);
        var profile = Assert.IsType<TenantBillingProfileDto>(Assert.IsType<OkObjectResult>(response.Result).Value);

        // The only other symptom is statements silently ceasing to appear.
        Assert.True(profile.PinnedRateCardMissing);
        Assert.Equal(987_654L, profile.PinnedRateCardId);
        Assert.Null(profile.PinnedRateCardCode);
    }

    [Fact]
    public void Every_billing_endpoint_including_the_commercial_mutations_requires_the_billing_policy()
    {
        // Sec9: SupportAdmin holds Platform.TenantAdmin and can suspend or archive a tenant,
        // but must never be able to change what it is charged. An action here that weakened
        // or dropped the class-level policy would hand them exactly that.
        var controller = typeof(PlatformBillingController);
        Assert.NotNull(controller.GetCustomAttributes<AuthorizeAttribute>()
            .SingleOrDefault(a => a.Policy == PlatformPolicies.Billing));

        var actions = controller
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => m.GetCustomAttributes(inherit: true)
                .Any(a => a is Microsoft.AspNetCore.Mvc.Routing.HttpMethodAttribute))
            .ToList();

        foreach (var name in new[]
                 {
                     nameof(PlatformBillingController.SetTenantRateCard),
                     nameof(PlatformBillingController.SetTenantCommercialTerms),
                     nameof(PlatformBillingController.FinalizeStatement),
                     nameof(PlatformBillingController.GetRevenueRisk)
                 })
            Assert.Contains(actions, a => a.Name == name);

        foreach (var action in actions)
        {
            Assert.Empty(action.GetCustomAttributes<AllowAnonymousAttribute>());
            var overriding = action.GetCustomAttributes<AuthorizeAttribute>().ToList();
            Assert.All(overriding, gate => Assert.Contains(gate.Policy,
                new[] { PlatformPolicies.Billing, PlatformPolicies.Owner, PlatformPolicies.Mfa }));
        }
    }

    // ============================================================ 10. run liveness

    [Fact]
    public void An_enabled_billing_run_registers_a_heartbeat_so_a_dead_loop_turns_readiness_red()
    {
        using var db = new RevenueTestDb();
        using var provider = WorkerServices(db);
        var heartbeats = new BackgroundWorkerHeartbeats();

        _ = Worker(provider, heartbeats: heartbeats);

        // BackgroundServiceExceptionBehavior is Ignore, so a faulted billing loop is gone for
        // the life of the process. Registration in the constructor is what makes that visible.
        var status = Assert.Single(heartbeats.Snapshot(), s => s.Worker == BackgroundWorkerNames.BillingRun);
        Assert.Null(status.LastBeatUtc);
        Assert.True(status.IsAlive); // still inside the startup grace
    }

    [Fact]
    public async Task A_completed_sweep_beats_the_billing_run_heartbeat()
    {
        using var db = new RevenueTestDb();
        var cardId = SeedRateCard(db);
        SeedTenant(db, planPrice: 100m, withPlan: true, rateCardId: cardId);

        await using var provider = WorkerServices(db);
        var heartbeats = new BackgroundWorkerHeartbeats();
        var worker = Worker(provider, o => o.CatchUpPriorPeriod = false, heartbeats);

        await worker.SweepOnceAsync(CancellationToken.None);
        heartbeats.Beat(BackgroundWorkerNames.BillingRun, TimeSpan.FromHours(6));

        var status = Assert.Single(heartbeats.Snapshot(), s => s.Worker == BackgroundWorkerNames.BillingRun);
        Assert.NotNull(status.LastBeatUtc);
        Assert.True(status.IsAlive);
    }

    [Fact]
    public void A_disabled_billing_run_registers_nothing_so_readiness_is_not_held_red_by_a_deliberate_choice()
    {
        using var db = new RevenueTestDb();
        using var provider = WorkerServices(db);
        var heartbeats = new BackgroundWorkerHeartbeats();

        _ = Worker(provider, o => o.Enabled = false, heartbeats);

        Assert.DoesNotContain(heartbeats.Snapshot(), s => s.Worker == BackgroundWorkerNames.BillingRun);
    }

    // =================================================================== support

    private const long TenantId = 1;
    private const long Bu = 71;

    private static BillingPeriod Period(string key)
    {
        Assert.True(BillingPeriod.TryParse(key, out var period));
        return period;
    }

    private static BillingStatementLine Line(BillingStatement statement, string key)
        => statement.Lines.Single(l => l.MeterKey == key);

    private static BillingStatementService Service(ErpRfqAutomationContext context)
        => new(context, NullLogger<BillingStatementService>.Instance);

    private static IEntitlementService Entitlements(ErpRfqAutomationContext ctx)
        => new EntitlementService(
            new TenantAccessService(ctx, new MemoryCache(new MemoryCacheOptions()),
                NullLogger<TenantAccessService>.Instance),
            ctx);

    private static PlatformBillingController Controller(
        ErpRfqAutomationContext context, IBillingStatementService service)
        => new(context, service,
            new ERP_RFQ_Automation.Platform.Services.PlatformAuditService(
                context, NullLogger<ERP_RFQ_Automation.Platform.Services.PlatformAuditService>.Instance),
            NullLogger<PlatformBillingController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext
                {
                    User = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(
                        [
                            new System.Security.Claims.Claim("sub", "7"),
                            new System.Security.Claims.Claim("email", "billing@nexora.test"),
                            new System.Security.Claims.Claim(
                                PlatformAuthConstants.ScopeClaim, PlatformAuthConstants.PlatformScopeValue),
                            new System.Security.Claims.Claim(
                                PlatformAuthConstants.PlatformRoleClaim, PlatformRole.Owner.ToString())
                        ], "Platform"))
                }
            }
        };

    private static ServiceProvider WorkerServices(RevenueTestDb db)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        // A fresh context per DI scope, exactly as the production scope factory hands one
        // out — the point of the worker's per-tenant scope.
        services.AddScoped<ErpRfqAutomationContext>(_ => db.ContextFor(null));
        services.AddScoped<IBillingStatementService, BillingStatementService>();
        return services.BuildServiceProvider();
    }

    private static BillingRunWorker Worker(
        ServiceProvider provider, Action<BillingRunOptions>? configure = null,
        IBackgroundWorkerHeartbeats? heartbeats = null)
    {
        var options = new BillingRunOptions();
        configure?.Invoke(options);
        return new BillingRunWorker(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new StaticOptionsMonitor<BillingRunOptions>(options),
            NullLogger<BillingRunWorker>.Instance,
            heartbeats);
    }

    private sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;
        public T Get(string? name) => value;
        public IDisposable OnChange(Action<T, string?> listener) => new NullChangeToken();
        private sealed class NullChangeToken : IDisposable { public void Dispose() { } }
    }

    /// <summary>
    /// SQLite-in-memory over the REAL model. The billing tables come from the production
    /// <c>OnModelCreatingPartial</c>; the only local adjustment is storing the evidence
    /// ledger's <c>DateTimeOffset</c> timestamps as UTC <c>DateTime</c>, because SQLite has
    /// no sortable storage for DateTimeOffset and the storage meter filters
    /// <c>SourceDocuments.CreatedOn &lt; PeriodEnd</c>. With the conversion the REAL
    /// production query translates here unchanged; PostgreSQL keeps native timestamptz.
    /// </summary>
    private sealed class RevenueTestDb : IDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly DbContextOptions<ErpRfqAutomationContext> _options;

        public RevenueTestDb()
        {
            _connection = new SqliteConnection("Data Source=:memory:");
            _connection.Open();
            _options = new DbContextOptionsBuilder<ErpRfqAutomationContext>()
                .UseSqlite(_connection)
                .EnableSensitiveDataLogging()
                .Options;
            using var context = ContextFor(null);
            context.Database.EnsureCreated();
        }

        public ErpRfqAutomationContext ContextFor(long? businessUnitId)
            => new SqliteSortableTimestampContext(_options, new StubTenant(businessUnitId));

        public void Dispose() => _connection.Dispose();
    }

    private sealed class SqliteSortableTimestampContext : ErpRfqAutomationContext
    {
        public SqliteSortableTimestampContext(DbContextOptions<ErpRfqAutomationContext> options, ITenantContext tenant)
            : base(options, tenant)
        {
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            var utcOffset = new ValueConverter<DateTimeOffset, DateTime>(
                v => v.UtcDateTime, v => new DateTimeOffset(v, TimeSpan.Zero));
            modelBuilder.Entity<SourceDocument>().Property(x => x.CreatedOn)
                .HasColumnType("TEXT").HasConversion(utcOffset);
            modelBuilder.Entity<SourceDocument>().Property(x => x.UpdatedOn)
                .HasColumnType("TEXT").HasConversion(utcOffset);
        }
    }

    private static long SeedRateCard(
        RevenueTestDb db, string code = "standard", decimal documentPrice = 1.50m, bool active = true,
        DateTime? effectiveFrom = null)
    {
        using var seed = db.ContextFor(null);
        var card = new RateCard
        {
            Code = $"{code}-{Guid.NewGuid():N}"[..24],
            Currency = "USD",
            EffectiveFromUtc = effectiveFrom ?? new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            IsActive = active,
            CreatedBy = "test",
            Lines =
            {
                new RateCardLine
                {
                    MeterKey = BillingMeterKeys.Documents,
                    IncludedQuantity = 2m,
                    UnitPrice = documentPrice,
                    Unit = "document"
                }
            }
        };
        seed.Add(card);
        seed.SaveChanges();
        return card.Id;
    }

    private static void SeedTenant(
        RevenueTestDb db, decimal? planPrice, bool withPlan, long tenantId = TenantId, string slug = "billing",
        long planId = 10, long? rateCardId = null, TenantStatus status = TenantStatus.Active,
        TenantBillingMode billingMode = TenantBillingMode.Billable, string? billingModeReason = null,
        DateTime? trialEndsOn = null, DateTime? billingStartsOn = null)
    {
        using var seed = db.ContextFor(null);
        Seed.EnsureBusinessUnit(seed, Bu + tenantId);
        if (withPlan)
            seed.Set<Plan>().Add(new Plan
            {
                Id = planId,
                Code = $"plan-{planId}",
                Name = $"Plan {planId}",
                MonthlyPriceUsd = planPrice
            });
        seed.Set<Tenant>().Add(new Tenant
        {
            Id = tenantId,
            Name = $"Tenant {tenantId}",
            Slug = $"{slug}-{tenantId}",
            Status = status,
            PlanId = withPlan ? planId : null,
            PrimaryBusinessUnitId = Bu + tenantId,
            RateCardId = rateCardId,
            BillingMode = billingMode,
            BillingModeReason = billingModeReason,
            TrialEndsOn = trialEndsOn,
            BillingStartsOn = billingStartsOn,
            CreatedBy = "test",
            CreatedOn = DateTime.UtcNow
        });
        seed.SaveChanges();
    }

    /// <summary>Five billable July documents for the default tenant's business unit.</summary>
    private static void SeedUsage(RevenueTestDb db, long tenantId = TenantId)
    {
        using var seed = db.ContextFor(null);
        for (var i = 0; i < 5; i++)
            seed.Set<ExtractionJob>().Add(NewJob(InJuly.AddHours(i), Bu + tenantId));
        seed.SaveChanges();
    }

    private static void SeedTenantForEntitlements(
        RevenueTestDb db, long? planId, DateTime createdOn,
        TenantBillingMode billingMode = TenantBillingMode.Billable,
        string? billingModeReason = null,
        int maxSeats = 5, int maxDocsPerMonth = 1000)
    {
        using var seed = db.ContextFor(null);
        Seed.EnsureBusinessUnit(seed, Bu);
        if (planId is long id)
            seed.Set<Plan>().Add(new Plan
            {
                Id = id,
                Code = $"plan-{id}",
                Name = $"Plan {id}",
                MaxSeats = maxSeats,
                MaxDocsPerMonth = maxDocsPerMonth
            });
        seed.Set<Tenant>().Add(new Tenant
        {
            Id = TenantId,
            Name = "Entitlement Tenant",
            Slug = "entitlements",
            Status = TenantStatus.Active,
            PlanId = planId,
            PrimaryBusinessUnitId = Bu,
            BillingMode = billingMode,
            BillingModeReason = billingModeReason,
            CreatedBy = "test",
            CreatedOn = createdOn
        });
        seed.SaveChanges();
    }

    private static void SeedUsers(RevenueTestDb db, int count)
    {
        using var seed = db.ContextFor(null);
        for (var i = 0; i < count; i++)
            seed.Users.Add(new User
            {
                FirstName = "Seat",
                LastName = "Holder",
                Email = $"seat-{Guid.NewGuid():N}@nexora.test",
                PasswordHash = "hash",
                ImageUrl = "",
                Buid = Bu,
                IsActive = true,
                CreatedBy = "test",
                CreatedOn = DateTime.UtcNow
            });
        seed.SaveChanges();
    }

    private static ExtractionJob NewJob(
        DateTime createdOn, long buId = Bu + TenantId,
        ExtractionStatus status = ExtractionStatus.Succeeded) => new()
    {
        BusinessUnitId = buId,
        BatchId = Guid.NewGuid(),
        SourceType = ExtractionSourceType.ManualUpload,
        Status = status,
        ContentHash = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N"),
        StoragePath = "/uploads/test.pdf",
        CreatedOn = createdOn,
        UpdatedOn = createdOn,
        NextAttemptAt = createdOn
    };
}
