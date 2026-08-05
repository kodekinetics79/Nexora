using System.Data.Common;
using System.Reflection;
using ERP_RFQ_Automation.AI;
using ERP_RFQ_Automation.Billing;
using ERP_RFQ_Automation.Billing.Controllers;
using ERP_RFQ_Automation.DocumentIntelligence.Persistence;
using ERP_RFQ_Automation.Extraction;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.MultiTenancy;
using ERP_RFQ_Automation.Platform.Auth;
using ERP_RFQ_Automation.Platform.Models;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Microsoft.Extensions.Logging.Abstractions;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// WS-C SaaS billing: meter aggregation from the real source ledgers
/// (ExtractionJobs / AiRequests / Users), rate-card charge math, idempotent
/// statement compute, duplicate-charge protection, finalize immutability and
/// endpoint authorization. Uses the same SQLite-in-memory + real-model approach
/// as PlatformSecurityRegressionTests (via a billing-enabled context subclass,
/// because the Integration Owner wires ConfigureBillingModel at merge).
/// </summary>
public class PlatformBillingTests
{
    private static readonly DateTime PeriodStart = new(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly BillingPeriod July = Parse("2026-07");
    private static readonly DateTime InJuly = new(2026, 7, 10, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime InJune = new(2026, 6, 20, 12, 0, 0, DateTimeKind.Utc);

    // ---------------------------------------------------------------- period

    [Fact]
    public void Period_parses_YYYY_MM_into_a_utc_calendar_month()
    {
        Assert.True(BillingPeriod.TryParse("2026-07", out var period));
        Assert.Equal(PeriodStart, period.StartUtc);
        Assert.Equal(PeriodStart.AddMonths(1), period.EndUtc);
        Assert.Equal("2026-07", period.Key);
        Assert.False(BillingPeriod.TryParse("2026-13", out _));
        Assert.False(BillingPeriod.TryParse("garbage", out _));
        Assert.False(BillingPeriod.TryParse(null, out _));
    }

    // ------------------------------------------------------- meter aggregation

    [Fact]
    public async Task Usage_aggregates_documents_tokens_and_seats_from_the_source_ledgers()
    {
        using var db = new BillingTestDb();
        var (tenantId, buId) = await SeedTenantAsync(db);
        long otherBu;
        await using (var seed = db.Context(null))
        {
            var rival = NewBusinessUnit("OTHER");
            seed.Add(rival);
            await seed.SaveChangesAsync();
            otherBu = rival.Id;

            // documents: 3 in period, 1 outside, 1 other tenant
            seed.AddRange(
                NewJob(buId, InJuly), NewJob(buId, InJuly.AddDays(1)), NewJob(buId, InJuly.AddDays(2)),
                NewJob(buId, InJune),
                NewJob(otherBu, InJuly));

            // tokens: settled external in period count; local / failed / out-of-period / other BU do not
            seed.AddRange(
                NewAiRequest(buId, AiProviderClass.External, AiCallStatuses.Succeeded, 40_000, 20_000, InJuly),
                NewAiRequest(buId, AiProviderClass.External, AiCallStatuses.Succeeded, 10_000, 5_000, InJuly.AddDays(3)),
                NewAiRequest(buId, AiProviderClass.Local, AiCallStatuses.Succeeded, 999_999, 1, InJuly),
                NewAiRequest(buId, AiProviderClass.External, AiCallStatuses.Failed, 7_000, 0, InJuly),
                NewAiRequest(buId, AiProviderClass.External, AiCallStatuses.Succeeded, 5_000, 5_000, InJune),
                NewAiRequest(otherBu, AiProviderClass.External, AiCallStatuses.Succeeded, 1_000, 1_000, InJuly));

            // seats: 2 active, 1 inactive, 1 active in the other BU
            seed.AddRange(
                NewUser(buId, active: true), NewUser(buId, active: true), NewUser(buId, active: false),
                NewUser(otherBu, active: true));
            await seed.SaveChangesAsync();
        }

        await using var context = db.Context(null);
        var service = Service(context);
        var usage = await service.GetUsageAsync(tenantId, July);

        Assert.Equal(buId, usage.BusinessUnitId);
        Assert.Equal(3m, Meter(usage, BillingMeterKeys.Documents).Quantity);
        Assert.Equal(75_000m, Meter(usage, BillingMeterKeys.AiTokensExternal).Quantity);
        Assert.Equal(2m, Meter(usage, BillingMeterKeys.Seats).Quantity);
        Assert.Contains("ExtractionJobs", Meter(usage, BillingMeterKeys.Documents).SourceNote);
    }

    // -------------------------------------------------- allowance/overage math

    [Fact]
    public async Task Compute_prices_overage_above_included_allowances_with_1k_token_unit_and_away_from_zero_rounding()
    {
        using var db = new BillingTestDb();
        var (tenantId, buId) = await SeedTenantAsync(db, withPlan: true);
        var rateCardId = await SeedRateCardAsync(db);
        await SeedStandardJulyUsageAsync(db, buId);

        await using var context = db.Context(null);
        var service = Service(context);
        var statement = await service.ComputeStatementAsync(tenantId, July, rateCardId);

        Assert.Equal(BillingStatementStatus.Draft, statement.Status);
        Assert.Equal("USD", statement.Currency);

        var docs = Line(statement, BillingMeterKeys.Documents);
        Assert.Equal(5m, docs.MeteredQuantity);
        Assert.Equal(2m, docs.IncludedQuantity);
        Assert.Equal(3m, docs.BillableQuantity);
        Assert.Equal(4.50m, docs.Amount); // 3 x 1.50

        // 175,000 metered - 50,000 included = 125,000 tokens -> 125 x 1K x 0.001
        // = 0.125 -> 0.13 (2dp AWAY-FROM-ZERO; banker's rounding would say 0.12).
        var tokens = Line(statement, BillingMeterKeys.AiTokensExternal);
        Assert.Equal(175_000m, tokens.MeteredQuantity);
        Assert.Equal(125_000m, tokens.BillableQuantity);
        Assert.Equal(0.13m, tokens.Amount);

        var seats = Line(statement, BillingMeterKeys.Seats);
        Assert.Equal(3m, seats.MeteredQuantity);
        Assert.Equal(2m, seats.BillableQuantity);
        Assert.Equal(60.00m, seats.Amount); // 2 x 30

        // The seeded plan has no MonthlyPriceUsd configured: base charge is an
        // honest zero line, not a fabricated price.
        var baseLine = Line(statement, BillingMeterKeys.BaseSubscription);
        Assert.Equal(0m, baseLine.Amount);
        Assert.Contains("no monthly price", baseLine.SourceNote);

        Assert.Equal(64.63m, statement.TotalAmount);
    }

    [Fact]
    public async Task Compute_never_bills_negative_overage_when_usage_is_below_the_allowance()
    {
        using var db = new BillingTestDb();
        var (tenantId, buId) = await SeedTenantAsync(db);
        var rateCardId = await SeedRateCardAsync(db);
        await using (var seed = db.Context(null))
        {
            seed.Add(NewJob(buId, InJuly)); // 1 document, allowance is 2
            await seed.SaveChangesAsync();
        }

        await using var context = db.Context(null);
        var statement = await Service(context).ComputeStatementAsync(tenantId, July, rateCardId);

        var docs = Line(statement, BillingMeterKeys.Documents);
        Assert.Equal(1m, docs.MeteredQuantity);
        Assert.Equal(0m, docs.BillableQuantity); // max(0, 1 - 2)
        Assert.Equal(0m, docs.Amount);
        Assert.Equal(0m, statement.TotalAmount);
    }

    // ------------------------------------------------------------- idempotency

    [Fact]
    public async Task Compute_twice_yields_one_statement_with_a_stable_total()
    {
        using var db = new BillingTestDb();
        var (tenantId, buId) = await SeedTenantAsync(db, withPlan: true);
        var rateCardId = await SeedRateCardAsync(db);
        await SeedStandardJulyUsageAsync(db, buId);

        long firstId;
        decimal firstTotal;
        await using (var context = db.Context(null))
        {
            var first = await Service(context).ComputeStatementAsync(tenantId, July, rateCardId);
            firstId = first.Id;
            firstTotal = first.TotalAmount;
        }

        await using (var context = db.Context(null))
        {
            var second = await Service(context).ComputeStatementAsync(tenantId, July, rateCardId);
            Assert.Equal(firstId, second.Id);
            Assert.Equal(firstTotal, second.TotalAmount);
        }

        await using var verification = db.Context(null);
        Assert.Equal(1, await verification.Set<BillingStatement>().CountAsync());
        Assert.Equal(4, await verification.Set<BillingStatementLine>().CountAsync()); // base + 3 meters, not doubled
    }

    // ------------------------------------------------ duplicate-charge guards

    [Fact]
    public async Task Duplicate_statement_for_the_same_tenant_period_is_rejected_by_the_unique_index()
    {
        using var db = new BillingTestDb();
        var (tenantId, _) = await SeedTenantAsync(db);
        var rateCardId = await SeedRateCardAsync(db);

        await using var context = db.Context(null);
        context.Add(NewStatement(tenantId, rateCardId));
        await context.SaveChangesAsync();

        context.Add(NewStatement(tenantId, rateCardId));
        var exception = await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
        Assert.True(BillingStatementService.IsUniqueViolation(exception));
    }

    [Fact]
    public void Unique_violation_detection_recognises_postgres_and_sqlite_shapes()
    {
        Assert.True(BillingStatementService.IsUniqueViolation(new DbUpdateException("boom",
            new InvalidOperationException("23505: duplicate key value violates unique constraint \"UX_BillingStatements_Tenant_PeriodStart\""))));
        Assert.True(BillingStatementService.IsUniqueViolation(new DbUpdateException("boom",
            new InvalidOperationException("SQLite Error 19: 'UNIQUE constraint failed: BillingStatements.TenantId, BillingStatements.PeriodStartUtc'."))));
        Assert.False(BillingStatementService.IsUniqueViolation(new DbUpdateException("boom",
            new InvalidOperationException("FOREIGN KEY constraint failed"))));
    }

    [Fact]
    public async Task Compute_folds_into_the_existing_statement_when_a_rival_row_lands_mid_compute()
    {
        using var db = new BillingTestDb();
        var (tenantId, buId) = await SeedTenantAsync(db);
        var rateCardId = await SeedRateCardAsync(db);
        await SeedStandardJulyUsageAsync(db, buId);

        // Simulate the race: right after the service's first (pre-transaction)
        // existence check reads BillingStatements, a rival compute commits a Draft
        // for the same (tenant, period). The unique index makes a second INSERT
        // impossible; the service must fold into the rival row instead.
        db.RivalInsert.Arm(tenantId, rateCardId, PeriodStart);

        await using var context = db.Context(null);
        var statement = await Service(context).ComputeStatementAsync(tenantId, July, rateCardId);

        Assert.True(db.RivalInsert.Fired);
        await using var verification = db.Context(null);
        var rows = await verification.Set<BillingStatement>().ToListAsync();
        var row = Assert.Single(rows); // duplicate-charge protection held
        Assert.Equal(row.Id, statement.Id);
        Assert.Equal(64.63m, statement.TotalAmount); // recomputed from real usage, not the rival's placeholder
    }

    // --------------------------------------------------- finalize immutability

    [Fact]
    public async Task Finalize_locks_the_statement_and_further_computes_return_it_unchanged()
    {
        using var db = new BillingTestDb();
        var (tenantId, buId) = await SeedTenantAsync(db);
        var rateCardId = await SeedRateCardAsync(db);
        await SeedStandardJulyUsageAsync(db, buId);

        long statementId;
        decimal finalTotal;
        await using (var context = db.Context(null))
        {
            var service = Service(context);
            var draft = await service.ComputeStatementAsync(tenantId, July, rateCardId);
            var finalized = await service.FinalizeAsync(draft.Id, "billing@nexora.test");
            statementId = finalized.Id;
            finalTotal = finalized.TotalAmount;
            Assert.Equal(BillingStatementStatus.Final, finalized.Status);
            Assert.NotNull(finalized.FinalizedAtUtc);
            Assert.Equal("billing@nexora.test", finalized.FinalizedBy);

            // Finalize is idempotent.
            var again = await service.FinalizeAsync(draft.Id, "someone-else@nexora.test");
            Assert.Equal("billing@nexora.test", again.FinalizedBy);
        }

        // Usage changes AFTER finalization must not alter the Final statement.
        await using (var seed = db.Context(null))
        {
            seed.AddRange(NewJob(buId, InJuly.AddDays(5)), NewJob(buId, InJuly.AddDays(6)));
            await seed.SaveChangesAsync();
        }

        await using (var context = db.Context(null))
        {
            var recompute = await Service(context).ComputeStatementAsync(tenantId, July, rateCardId);
            Assert.Equal(statementId, recompute.Id);
            Assert.Equal(BillingStatementStatus.Final, recompute.Status);
            Assert.Equal(finalTotal, recompute.TotalAmount); // unchanged despite new usage
        }

        await using var verification = db.Context(null);
        Assert.Equal(1, await verification.Set<BillingStatement>().CountAsync());
    }

    // ----------------------------------------------- rate-card immutability

    [Fact]
    public async Task RateCard_update_is_rejected_with_409_once_a_final_statement_references_it()
    {
        using var db = new BillingTestDb();
        var (tenantId, buId) = await SeedTenantAsync(db);
        var rateCardId = await SeedRateCardAsync(db);
        await SeedStandardJulyUsageAsync(db, buId);

        var update = new UpdateRateCardRequest("USD", PeriodStart.AddYears(-1), null, true,
            new[] { new RateCardLineRequest(BillingMeterKeys.Documents, 0m, 9.99m, "document", null) });

        // While only a Draft references the card, editing is allowed.
        await using (var context = db.Context(null))
        {
            var service = Service(context);
            await service.ComputeStatementAsync(tenantId, July, rateCardId);
            var controller = Controller(context, service);
            var allowed = await controller.UpdateRateCard(rateCardId, update, CancellationToken.None);
            Assert.IsType<OkObjectResult>(allowed.Result);
        }

        await using (var context = db.Context(null))
        {
            var service = Service(context);
            var draft = await context.Set<BillingStatement>().SingleAsync();
            await service.FinalizeAsync(draft.Id, "billing@nexora.test");

            var controller = Controller(context, service);
            var rejected = await controller.UpdateRateCard(rateCardId, update, CancellationToken.None);
            Assert.IsType<ConflictObjectResult>(rejected.Result);
        }
    }

    // ----------------------------------------------------------- cost / margin

    [Fact]
    public async Task Cost_report_withholds_totals_when_any_settled_request_is_unpriced()
    {
        using var db = new BillingTestDb();
        var (tenantId, buId) = await SeedTenantAsync(db);
        await using (var seed = db.Context(null))
        {
            seed.AddRange(
                NewAiRequest(buId, AiProviderClass.External, AiCallStatuses.Succeeded, 1_000, 500, InJuly,
                    estimatedCost: 1.25m, costStatus: AiCostStatuses.Priced),
                NewAiRequest(buId, AiProviderClass.Local, AiCallStatuses.Succeeded, 2_000, 100, InJuly,
                    estimatedCost: null, costStatus: AiCostStatuses.RateUnavailable));
            await seed.SaveChangesAsync();
        }

        await using var context = db.Context(null);
        var report = await Service(context).GetCostAsync(tenantId, July);

        Assert.Equal(2, report.SettledAiRequestCount);
        Assert.Equal(1, report.UnpricedAiRequestCount);
        Assert.Equal(1.25m, report.PricedAiCostSubtotal);
        Assert.Null(report.AiCostTotal); // honest null, never fabricated
        Assert.Null(report.GrossMargin);
        Assert.Contains("withheld", report.Note);
    }

    [Fact]
    public async Task Cost_report_computes_margin_when_all_costs_are_priced_and_a_statement_exists()
    {
        using var db = new BillingTestDb();
        var (tenantId, buId) = await SeedTenantAsync(db);
        var rateCardId = await SeedRateCardAsync(db);
        await SeedStandardJulyUsageAsync(db, buId, aiCostPerRequest: 0.50m);

        await using var context = db.Context(null);
        var service = Service(context);
        await service.ComputeStatementAsync(tenantId, July, rateCardId);
        var report = await service.GetCostAsync(tenantId, July);

        Assert.Equal(0, report.UnpricedAiRequestCount);
        Assert.Equal(1.00m, report.AiCostTotal); // 2 settled external requests x 0.50
        Assert.Equal(64.63m, report.StatementTotal);
        Assert.Equal(63.63m, report.GrossMargin);
    }

    // ------------------------------------------- P0-B1 billable job statuses

    [Fact]
    public async Task Documents_meter_counts_only_billable_job_statuses_and_reports_the_metering_scope()
    {
        using var db = new BillingTestDb();
        var (tenantId, buId) = await SeedTenantAsync(db);
        await using (var seed = db.Context(null))
        {
            // One job in EVERY status the state machine knows.
            var day = 0;
            foreach (var status in Enum.GetValues<ExtractionStatus>())
                seed.Add(NewJob(buId, InJuly.AddDays(day++), status));
            await seed.SaveChangesAsync();
        }

        await using var context = db.Context(null);
        var usage = await Service(context).GetUsageAsync(tenantId, July);

        // Billable: Pending, Leased, Extracting, Persisting, Succeeded.
        // Excluded: Duplicate (already billed once), Failed, DeadLetter (never delivered).
        Assert.Equal(5m, Meter(usage, BillingMeterKeys.Documents).Quantity);
        Assert.Contains("excludes Duplicate/Failed/DeadLetter", Meter(usage, BillingMeterKeys.Documents).SourceNote);

        // P2-B7: the readout states the v1 metering scope explicitly.
        Assert.Equal("primary-business-unit", usage.MeteringScope);

        // The policy itself pins the exact exclusion set.
        Assert.Equal(
            new[] { ExtractionStatus.Duplicate, ExtractionStatus.Failed, ExtractionStatus.DeadLetter },
            BillableDocumentPolicy.NonBillableStatuses);
        Assert.True(BillableDocumentPolicy.IsBillable(ExtractionStatus.Pending));
        Assert.False(BillableDocumentPolicy.IsBillable(ExtractionStatus.Duplicate));
    }

    // --------------------------------------------- P0-B2 reproducible seats

    [Fact]
    public async Task Seats_meter_is_reproducible_when_users_churn_after_the_period()
    {
        using var db = new BillingTestDb();
        var (tenantId, buId) = await SeedTenantAsync(db);
        var afterPeriodEnd = new DateTime(2026, 8, 5, 9, 0, 0, DateTimeKind.Utc);

        long deactivateLaterId;
        await using (var seed = db.Context(null))
        {
            var deactivateLater = NewUser(buId, active: true, createdOn: InJune);
            seed.AddRange(
                deactivateLater,                                                     // active all period -> counts
                NewUser(buId, active: true, createdOn: InJuly),                      // created mid-period -> counts
                NewUser(buId, active: false, deactivatedAtUtc: afterPeriodEnd),      // deactivated AFTER period end -> still counts for July
                NewUser(buId, active: false,
                    deactivatedAtUtc: new DateTime(2026, 7, 15, 0, 0, 0, DateTimeKind.Utc)), // deactivated mid-period -> no seat
                NewUser(buId, active: false),                                        // legacy inactive, null DeactivatedAtUtc -> deactivated
                NewUser(buId, active: true, createdOn: afterPeriodEnd));             // created after period end -> no July seat
            await seed.SaveChangesAsync();
            deactivateLaterId = deactivateLater.Id;
        }

        await using (var context = db.Context(null))
        {
            var usage = await Service(context).GetUsageAsync(tenantId, July);
            Assert.Equal(3m, Meter(usage, BillingMeterKeys.Seats).Quantity);
            Assert.Contains("DeactivatedAtUtc", Meter(usage, BillingMeterKeys.Seats).SourceNote);
        }

        // The core P0-B2 property: deactivating a user AFTER the period must not
        // change a recomputed reading for that period.
        await using (var churn = db.Context(null))
        {
            var user = await churn.Set<User>().IgnoreQueryFilters().SingleAsync(u => u.Id == deactivateLaterId);
            user.IsActive = false;
            user.DeactivatedAtUtc = afterPeriodEnd;
            await churn.SaveChangesAsync();
        }

        await using (var recompute = db.Context(null))
        {
            var usage = await Service(recompute).GetUsageAsync(tenantId, July);
            Assert.Equal(3m, Meter(usage, BillingMeterKeys.Seats).Quantity); // unchanged
        }
    }

    // ----------------------------------------------- P0-B3 USD-only currency

    [Fact]
    public async Task Non_usd_rate_cards_are_rejected_on_create_and_update_with_400()
    {
        using var db = new BillingTestDb();
        await SeedTenantAsync(db);
        var usdCardId = await SeedRateCardAsync(db);

        await using var context = db.Context(null);
        var controller = Controller(context, Service(context));

        var line = new RateCardLineRequest(BillingMeterKeys.Documents, 0m, 1m, "document", null);
        var created = await controller.CreateRateCard(
            new CreateRateCardRequest("eur-card", "EUR", PeriodStart, null, true, new[] { line }),
            CancellationToken.None);
        var badCreate = Assert.IsType<BadRequestObjectResult>(created.Result);
        Assert.Contains("USD-only", badCreate.Value!.ToString());

        var updated = await controller.UpdateRateCard(usdCardId,
            new UpdateRateCardRequest("EUR", PeriodStart, null, true, new[] { line }),
            CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(updated.Result);
    }

    [Fact]
    public async Task Compute_conflicts_with_409_when_the_rate_card_is_not_usd()
    {
        using var db = new BillingTestDb();
        var (tenantId, _) = await SeedTenantAsync(db);

        long eurCardId;
        await using (var seed = db.Context(null))
        {
            // Bypasses the API guard — simulates a card that predates the v1 rule.
            var eurCard = new RateCard
            {
                Code = $"legacy-eur-{Guid.NewGuid():N}"[..20],
                Currency = "EUR",
                EffectiveFromUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                IsActive = true,
                Lines = { new RateCardLine { MeterKey = BillingMeterKeys.Documents, IncludedQuantity = 0m, UnitPrice = 1m, Unit = "document" } }
            };
            seed.Add(eurCard);
            await seed.SaveChangesAsync();
            eurCardId = eurCard.Id;
        }

        await using var context = db.Context(null);
        var service = Service(context);
        var ex = await Assert.ThrowsAsync<BillingConflictException>(
            () => service.ComputeStatementAsync(tenantId, July, eurCardId));
        Assert.Contains("USD-only", ex.Message);

        var controller = Controller(context, service);
        var result = await controller.ComputeStatement(
            new ComputeStatementRequest(tenantId, "2026-07", eurCardId), CancellationToken.None);
        Assert.IsType<ConflictObjectResult>(result.Result);

        await using var verification = db.Context(null);
        Assert.Equal(0, await verification.Set<BillingStatement>().CountAsync()); // nothing half-written
    }

    // ------------------------------------------ P2-B8/A5 version-race handling

    [Fact]
    public async Task Compute_returns_the_current_row_when_a_rival_version_bump_races_the_save()
    {
        using var db = new BillingTestDb();
        var (tenantId, buId) = await SeedTenantAsync(db);
        var rateCardId = await SeedRateCardAsync(db);
        await SeedStandardJulyUsageAsync(db, buId);

        long draftId;
        await using (var context = db.Context(null))
        {
            draftId = (await Service(context).ComputeStatementAsync(tenantId, July, rateCardId)).Id;
        }

        // Fire the rival bump after the SECOND BillingStatements SELECT — the
        // in-transaction tracked load — so our UPDATE ... WHERE Version = old
        // matches zero rows and throws DbUpdateConcurrencyException.
        db.VersionBump.Arm(draftId, fireOnMatch: 2);

        await using (var context = db.Context(null))
        {
            var statement = await Service(context).ComputeStatementAsync(tenantId, July, rateCardId);
            Assert.True(db.VersionBump.Fired);
            Assert.Equal(draftId, statement.Id); // current row returned, no 500
        }

        await using var verification = db.Context(null);
        var row = Assert.Single(await verification.Set<BillingStatement>().ToListAsync());
        Assert.Equal(draftId, row.Id);
    }

    [Fact]
    public async Task Finalize_retries_past_a_concurrent_version_bump_and_finalizes_the_current_row()
    {
        using var db = new BillingTestDb();
        var (tenantId, buId) = await SeedTenantAsync(db);
        var rateCardId = await SeedRateCardAsync(db);
        await SeedStandardJulyUsageAsync(db, buId);

        long draftId;
        await using (var context = db.Context(null))
        {
            draftId = (await Service(context).ComputeStatementAsync(tenantId, July, rateCardId)).Id;
        }

        // Rival bump lands right after finalize's first load (outside the
        // transaction, so it commits immediately) -> first SaveChanges hits
        // DbUpdateConcurrencyException -> the service reloads and finalizes the
        // CURRENT row instead of surfacing a 500.
        db.VersionBump.Arm(draftId, fireOnMatch: 1);

        await using (var context = db.Context(null))
        {
            var finalized = await Service(context).FinalizeAsync(draftId, "billing@nexora.test");
            Assert.True(db.VersionBump.Fired);
            Assert.Equal(BillingStatementStatus.Final, finalized.Status);
            Assert.Equal("billing@nexora.test", finalized.FinalizedBy);
        }

        await using var verification = db.Context(null);
        var row = await verification.Set<BillingStatement>().SingleAsync(s => s.Id == draftId);
        Assert.Equal(BillingStatementStatus.Final, row.Status);
        Assert.Equal(3, row.Version); // 1 (draft) + rival bump + finalize
    }

    // ------------------------------------------- Sec3 audit inside finalize tx

    [Fact]
    public async Task Finalize_audit_failure_rolls_back_the_finalize_and_success_writes_the_row_atomically()
    {
        using var db = new BillingTestDb();
        var (tenantId, buId) = await SeedTenantAsync(db);
        var rateCardId = await SeedRateCardAsync(db);
        await SeedStandardJulyUsageAsync(db, buId);

        long draftId;
        await using (var context = db.Context(null))
        {
            draftId = (await Service(context).ComputeStatementAsync(tenantId, July, rateCardId)).Id;
        }

        // Audit write fails -> the whole finalize must roll back (audit runs
        // INSIDE the finalize transaction, not after commit).
        await using (var context = db.Context(null))
        {
            var controller = Controller(context, Service(context), new ExplodingAuditService());
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => controller.FinalizeStatement(draftId, CancellationToken.None));
        }

        await using (var verification = db.Context(null))
        {
            var row = await verification.Set<BillingStatement>().SingleAsync(s => s.Id == draftId);
            Assert.Equal(BillingStatementStatus.Draft, row.Status); // rolled back with the audit
            Assert.Null(row.FinalizedAtUtc);
        }

        // Healthy audit: Draft -> Final and the audit row commit together.
        await using (var context = db.Context(null))
        {
            var controller = Controller(context, Service(context));
            var ok = await controller.FinalizeStatement(draftId, CancellationToken.None);
            Assert.IsType<OkObjectResult>(ok.Result);
        }

        await using (var final = db.Context(null))
        {
            var row = await final.Set<BillingStatement>().SingleAsync(s => s.Id == draftId);
            Assert.Equal(BillingStatementStatus.Final, row.Status);
            var audit = await final.Set<PlatformAuditLog>()
                .Where(a => a.Action == "billing.statement.finalize")
                .ToListAsync();
            Assert.Single(audit);
        }
    }

    // ----------------------------------------- P2-B6 unreconciled external spend

    [Fact]
    public async Task Cost_report_surfaces_unreconciled_external_requests_so_margin_is_not_overstated()
    {
        using var db = new BillingTestDb();
        var (tenantId, buId) = await SeedTenantAsync(db);
        await using (var seed = db.Context(null))
        {
            seed.AddRange(
                NewAiRequest(buId, AiProviderClass.External, AiCallStatuses.Succeeded, 1_000, 500, InJuly,
                    estimatedCost: 2.00m, costStatus: AiCostStatuses.Priced),
                NewAiRequest(buId, AiProviderClass.External, AiCallStatuses.Unknown, 8_000, 2_000, InJuly),
                NewAiRequest(buId, AiProviderClass.External, AiCallStatuses.Failed, 4_000, 0, InJuly.AddDays(1)),
                NewAiRequest(buId, AiProviderClass.Local, AiCallStatuses.Failed, 999, 0, InJuly), // local: no provider spend
                NewAiRequest(buId, AiProviderClass.External, AiCallStatuses.Failed, 5_000, 0, InJune)); // out of period
            await seed.SaveChangesAsync();
        }

        await using var context = db.Context(null);
        var report = await Service(context).GetCostAsync(tenantId, July);

        Assert.Equal(2, report.UnreconciledExternalRequestCount);   // Unknown + Failed external, July only
        Assert.Equal(14_000L, report.UnreconciledExternalTokens);   // 8k+2k + 4k+0
        Assert.Equal(2.00m, report.AiCostTotal);                    // settled cost still honest and complete
        Assert.Contains("unpriced provider spend", report.Note);
        Assert.Equal("primary-business-unit", report.MeteringScope);
    }

    // ------------------------------------------------ P2-B11 direct plan price

    [Fact]
    public async Task Compute_prices_the_base_subscription_from_the_plan_monthly_price_property()
    {
        using var db = new BillingTestDb();
        var (tenantId, _) = await SeedTenantAsync(db, monthlyPriceUsd: 199.00m);
        var rateCardId = await SeedRateCardAsync(db);

        await using var context = db.Context(null);
        var statement = await Service(context).ComputeStatementAsync(tenantId, July, rateCardId);

        var baseLine = Line(statement, BillingMeterKeys.BaseSubscription);
        Assert.Equal(199.00m, baseLine.UnitPrice);
        Assert.Equal(199.00m, baseLine.Amount);
        Assert.Contains("monthly subscription price", baseLine.SourceNote);
    }

    // ------------------------- usage-based pages / OCR / storage meters (v2)

    [Fact]
    public async Task Pages_meter_sums_run_evidence_for_billable_jobs_only_and_never_double_counts_retries()
    {
        using var db = new BillingTestDb();
        var (tenantId, buId) = await SeedTenantAsync(db);
        long otherBu;
        await using (var seed = db.Context(null))
        {
            var rival = NewBusinessUnit("OTHER");
            seed.Add(rival);
            await seed.SaveChangesAsync();
            otherBu = rival.Id;

            // One job in EVERY status, each with run evidence of 10 pages (4 OCR):
            // only the 5 billable statuses may contribute (P0-B1 alignment).
            foreach (var status in Enum.GetValues<ExtractionStatus>())
                await SeedJobWithRunsAsync(seed, buId, InJuly, status, pages: 10, ocrPages: 4);

            // Retried billable job: attempts 1 AND 2 persisted a run for the SAME
            // 7-page document. The meter takes MAX(PageCount) per job -> 7, not 14.
            await SeedJobWithRunsAsync(seed, buId, InJuly.AddDays(3), ExtractionStatus.Succeeded,
                pages: 7, ocrPages: 3, attempts: 2);

            // Billable job with NO run evidence: contributes 0 pages; the
            // SourceNote must expose the coverage shortfall instead of hiding it.
            seed.Add(NewJob(buId, InJuly.AddDays(4)));

            // Out-of-period and foreign-BU run evidence never leaks in.
            await SeedJobWithRunsAsync(seed, buId, InJune, ExtractionStatus.Succeeded, pages: 100, ocrPages: 100);
            await SeedJobWithRunsAsync(seed, otherBu, InJuly, ExtractionStatus.Succeeded, pages: 100, ocrPages: 100);
            await seed.SaveChangesAsync();
        }

        await using var context = db.Context(null);
        var usage = await Service(context).GetUsageAsync(tenantId, July);

        var pages = Meter(usage, BillingMeterKeys.PagesProcessed);
        Assert.Equal(57m, pages.Quantity); // 5 billable x 10 + 7 (retry, deduped) + 0 (no evidence)
        Assert.Contains("ExtractionRuns", pages.SourceNote);
        Assert.Contains("MAX(PageCount)", pages.SourceNote);
        Assert.Contains("6 of 7", pages.SourceNote); // 7 billable jobs, 6 with run evidence
        Assert.Contains("2026-07", pages.SourceNote);
        Assert.Contains($"BU {buId}", pages.SourceNote);

        // OCR pages are a separately metered subset with the same dedupe rules.
        var ocr = Meter(usage, BillingMeterKeys.PagesOcr);
        Assert.Equal(23m, ocr.Quantity); // 5 x 4 + 3
        Assert.Contains("OcrPageCount", ocr.SourceNote);
        Assert.Contains($"BU {buId}", ocr.SourceNote);
        Assert.True(ocr.Quantity <= pages.Quantity);

        // Platform plane: the run-evidence join must read across tenants even when
        // the request context is scoped to a DIFFERENT business unit (the tenant
        // query filters are deliberately ignored, as for the documents meter).
        await using var scoped = db.Context(otherBu);
        var crossTenant = await Service(scoped).GetUsageAsync(tenantId, July);
        Assert.Equal(57m, Meter(crossTenant, BillingMeterKeys.PagesProcessed).Quantity);
        Assert.Equal(23m, Meter(crossTenant, BillingMeterKeys.PagesOcr).Quantity);
    }

    [Fact]
    public async Task Page_meters_are_flagged_not_billing_ready_and_name_their_instrumentation_gaps()
    {
        using var db = new BillingTestDb();
        var (tenantId, buId) = await SeedTenantAsync(db);
        await using (var seed = db.Context(null))
        {
            await SeedJobWithRunsAsync(seed, buId, InJuly, ExtractionStatus.Succeeded, pages: 6, ocrPages: 2);
        }

        await using var context = db.Context(null);
        var usage = await Service(context).GetUsageAsync(tenantId, July);

        // The signal audit found page counts only partially instrumented, so the
        // page meters must announce that on the wire — quantity is a FLOOR.
        foreach (var meterKey in new[] { BillingMeterKeys.PagesProcessed, BillingMeterKeys.PagesOcr })
        {
            var meter = Meter(usage, meterKey);
            Assert.Equal(MeterSignalCoverage.Incomplete, meter.SignalCoverage);
            Assert.False(meter.BillingReady);
            Assert.NotNull(meter.CoverageNote);
            Assert.Contains("NOT BILLING-READY", meter.CoverageNote);
            Assert.Contains("Text-layer PDFs", meter.CoverageNote);
            Assert.Contains("capped at 10 pages", meter.CoverageNote);
            Assert.Contains("supplier-quote", meter.CoverageNote);
            Assert.Contains("FLOOR", meter.CoverageNote);
        }

        // Meters whose signal IS complete stay billing-ready and unqualified.
        foreach (var meterKey in new[]
                 { BillingMeterKeys.Documents, BillingMeterKeys.AiTokensExternal, BillingMeterKeys.Seats })
        {
            var meter = Meter(usage, meterKey);
            Assert.Equal(MeterSignalCoverage.Complete, meter.SignalCoverage);
            Assert.True(meter.BillingReady);
            Assert.Null(meter.CoverageNote);
        }

        // Storage measures exactly what it claims (ByteSize is always recorded),
        // but still names the doors that bypass the evidence ledger entirely.
        var storage = Meter(usage, BillingMeterKeys.StorageGb);
        Assert.Equal(MeterSignalCoverage.Complete, storage.SignalCoverage);
        Assert.True(storage.BillingReady);
        Assert.Contains("bypass the evidence ledger", storage.CoverageNote);
    }

    [Fact]
    public async Task Pricing_a_not_billing_ready_page_meter_still_computes_but_the_line_carries_the_caveat()
    {
        using var db = new BillingTestDb();
        var (tenantId, buId) = await SeedTenantAsync(db);
        long cardId;
        await using (var seed = db.Context(null))
        {
            var card = new RateCard
            {
                Code = $"pages-{Guid.NewGuid():N}"[..20],
                Currency = "USD",
                EffectiveFromUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                IsActive = true,
                Lines = { new RateCardLine { MeterKey = BillingMeterKeys.PagesProcessed, IncludedQuantity = 0m, UnitPrice = 0.20m, Unit = "page" } }
            };
            seed.Add(card);
            await SeedJobWithRunsAsync(seed, buId, InJuly, ExtractionStatus.Succeeded, pages: 9, ocrPages: 0);
            await seed.SaveChangesAsync();
            cardId = card.Id;
        }

        await using var context = db.Context(null);
        var statement = await Service(context).ComputeStatementAsync(tenantId, July, cardId);

        // The machinery works: a card WITH a pages line prices it normally...
        var line = Line(statement, BillingMeterKeys.PagesProcessed);
        Assert.Equal(9m, line.MeteredQuantity);
        Assert.Equal(1.80m, line.Amount); // 9 x 0.20
        Assert.Equal(1.80m, statement.TotalAmount);

        // ...but the priced line must never look more trustworthy than its
        // signal: provenance AND the coverage caveat travel together.
        Assert.Contains("ExtractionRuns", line.SourceNote);
        Assert.Contains("2026-07", line.SourceNote);
        Assert.Contains($"BU {buId}", line.SourceNote);
        Assert.Contains("NOT BILLING-READY", line.SourceNote);
    }

    [Fact]
    public async Task Storage_meter_snapshots_retained_bytes_at_period_end_and_prices_per_gib_away_from_zero()
    {
        using var db = new BillingTestDb();
        var (tenantId, buId) = await SeedTenantAsync(db);
        long cardId;
        await using (var seed = db.Context(null))
        {
            var card = new RateCard
            {
                Code = $"storage-{Guid.NewGuid():N}"[..20],
                Currency = "USD",
                EffectiveFromUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                IsActive = true,
                Lines = { new RateCardLine { MeterKey = BillingMeterKeys.StorageGb, IncludedQuantity = 0m, UnitPrice = 0.10m, Unit = "GB" } }
            };
            seed.Add(card);

            // 1.25 GiB received in June: retained through July (append-only ledger).
            await SeedSourceDocumentAsync(seed, buId, byteSize: 1_342_177_280L, createdOn: InJune);
            // Received AFTER the July period end: not part of the July snapshot.
            await SeedSourceDocumentAsync(seed, buId, byteSize: 999_999_999L,
                createdOn: new DateTime(2026, 8, 2, 8, 0, 0, DateTimeKind.Utc));
            await seed.SaveChangesAsync();
            cardId = card.Id;
        }

        await using var context = db.Context(null);
        var service = Service(context);

        var usage = await service.GetUsageAsync(tenantId, July);
        var storage = Meter(usage, BillingMeterKeys.StorageGb);
        Assert.Equal(1_342_177_280m, storage.Quantity); // metered in raw bytes
        Assert.Equal("byte", storage.Unit);
        Assert.Contains("SourceDocuments.ByteSize", storage.SourceNote);
        Assert.Contains("2026-07", storage.SourceNote);
        Assert.Contains($"BU {buId}", storage.SourceNote);

        // Divisor contract: GiB = 1024^3 bytes, priced like the 1K-token pattern.
        Assert.Equal(1_073_741_824m, BillingMeterKeys.BytesPerGigabyte);
        Assert.Equal(BillingMeterKeys.BytesPerGigabyte, BillingStatementService.PricedUnitDivisor(BillingMeterKeys.StorageGb));
        Assert.Equal(1_000m, BillingStatementService.PricedUnitDivisor(BillingMeterKeys.AiTokensExternal));
        Assert.Equal(1m, BillingStatementService.PricedUnitDivisor(BillingMeterKeys.PagesProcessed));

        var statement = await service.ComputeStatementAsync(tenantId, July, cardId);
        var line = Line(statement, BillingMeterKeys.StorageGb);
        Assert.Equal(1_342_177_280m, line.MeteredQuantity);
        Assert.Equal(1_342_177_280m, line.BillableQuantity);
        // 1.25 GiB x 0.10/GiB = 0.125 -> 0.13 (2dp AWAY-FROM-ZERO, not banker's 0.12).
        Assert.Equal(0.13m, line.Amount);
        Assert.Equal(0.13m, statement.TotalAmount);
    }

    [Fact]
    public async Task Rate_card_without_page_or_storage_lines_charges_nothing_for_those_meters()
    {
        using var db = new BillingTestDb();
        var (tenantId, buId) = await SeedTenantAsync(db);
        var rateCardId = await SeedRateCardAsync(db); // documents/tokens/seats lines only
        await using (var seed = db.Context(null))
        {
            await SeedJobWithRunsAsync(seed, buId, InJuly, ExtractionStatus.Succeeded,
                pages: 40, ocrPages: 10, byteSize: 5_000_000L);
        }

        await using var context = db.Context(null);
        var service = Service(context);

        // Usage still reports the page/storage meters (with source notes)...
        var usage = await service.GetUsageAsync(tenantId, July);
        Assert.Equal(40m, Meter(usage, BillingMeterKeys.PagesProcessed).Quantity);
        Assert.Equal(10m, Meter(usage, BillingMeterKeys.PagesOcr).Quantity);
        Assert.Equal(5_000_000m, Meter(usage, BillingMeterKeys.StorageGb).Quantity);

        // ...but meters are ADDITIVE: a card with no line for a meter does not
        // charge it — no page/storage lines and no page/storage money.
        var statement = await service.ComputeStatementAsync(tenantId, July, rateCardId);
        Assert.DoesNotContain(statement.Lines, l => l.MeterKey == BillingMeterKeys.PagesProcessed);
        Assert.DoesNotContain(statement.Lines, l => l.MeterKey == BillingMeterKeys.PagesOcr);
        Assert.DoesNotContain(statement.Lines, l => l.MeterKey == BillingMeterKeys.StorageGb);
        Assert.Equal(0m, statement.TotalAmount); // 1 doc within the 2-doc allowance, nothing else
    }

    [Fact]
    public async Task Full_statement_with_all_meters_priced_sums_correctly_and_every_line_names_its_source()
    {
        using var db = new BillingTestDb();
        var (tenantId, buId) = await SeedTenantAsync(db, monthlyPriceUsd: 100.00m);
        long cardId;
        await using (var seed = db.Context(null))
        {
            var card = new RateCard
            {
                Code = $"allmeters-{Guid.NewGuid():N}"[..20],
                Currency = "USD",
                EffectiveFromUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                IsActive = true,
                Lines =
                {
                    new RateCardLine { MeterKey = BillingMeterKeys.Documents, IncludedQuantity = 0m, UnitPrice = 1.50m, Unit = "document" },
                    new RateCardLine { MeterKey = BillingMeterKeys.PagesProcessed, IncludedQuantity = 0m, UnitPrice = 0.10m, Unit = "page" },
                    new RateCardLine { MeterKey = BillingMeterKeys.PagesOcr, IncludedQuantity = 0m, UnitPrice = 0.05m, Unit = "page" },
                    new RateCardLine { MeterKey = BillingMeterKeys.AiTokensExternal, IncludedQuantity = 0m, UnitPrice = 0.001m, Unit = "1K tokens" },
                    new RateCardLine { MeterKey = BillingMeterKeys.Seats, IncludedQuantity = 1m, UnitPrice = 30m, Unit = "seat" },
                    new RateCardLine { MeterKey = BillingMeterKeys.StorageGb, IncludedQuantity = 0m, UnitPrice = 0.25m, Unit = "GB" }
                }
            };
            seed.Add(card);

            // 2 billable jobs: 3 native pages + 2 fully-OCR pages, 0.5 GiB each.
            await SeedJobWithRunsAsync(seed, buId, InJuly, ExtractionStatus.Succeeded,
                pages: 3, ocrPages: 0, byteSize: 536_870_912L);
            await SeedJobWithRunsAsync(seed, buId, InJuly.AddDays(1), ExtractionStatus.Succeeded,
                pages: 2, ocrPages: 2, byteSize: 536_870_912L);

            seed.Add(NewAiRequest(buId, AiProviderClass.External, AiCallStatuses.Succeeded, 8_000, 2_000, InJuly));
            seed.AddRange(NewUser(buId, active: true), NewUser(buId, active: true));
            await seed.SaveChangesAsync();
            cardId = card.Id;
        }

        await using var context = db.Context(null);
        var statement = await Service(context).ComputeStatementAsync(tenantId, July, cardId);

        Assert.Equal(100.00m, Line(statement, BillingMeterKeys.BaseSubscription).Amount);
        Assert.Equal(3.00m, Line(statement, BillingMeterKeys.Documents).Amount);      // 2 x 1.50
        Assert.Equal(0.50m, Line(statement, BillingMeterKeys.PagesProcessed).Amount); // 5 x 0.10
        Assert.Equal(0.10m, Line(statement, BillingMeterKeys.PagesOcr).Amount);       // 2 x 0.05
        Assert.Equal(0.01m, Line(statement, BillingMeterKeys.AiTokensExternal).Amount); // 10K/1K x 0.001
        Assert.Equal(30.00m, Line(statement, BillingMeterKeys.Seats).Amount);         // (2-1) x 30
        Assert.Equal(0.25m, Line(statement, BillingMeterKeys.StorageGb).Amount);      // 1 GiB x 0.25
        Assert.Equal(133.86m, statement.TotalAmount);
        Assert.Equal(7, statement.Lines.Count);

        // Traceability: every metered line's SourceNote names its source ledger,
        // the period and the BU, consistent with the pre-existing meters.
        foreach (var (meterKey, ledger) in new[]
                 {
                     (BillingMeterKeys.Documents, "ExtractionJobs"),
                     (BillingMeterKeys.PagesProcessed, "ExtractionRuns"),
                     (BillingMeterKeys.PagesOcr, "ExtractionRuns"),
                     (BillingMeterKeys.AiTokensExternal, "AiRequests"),
                     (BillingMeterKeys.Seats, "Users"),
                     (BillingMeterKeys.StorageGb, "SourceDocuments")
                 })
        {
            var sourceNote = Line(statement, meterKey).SourceNote;
            Assert.NotNull(sourceNote);
            Assert.Contains(ledger, sourceNote);
            Assert.Contains($"BU {buId}", sourceNote);
            if (meterKey != BillingMeterKeys.Seats) // seats note pins the period-end date instead
                Assert.Contains("2026-07", sourceNote);
        }

        // Only the page lines carry the not-billing-ready caveat.
        Assert.Contains("NOT BILLING-READY", Line(statement, BillingMeterKeys.PagesProcessed).SourceNote);
        Assert.Contains("NOT BILLING-READY", Line(statement, BillingMeterKeys.PagesOcr).SourceNote);
        Assert.DoesNotContain("NOT BILLING-READY", Line(statement, BillingMeterKeys.Documents).SourceNote);
    }

    [Fact]
    public async Task Usage_readout_reports_all_six_meters_even_for_a_tenant_with_no_business_unit()
    {
        using var db = new BillingTestDb();
        long tenantId;
        await using (var seed = db.Context(null))
        {
            var tenant = new Tenant
            {
                Name = "Unmapped Tenant",
                Slug = $"unmapped-{Guid.NewGuid():N}"[..20],
                Status = TenantStatus.Active,
                PrimaryBusinessUnitId = null,
                CreatedBy = "test",
                CreatedOn = DateTime.UtcNow
            };
            seed.Add(tenant);
            await seed.SaveChangesAsync();
            tenantId = tenant.Id;
        }

        await using var context = db.Context(null);
        var service = Service(context);
        var usage = await service.GetUsageAsync(tenantId, July);

        var expected = new[]
        {
            BillingMeterKeys.Documents, BillingMeterKeys.PagesProcessed, BillingMeterKeys.PagesOcr,
            BillingMeterKeys.AiTokensExternal, BillingMeterKeys.Seats, BillingMeterKeys.StorageGb
        };
        Assert.Equal(expected.OrderBy(k => k), usage.Meters.Select(m => m.MeterKey).OrderBy(k => k));
        Assert.All(usage.Meters, m => Assert.Equal(0m, m.Quantity));
        Assert.All(usage.Meters, m => Assert.False(string.IsNullOrWhiteSpace(m.SourceNote)));

        // The readout ENDPOINT carries every meter (with its per-meter source
        // note) on the wire, not just the service result.
        var response = await Controller(context, service).GetUsage(tenantId, "2026-07", CancellationToken.None);
        var payload = Assert.IsType<TenantUsageReadout>(Assert.IsType<OkObjectResult>(response.Result).Value);
        Assert.Equal(expected.OrderBy(k => k), payload.Meters.Select(m => m.MeterKey).OrderBy(k => k));
        Assert.All(payload.Meters, m => Assert.False(string.IsNullOrWhiteSpace(m.SourceNote)));
    }

    // ------------------------------------------------------------ authorization

    [Fact]
    public void Every_billing_endpoint_requires_the_platform_billing_policy()
    {
        var controller = typeof(PlatformBillingController);

        var classPolicy = controller.GetCustomAttributes<AuthorizeAttribute>()
            .SingleOrDefault(a => a.Policy == PlatformPolicies.Billing);
        Assert.NotNull(classPolicy);

        var actions = controller
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => m.GetCustomAttributes().Any(a => a is Microsoft.AspNetCore.Mvc.Routing.HttpMethodAttribute))
            .ToList();

        Assert.True(actions.Count >= 8, "Expected the eight billing endpoints to be present.");
        foreach (var action in actions)
        {
            Assert.False(action.GetCustomAttributes<AllowAnonymousAttribute>().Any(),
                $"{action.Name} must not opt out of the Platform.Billing policy.");
            var overriding = action.GetCustomAttributes<AuthorizeAttribute>().ToList();
            Assert.True(overriding.Count == 0 || overriding.All(a => a.Policy == PlatformPolicies.Billing),
                $"{action.Name} must not weaken the class-level Platform.Billing policy.");
        }
    }

    // =================================================================== support

    private static BillingPeriod Parse(string key)
    {
        Assert.True(BillingPeriod.TryParse(key, out var period));
        return period;
    }

    private static MeterReading Meter(TenantUsageReadout usage, string key)
        => usage.Meters.Single(m => m.MeterKey == key);

    private static BillingStatementLine Line(BillingStatement statement, string key)
        => statement.Lines.Single(l => l.MeterKey == key);

    private static BillingStatementService Service(ErpRfqAutomationContext context)
        => new(context, NullLogger<BillingStatementService>.Instance);

    private static PlatformBillingController Controller(
        ErpRfqAutomationContext context, IBillingStatementService service,
        ERP_RFQ_Automation.Platform.Services.IPlatformAuditService? audit = null)
        => new(context, service,
            audit ?? new ERP_RFQ_Automation.Platform.Services.PlatformAuditService(
                context, NullLogger<ERP_RFQ_Automation.Platform.Services.PlatformAuditService>.Instance),
            NullLogger<PlatformBillingController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(
                    [
                        new System.Security.Claims.Claim("sub", "7"),
                        new System.Security.Claims.Claim("email", "billing@nexora.test")
                    ], "Platform"))
                }
            }
        };

    /// <summary>Sec3 test double: any audit write fails, as if the audit store were down.</summary>
    private sealed class ExplodingAuditService : ERP_RFQ_Automation.Platform.Services.IPlatformAuditService
    {
        public Task WriteAsync(
            System.Security.Claims.ClaimsPrincipal actor, string action, string? targetType = null,
            string? targetId = null, object? metadata = null, long? actAsTenantId = null,
            HttpContext? httpContext = null, CancellationToken ct = default)
            => throw new InvalidOperationException("Audit store unavailable (test double).");
    }

    private static async Task<(long TenantId, long BusinessUnitId)> SeedTenantAsync(
        BillingTestDb db, bool withPlan = false, decimal? monthlyPriceUsd = null)
    {
        await using var seed = db.Context(null);
        var bu = NewBusinessUnit("BILL");
        seed.Add(bu);
        await seed.SaveChangesAsync();

        long? planId = null;
        if (withPlan || monthlyPriceUsd is not null)
        {
            var plan = new Plan
            {
                Code = $"pro-{Guid.NewGuid():N}"[..12],
                Name = "Pro",
                MonthlyPriceUsd = monthlyPriceUsd,
                CreatedOn = DateTime.UtcNow
            };
            seed.Add(plan);
            await seed.SaveChangesAsync();
            planId = plan.Id;
        }

        var tenant = new Tenant
        {
            Name = "Billing Tenant",
            Slug = $"billing-{Guid.NewGuid():N}"[..20],
            Status = TenantStatus.Active,
            PrimaryBusinessUnitId = bu.Id,
            PlanId = planId,
            CreatedBy = "test",
            CreatedOn = DateTime.UtcNow
        };
        seed.Add(tenant);
        await seed.SaveChangesAsync();
        return (tenant.Id, bu.Id);
    }

    private static async Task<long> SeedRateCardAsync(BillingTestDb db)
    {
        await using var seed = db.Context(null);
        var card = new RateCard
        {
            Code = $"standard-{Guid.NewGuid():N}"[..20],
            Currency = "USD",
            EffectiveFromUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            IsActive = true,
            CreatedBy = "test",
            Lines =
            {
                new RateCardLine { MeterKey = BillingMeterKeys.Documents, IncludedQuantity = 2m, UnitPrice = 1.50m, Unit = "document" },
                new RateCardLine { MeterKey = BillingMeterKeys.AiTokensExternal, IncludedQuantity = 50_000m, UnitPrice = 0.001m, Unit = "1K tokens" },
                new RateCardLine { MeterKey = BillingMeterKeys.Seats, IncludedQuantity = 1m, UnitPrice = 30m, Unit = "seat" }
            }
        };
        seed.Add(card);
        await seed.SaveChangesAsync();
        return card.Id;
    }

    /// <summary>5 documents, 175,000 settled external tokens, 3 active seats in July 2026.</summary>
    private static async Task SeedStandardJulyUsageAsync(
        BillingTestDb db, long buId, decimal? aiCostPerRequest = null)
    {
        await using var seed = db.Context(null);
        for (var i = 0; i < 5; i++)
            seed.Add(NewJob(buId, InJuly.AddHours(i)));
        seed.AddRange(
            NewAiRequest(buId, AiProviderClass.External, AiCallStatuses.Succeeded, 100_000, 0, InJuly,
                estimatedCost: aiCostPerRequest, costStatus: aiCostPerRequest is null ? AiCostStatuses.RateUnavailable : AiCostStatuses.Priced),
            NewAiRequest(buId, AiProviderClass.External, AiCallStatuses.Succeeded, 50_000, 25_000, InJuly.AddDays(1),
                estimatedCost: aiCostPerRequest, costStatus: aiCostPerRequest is null ? AiCostStatuses.RateUnavailable : AiCostStatuses.Priced));
        seed.AddRange(NewUser(buId, true), NewUser(buId, true), NewUser(buId, true));
        await seed.SaveChangesAsync();
    }

    /// <summary>
    /// Seeds one ExtractionJob plus its evidence-ledger trail: a corpus, a
    /// SourceDocument (ByteSize = <paramref name="byteSize"/>, ContentHash shared
    /// with the job) and <paramref name="attempts"/> completed ExtractionRuns that
    /// each record <paramref name="pages"/> pages (<paramref name="ocrPages"/> of
    /// them OCR) — mirroring how ExtractionWorker persists retries: one run per
    /// attempt, same document, same counts.
    /// </summary>
    private static async Task<ExtractionJob> SeedJobWithRunsAsync(
        ErpRfqAutomationContext seed, long buId, DateTime createdOn, ExtractionStatus status,
        int pages, int ocrPages = 0, int attempts = 1, long byteSize = 4_096L)
    {
        var job = NewJob(buId, createdOn, status);
        var corpus = DocumentCorpus.Create(buId, Guid.NewGuid(), CorpusSourceType.ManualUpload, createdOn);
        seed.AddRange(job, corpus);
        await seed.SaveChangesAsync();

        var document = SourceDocument.Create(buId, corpus.Id, job.ContentHash, "billing-test.pdf",
            "application/pdf", "evidence", $"objects/{Guid.NewGuid():N}", "1", byteSize, createdOn);
        seed.Add(document);
        await seed.SaveChangesAsync();

        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            var run = ExtractionRun.Create(buId, document.Id, Guid.NewGuid(), job.Id, attempt,
                "llm-unstructured/v1", "lead-extraction/v1", createdOn);
            run.Start(createdOn);
            if (ocrPages > 0)
                run.RecordProcessingEvidence(ExtractionProcessingPath.LocalOcr,
                    ExtractionOcrStatus.Completed, ocrPages, ocrTruncated: false);
            run.Complete(pages, 0, 0, 0, 0, 0, createdOn.AddMinutes(1));
            seed.Add(run);
        }
        await seed.SaveChangesAsync();
        return job;
    }

    /// <summary>Seeds a stand-alone retained evidence document (storage meter input).</summary>
    private static async Task SeedSourceDocumentAsync(
        ErpRfqAutomationContext seed, long buId, long byteSize, DateTime createdOn)
    {
        var corpus = DocumentCorpus.Create(buId, Guid.NewGuid(), CorpusSourceType.ManualUpload, createdOn);
        seed.Add(corpus);
        await seed.SaveChangesAsync();
        seed.Add(SourceDocument.Create(buId, corpus.Id,
            Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N"), "stored.bin",
            "application/octet-stream", "evidence", $"objects/{Guid.NewGuid():N}", "1", byteSize, createdOn));
        await seed.SaveChangesAsync();
    }

    private static BusinessUnit NewBusinessUnit(string code) => new()
    {
        BusinessUnitCode = $"{code}-{Guid.NewGuid():N}"[..12],
        BusinessUnitName = $"BU {code}",
        IsActive = true,
        CreatedBy = "test",
        CreatedOn = DateTime.UtcNow
    };

    private static ExtractionJob NewJob(
        long buId, DateTime createdOn, ExtractionStatus status = ExtractionStatus.Succeeded) => new()
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

    private static AiRequest NewAiRequest(
        long buId, AiProviderClass providerClass, string status,
        long inputTokens, long outputTokens, DateTime createdOn,
        decimal? estimatedCost = null, string costStatus = AiCostStatuses.RateUnavailable) => new()
    {
        Id = Guid.NewGuid(),
        BusinessUnitId = buId,
        Operation = "RfqExtraction",
        IdempotencyKey = Guid.NewGuid().ToString("N"),
        PromptHash = new string('a', 64),
        PromptVersion = "v1",
        Provider = providerClass == AiProviderClass.Local ? "local-ollama" : "external-api",
        ProviderClass = providerClass,
        Model = "test-model",
        Status = status,
        InputTokens = inputTokens,
        OutputTokens = outputTokens,
        EstimatedCost = estimatedCost,
        CostStatus = costStatus,
        TokenSource = AiTokenSources.ProviderExact,
        CreatedOn = createdOn,
        CompletedOn = createdOn.AddSeconds(30)
    };

    /// <summary>
    /// Seat-holder factory. CreatedOn defaults to inside the July test period so
    /// the reproducible seats derivation (CreatedOn &lt; PeriodEnd) counts them;
    /// deactivatedAtUtc drives the P0-B2 timestamp rules.
    /// </summary>
    private static User NewUser(
        long buId, bool active, DateTime? createdOn = null, DateTime? deactivatedAtUtc = null) => new()
    {
        FirstName = "Seat",
        LastName = "Holder",
        Email = $"seat-{Guid.NewGuid():N}@nexora.test",
        PasswordHash = "hash",
        ImageUrl = "",
        Buid = buId,
        IsActive = active,
        DeactivatedAtUtc = deactivatedAtUtc,
        CreatedBy = "test",
        CreatedOn = createdOn ?? InJuly
    };

    private static BillingStatement NewStatement(long tenantId, long rateCardId) => new()
    {
        TenantId = tenantId,
        PeriodStartUtc = PeriodStart,
        PeriodEndUtc = PeriodStart.AddMonths(1),
        RateCardId = rateCardId,
        Currency = "USD",
        Status = BillingStatementStatus.Draft,
        TotalAmount = 0m,
        ComputedAtUtc = DateTime.UtcNow
    };

    /// <summary>
    /// SQLite-in-memory database over the REAL model PLUS the billing
    /// configuration, applied by a context subclass because the Integration Owner
    /// wires ConfigureBillingModel into the production OnModelCreatingPartial at
    /// merge time (applying it twice stays harmless).
    /// </summary>
    private sealed class BillingTestDb : IDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly DbContextOptions<ErpRfqAutomationContext> _options;

        public BillingTestDb()
        {
            _connection = new SqliteConnection("Data Source=:memory:");
            _connection.Open();
            _options = new DbContextOptionsBuilder<ErpRfqAutomationContext>()
                .UseSqlite(_connection)
                .AddInterceptors(RivalInsert, VersionBump)
                .EnableSensitiveDataLogging()
                .Options;
            using var context = Context(null);
            context.Database.EnsureCreated();
        }

        public RivalStatementInterceptor RivalInsert { get; } = new();

        public RivalVersionBumpInterceptor VersionBump { get; } = new();

        public BillingEnabledContext Context(long? businessUnitId)
            => new(_options, new StubTenant(businessUnitId));

        public void Dispose() => _connection.Dispose();
    }

    private sealed class BillingEnabledContext : ErpRfqAutomationContext
    {
        public BillingEnabledContext(DbContextOptions<ErpRfqAutomationContext> options, ITenantContext tenant)
            : base(options, tenant)
        {
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            BillingModelConfiguration.Apply(modelBuilder);

            // The evidence ledger (SourceDocuments / ExtractionRuns) backs the
            // pages.processed / pages.ocr / storage.gb meters. Production maps it
            // on PostgreSQL only, so the SQLite test model opts in here — minus
            // the five CHECK constraints that use PostgreSQL's regex operator
            // (~), which SQLite cannot parse at EnsureCreated time.
            modelBuilder.AddEvidenceLedger();

            // SQLite cannot translate DateTimeOffset ordering comparisons (it has
            // no sortable storage for them), and the storage meter filters
            // SourceDocuments.CreatedOn < PeriodEnd. Storing that one timestamp as
            // a UTC DateTime in the test lane makes the REAL production query
            // translate here unchanged; PostgreSQL keeps native timestamptz.
            var utcOffset = new ValueConverter<DateTimeOffset, DateTime>(
                v => v.UtcDateTime, v => new DateTimeOffset(v, TimeSpan.Zero));
            modelBuilder.Entity<SourceDocument>().Property(x => x.CreatedOn)
                .HasColumnType("TEXT").HasConversion(utcOffset);
            modelBuilder.Entity<SourceDocument>().Property(x => x.UpdatedOn)
                .HasColumnType("TEXT").HasConversion(utcOffset);

            modelBuilder.Entity<ExtractionRun>().Metadata.RemoveCheckConstraint("ck_extraction_runs_cost");
            modelBuilder.Entity<SourceDocument>().Metadata.RemoveCheckConstraint("ck_source_documents_content_hash");
            modelBuilder.Entity<DocumentPage>().Metadata.RemoveCheckConstraint("ck_document_pages_text_hash");
            modelBuilder.Entity<CanonicalLineItem>().Metadata.RemoveCheckConstraint("ck_canonical_line_items_currency");
            modelBuilder.Entity<FieldEvidence>().Metadata.RemoveCheckConstraint("ck_field_evidence_key");
        }
    }

    /// <summary>
    /// Race simulator for the P2-B8/A5 version races: after the Nth SELECT that
    /// touches BillingStatements, bumps the target statement's Version by raw
    /// SQL — exactly what a rival compute/finalize does between our read and our
    /// SaveChanges, forcing DbUpdateConcurrencyException on the concurrency
    /// token. Joins the intercepted command's transaction when one is open
    /// (Microsoft.Data.Sqlite requires it), so an in-transaction bump rolls back
    /// with the losing transaction — the assertion is that the service catches
    /// the conflict instead of surfacing a 500.
    /// </summary>
    private sealed class RivalVersionBumpInterceptor : DbCommandInterceptor
    {
        private long _statementId;
        private int _matchesToSkip;
        private bool _armed;

        public bool Fired { get; private set; }

        public void Arm(long statementId, int fireOnMatch = 1)
        {
            _statementId = statementId;
            _matchesToSkip = fireOnMatch - 1;
            _armed = true;
            Fired = false;
        }

        public override async ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command,
            CommandExecutedEventData eventData,
            DbDataReader result,
            CancellationToken cancellationToken = default)
        {
            if (_armed
                && command.CommandText.Contains("SELECT", StringComparison.OrdinalIgnoreCase)
                && command.CommandText.Contains("BillingStatements", StringComparison.Ordinal))
            {
                if (_matchesToSkip > 0)
                {
                    _matchesToSkip--;
                }
                else
                {
                    _armed = false;
                    Fired = true;
                    await using var rival = (SqliteCommand)command.Connection!.CreateCommand();
                    rival.Transaction = (SqliteTransaction?)command.Transaction;
                    rival.CommandText =
                        """UPDATE "BillingStatements" SET "Version" = "Version" + 1 WHERE "Id" = $id""";
                    rival.Parameters.AddWithValue("$id", _statementId);
                    await rival.ExecuteNonQueryAsync(cancellationToken);
                }
            }

            return await base.ReaderExecutedAsync(command, eventData, result, cancellationToken);
        }
    }

    /// <summary>
    /// Race simulator: immediately after the FIRST read of BillingStatements
    /// (the service's pre-transaction existence check), commits a rival Draft
    /// statement for the same (tenant, period) on the same connection — the
    /// narrowest window in which a concurrent compute can land its INSERT first.
    /// </summary>
    private sealed class RivalStatementInterceptor : DbCommandInterceptor
    {
        private long _tenantId;
        private long _rateCardId;
        private DateTime _periodStartUtc;
        private bool _armed;

        public bool Fired { get; private set; }

        public void Arm(long tenantId, long rateCardId, DateTime periodStartUtc)
        {
            _tenantId = tenantId;
            _rateCardId = rateCardId;
            _periodStartUtc = periodStartUtc;
            _armed = true;
            Fired = false;
        }

        public override async ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command,
            CommandExecutedEventData eventData,
            DbDataReader result,
            CancellationToken cancellationToken = default)
        {
            if (_armed && !Fired
                && command.CommandText.Contains("SELECT", StringComparison.OrdinalIgnoreCase)
                && command.CommandText.Contains("BillingStatements", StringComparison.Ordinal))
            {
                Fired = true;
                _armed = false;
                await using var rival = (SqliteCommand)command.Connection!.CreateCommand();
                rival.CommandText = """
                    INSERT INTO "BillingStatements"
                        ("TenantId", "PeriodStartUtc", "PeriodEndUtc", "RateCardId", "Currency",
                         "Status", "TotalAmount", "ComputedAtUtc", "Version")
                    VALUES ($tenant, $start, $end, $card, 'USD', 'Draft', '999.99', $computed, 1)
                    """;
                rival.Parameters.AddWithValue("$tenant", _tenantId);
                rival.Parameters.AddWithValue("$start", _periodStartUtc.ToString("yyyy-MM-dd HH:mm:ss"));
                rival.Parameters.AddWithValue("$end", _periodStartUtc.AddMonths(1).ToString("yyyy-MM-dd HH:mm:ss"));
                rival.Parameters.AddWithValue("$card", _rateCardId);
                rival.Parameters.AddWithValue("$computed", DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.FFFFFFF"));
                await rival.ExecuteNonQueryAsync(cancellationToken);
            }

            return await base.ReaderExecutedAsync(command, eventData, result, cancellationToken);
        }
    }
}
