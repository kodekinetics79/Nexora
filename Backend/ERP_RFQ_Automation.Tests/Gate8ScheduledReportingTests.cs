using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.MultiTenancy;
using ERP_RFQ_Automation.Reporting;
using ERP_RFQ_Automation.Sla;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// FR-DSH-06 scheduled reporting, and above all the tenant boundary the worker runs under.
///
/// <para>A background job has no HttpContext, so it is handed the BYPASSRLS pipeline role, and a
/// null tenant makes every EF global filter a no-op — both isolation layers off at once. A
/// reporting worker is the worst possible place for that to happen, because a report is an artefact
/// that leaves the building. The tests below assert the fail-closed contract directly, mirroring
/// <c>SlaSweepWorkerScaleOutTests</c>.</para>
/// </summary>
public sealed class Gate8ScheduledReportingTests
{
    private const long Bu1 = 8_310;
    private const long Bu2 = 8_320;
    private static readonly DateTime Anchor = new(2026, 6, 10, 6, 0, 0, DateTimeKind.Utc);

    // ───────────────────────────── the tenant boundary

    /// <summary>
    /// Every query the sweep runs must see exactly one tenant. If a predicate is dropped, the
    /// observed-tenant list gains a null and this fails.
    /// </summary>
    [Fact]
    public async Task Each_tenant_is_swept_inside_its_own_pushed_scope()
    {
        using var host = new ReportHost();
        await host.SeedDueSubscriptionAsync(Bu1, id: 1);
        await host.SeedDueSubscriptionAsync(Bu2, id: 2);

        var swept = await host.CreateWorker().SweepOnceAsync(default);

        Assert.Equal(2, swept);

        // Exactly ONE unscoped context: the ids-only discovery query, which must run without a
        // tenant so the work gate's platform read is not refused at column level. If a second null
        // appears, some per-tenant query escaped its scope and ran under the bypass role.
        Assert.Equal(1, host.ObservedTenants.Count(t => t is null));
        Assert.Equal(new[] { Bu1, Bu2 }, host.ObservedTenants.Where(t => t.HasValue)
            .Select(t => t!.Value).Distinct().OrderBy(t => t).ToArray());
    }

    /// <summary>
    /// The fail-closed half. With the tenant scope not honoured — the exact shape of the wiring
    /// defect — the worker must refuse rather than run its body under the bypass role. Nothing is
    /// delivered and no claim is written.
    /// </summary>
    [Fact]
    public async Task A_scope_that_does_not_apply_stops_the_sweep_instead_of_running_unscoped()
    {
        using var host = new ReportHost(honourTenantScope: false);
        await host.SeedDueSubscriptionAsync(Bu1, id: 1);
        await host.SeedDueSubscriptionAsync(Bu2, id: 2);

        await host.CreateWorker().SweepOnceAsync(default);

        Assert.Empty(host.Delivery.Sent);
        await using var ctx = host.UnscopedContext();
        Assert.Empty(await ctx.Set<SlaEvent>().IgnoreQueryFilters().ToListAsync());
    }

    /// <summary>
    /// A tenant's report must never carry another tenant's rows. The neighbour is given a due
    /// subscription and its own data; the delivered document is asserted to name only the tenant it
    /// was built for.
    /// </summary>
    [Fact]
    public async Task A_delivered_report_carries_only_its_own_tenants_rows()
    {
        using var host = new ReportHost();
        await host.SeedDueSubscriptionAsync(Bu1, id: 1);
        await host.SeedDueSubscriptionAsync(Bu2, id: 2);

        await host.CreateWorker().SweepOnceAsync(default);

        Assert.Equal(2, host.Delivery.Sent.Count);
        foreach (var sent in host.Delivery.Sent)
            Assert.Equal($"Business Unit {sent.BusinessUnitId}", sent.Document.TenantLabel);
    }

    // ───────────────────────────── send-once

    [Fact]
    public async Task A_second_sweep_in_the_same_period_does_not_send_the_report_twice()
    {
        using var host = new ReportHost();
        await host.SeedDueSubscriptionAsync(Bu1, id: 1);

        await host.CreateWorker().SweepOnceAsync(default);
        await host.CreateWorker().SweepOnceAsync(default);

        // The schedule has already been advanced past now by the first sweep, so the second finds
        // nothing due. Both halves matter: the claim stops a concurrent duplicate, and advancing
        // past now stops a dormant subscription replaying every occurrence it missed.
        Assert.Single(host.Delivery.Sent);
    }

    /// <summary>
    /// A dormant subscription delivers the current period ONCE and resumes. Replaying a quarter of
    /// missed daily occurrences into a director's inbox is how a reporting channel gets filtered.
    /// </summary>
    [Fact]
    public void A_long_dormant_schedule_resumes_in_the_future_rather_than_replaying_its_backlog()
    {
        var subscription = new ReportSubscription { Cadence = ReportCadences.Daily, HourUtc = 6 };
        var now = new DateTime(2026, 6, 10, 9, 30, 0, DateTimeKind.Utc);
        var missedOccurrence = new DateTime(2026, 1, 1, 6, 0, 0, DateTimeKind.Utc);

        var next = ScheduledReportWorker.AdvanceSchedule(subscription, missedOccurrence, now);

        Assert.True(next > now);
        Assert.Equal(6, next.Hour);          // the configured slot is preserved, not drifted
        Assert.Equal(new DateTime(2026, 6, 11, 6, 0, 0, DateTimeKind.Utc), next);
    }

    /// <summary>
    /// A delivery the provider demonstrably never took RELEASES its claim so the next cycle retries,
    /// and the failure is visible on the subscription rather than only in a log nobody reads.
    /// RELEASED is the one status that frees the dedup key.
    /// </summary>
    [Fact]
    public async Task A_delivery_the_provider_never_took_releases_its_claim_and_records_the_failure()
    {
        using var host = new ReportHost();
        host.Delivery.Outcome = SlaSendOutcome.NotSent;
        await host.SeedDueSubscriptionAsync(Bu1, id: 1);

        await host.CreateWorker().SweepOnceAsync(default);

        await using var ctx = host.UnscopedContext();
        var claim = Assert.Single(await ctx.Set<SlaEvent>().IgnoreQueryFilters().ToListAsync());
        Assert.Equal(SlaEventStatuses.Released, claim.Status);
        Assert.True(SlaEventStatuses.IsReclaimable(claim.Status));

        var subscription = await ctx.Set<ReportSubscription>().IgnoreQueryFilters().SingleAsync(s => s.Id == 1);
        Assert.Equal(ReportRunOutcomes.Failed, subscription.LastRunOutcome);
        Assert.False(string.IsNullOrWhiteSpace(subscription.LastRunDetail));
    }

    /// <summary>
    /// An UNCERTAIN delivery KEEPS its claim and is never retried. A dropped connection after the
    /// body was accepted is routine, and a second copy of a board report is not recoverable from the
    /// recipient's inbox — so the run is recorded as failed for a human to act on, not re-sent.
    /// </summary>
    [Fact]
    public async Task An_uncertain_delivery_keeps_its_claim_and_is_never_retried()
    {
        using var host = new ReportHost();
        host.Delivery.Outcome = SlaSendOutcome.Uncertain;
        await host.SeedDueSubscriptionAsync(Bu1, id: 1);

        await host.CreateWorker().SweepOnceAsync(default);

        await using var ctx = host.UnscopedContext();
        var claim = Assert.Single(await ctx.Set<SlaEvent>().IgnoreQueryFilters().ToListAsync());
        Assert.Equal(SlaEventStatuses.Uncertain, claim.Status);
        Assert.False(SlaEventStatuses.IsReclaimable(claim.Status));

        var subscription = await ctx.Set<ReportSubscription>().IgnoreQueryFilters().SingleAsync(s => s.Id == 1);
        Assert.Equal(ReportRunOutcomes.Failed, subscription.LastRunOutcome);
        Assert.Contains("uncertain", subscription.LastRunDetail!, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// An empty report is not sent. A report that arrives empty every morning is exactly how a
    /// reporting channel gets filtered, and the real one is lost with it.
    /// </summary>
    [Fact]
    public async Task A_report_with_no_rows_is_recorded_and_not_emailed()
    {
        using var host = new ReportHost();
        await host.SeedDueSubscriptionAsync(Bu1, id: 1, withData: false);

        await host.CreateWorker().SweepOnceAsync(default);

        Assert.Empty(host.Delivery.Sent);
        await using var ctx = host.UnscopedContext();
        var subscription = await ctx.Set<ReportSubscription>().IgnoreQueryFilters().SingleAsync(s => s.Id == 1);
        Assert.Equal(ReportRunOutcomes.NothingToReport, subscription.LastRunOutcome);
    }

    /// <summary>A paused subscription is not due, whatever date it last carried.</summary>
    [Fact]
    public async Task A_paused_subscription_is_never_swept()
    {
        using var host = new ReportHost();
        await host.SeedDueSubscriptionAsync(Bu1, id: 1, isActive: false);

        var swept = await host.CreateWorker().SweepOnceAsync(default);

        Assert.Equal(0, swept);
        Assert.Empty(host.Delivery.Sent);
    }

    /// <summary>
    /// A null <c>NextRunOn</c> means NOT SCHEDULED. If it were ever read as "due since forever" the
    /// first sweep after deployment would mail the entire estate — failure #10 in the wiring
    /// contract, in date form.
    /// </summary>
    [Fact]
    public async Task A_subscription_with_no_next_run_is_not_due()
    {
        using var host = new ReportHost();
        await host.SeedDueSubscriptionAsync(Bu1, id: 1, scheduled: false);

        var swept = await host.CreateWorker().SweepOnceAsync(default);

        Assert.Equal(0, swept);
        Assert.Empty(host.Delivery.Sent);
    }

    // ───────────────────────────── schedule maths

    [Theory]
    [InlineData(ReportCadences.Daily, 6, 0, 1, "2026-06-10T07:00:00Z", "2026-06-11T06:00:00Z")]
    [InlineData(ReportCadences.Daily, 6, 0, 1, "2026-06-10T05:00:00Z", "2026-06-10T06:00:00Z")]
    // 2026-06-10 is a Wednesday; the next Sunday is the 14th.
    [InlineData(ReportCadences.Weekly, 6, 0, 1, "2026-06-10T05:00:00Z", "2026-06-14T06:00:00Z")]
    [InlineData(ReportCadences.Monthly, 6, 0, 1, "2026-06-10T05:00:00Z", "2026-07-01T06:00:00Z")]
    public void The_next_occurrence_is_strictly_after_the_moment_it_is_computed_from(
        string cadence, int hour, int dayOfWeek, int dayOfMonth, string after, string expected)
    {
        var subscription = new ReportSubscription
        {
            Cadence = cadence, HourUtc = hour, DayOfWeek = dayOfWeek, DayOfMonth = dayOfMonth
        };

        var next = ReportSubscriptionService.NextOccurrence(subscription,
            DateTime.Parse(after, null, System.Globalization.DateTimeStyles.AdjustToUniversal
                                       | System.Globalization.DateTimeStyles.AssumeUniversal));

        Assert.Equal(DateTime.Parse(expected, null,
            System.Globalization.DateTimeStyles.AdjustToUniversal
            | System.Globalization.DateTimeStyles.AssumeUniversal), next);
    }

    /// <summary>
    /// Computed from the OCCURRENCE, not from now. Advancing from "now" would let a worker that ran
    /// late shift a 06:00 report a few minutes later every single day until it drifted off its slot.
    /// </summary>
    [Fact]
    public async Task The_schedule_advances_from_the_occurrence_not_from_the_run_time()
    {
        using var host = new ReportHost();
        var occurrence = new DateTime(2026, 6, 9, 6, 0, 0, DateTimeKind.Utc);
        await host.SeedDueSubscriptionAsync(Bu1, id: 1, cadence: ReportCadences.Daily, nextRunOn: occurrence);

        await host.CreateWorker().SweepOnceAsync(default);

        await using var ctx = host.UnscopedContext();
        var subscription = await ctx.Set<ReportSubscription>().IgnoreQueryFilters().SingleAsync(s => s.Id == 1);

        // The SLOT is what must not drift. Advancing from the run time instead of the occurrence
        // would move a 06:00 report to whatever minute the worker happened to start.
        Assert.NotNull(subscription.NextRunOn);
        Assert.Equal(6, subscription.NextRunOn!.Value.Hour);
        Assert.Equal(0, subscription.NextRunOn.Value.Minute);
        Assert.True(subscription.NextRunOn > DateTime.UtcNow);
    }

    // ───────────────────────────── validation rejects the wrong values

    [Fact]
    public async Task A_subscription_with_no_recipient_is_refused()
    {
        using var db = new TestDb();
        using (var seed = db.ContextFor(null)) { Seed.EnsureBusinessUnit(seed, Bu1); await seed.SaveChangesAsync(); }
        using var ctx = db.ContextFor(Bu1);

        var error = await Assert.ThrowsAsync<ReportSubscriptionValidationException>(() =>
            new ReportSubscriptionService(ctx).UpsertAsync(Bu1, "tester", Command(recipients: "   ")));

        Assert.Contains("recipient", error.Message);
    }

    [Theory]
    [InlineData("not-a-report", ReportCadences.Daily, ReportFormats.Pdf, 6, 7)]
    [InlineData(ReportKeys.Pipeline, "HOURLY", ReportFormats.Pdf, 6, 7)]
    [InlineData(ReportKeys.Pipeline, ReportCadences.Daily, "CSV", 6, 7)]
    [InlineData(ReportKeys.Pipeline, ReportCadences.Daily, ReportFormats.Pdf, 24, 7)]
    [InlineData(ReportKeys.Pipeline, ReportCadences.Daily, ReportFormats.Pdf, 6, 0)]
    public async Task Values_that_would_schedule_something_undeliverable_are_refused(
        string reportKey, string cadence, string format, int hourUtc, int windowDays)
    {
        using var db = new TestDb();
        using (var seed = db.ContextFor(null)) { Seed.EnsureBusinessUnit(seed, Bu1); await seed.SaveChangesAsync(); }
        using var ctx = db.ContextFor(Bu1);

        await Assert.ThrowsAsync<ReportSubscriptionValidationException>(() =>
            new ReportSubscriptionService(ctx).UpsertAsync(Bu1, "tester",
                Command(reportKey, cadence, format, hourUtc, windowDays)));
    }

    /// <summary>Pausing clears the due date rather than leaving a stale past one behind, so
    /// un-pausing does not fire instantly.</summary>
    [Fact]
    public async Task Pausing_a_subscription_clears_its_next_run()
    {
        using var db = new TestDb();
        using (var seed = db.ContextFor(null)) { Seed.EnsureBusinessUnit(seed, Bu1); await seed.SaveChangesAsync(); }
        using var ctx = db.ContextFor(Bu1);
        var service = new ReportSubscriptionService(ctx);

        var active = await service.UpsertAsync(Bu1, "tester", Command());
        Assert.NotNull(active.NextRunOn);

        var paused = await service.UpsertAsync(Bu1, "tester", Command(isActive: false) with { Id = active.Id });
        Assert.Null(paused.NextRunOn);
    }

    private static UpsertReportSubscriptionCommand Command(
        string reportKey = ReportKeys.Pipeline, string cadence = ReportCadences.Daily,
        string format = ReportFormats.Pdf, int hourUtc = 6, int windowDays = 7,
        string recipients = "board@tenant.test", bool isActive = true)
        => new(null, reportKey, cadence, format, hourUtc, 0, 1, windowDays, recipients, isActive);

    // ───────────────────────────── harness

    private sealed record SentReport(long BusinessUnitId, ReportDocument Document, RenderedReport Rendered);


    private sealed class CapturingDelivery : IReportDelivery
    {
        private readonly object _gate = new();
        public List<SentReport> Sent { get; } = new();

        /// <summary>Outcome the fake provider reports. Sent by default.</summary>
        public SlaSendOutcome Outcome { get; set; } = SlaSendOutcome.Sent;

        public Task<SlaSendResult> SendAsync(IReadOnlyList<string> recipients, ReportDocument document,
            RenderedReport rendered, long businessUnitId, CancellationToken ct = default)
        {
            if (Outcome == SlaSendOutcome.NotSent)
                return Task.FromResult(SlaSendResult.NotSent("The fake provider refused it."));
            if (Outcome == SlaSendOutcome.Uncertain)
                return Task.FromResult(SlaSendResult.Uncertain("The fake provider returned no receipt."));

            lock (_gate) Sent.Add(new SentReport(businessUnitId, document, rendered));
            return Task.FromResult(new SlaSendResult(SlaSendOutcome.Sent, "fake", "accept-1"));
        }
    }

    /// <summary>
    /// Mirrors production wiring: an ambient <see cref="ITenantScopeAccessor"/> the worker pushes
    /// onto, and a scoped <see cref="ITenantContext"/> that reads it when the DbContext is built.
    /// <c>honourTenantScope: false</c> models the broken wiring the fail-closed guard exists for.
    /// </summary>
    private sealed class ReportHost : IDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly ServiceProvider _provider;
        private readonly DbContextOptions<ErpRfqAutomationContext> _rawOptions;
        private readonly object _gate = new();

        public CapturingDelivery Delivery { get; } = new();
        public List<long?> ObservedTenants { get; } = new();

        public ReportHost(bool honourTenantScope = true)
        {
            _connection = new SqliteConnection("Data Source=:memory:");
            _connection.Open();
            _rawOptions = new DbContextOptionsBuilder<ErpRfqAutomationContext>().UseSqlite(_connection).Options;
            using (var create = new ErpRfqAutomationContext(_rawOptions, new StubTenant(null)))
            {
                create.Database.EnsureCreated();
                create.Database.ExecuteSqlRaw("PRAGMA ignore_check_constraints = ON");
            }

            var services = new ServiceCollection();
            services.AddSingleton<ITenantScopeAccessor, TenantScopeAccessor>();
            services.AddScoped<ITenantContext>(sp =>
            {
                var ambient = honourTenantScope
                    ? sp.GetRequiredService<ITenantScopeAccessor>().BusinessUnitId
                    : null;
                lock (_gate) ObservedTenants.Add(ambient);
                return new StubTenant(ambient);
            });
            services.AddDbContext<ErpRfqAutomationContext>(o => o.UseSqlite(_connection), ServiceLifetime.Scoped);
            services.AddScoped<ERP_RFQ_Automation.Interfaces.IDashboardRepository,
                ERP_RFQ_Automation.Repositories.DashboardRepository>();
            services.AddScoped<IGrossMarginService, GrossMarginService>();
            services.AddScoped<IReportBuilder, ReportBuilder>();
            services.AddScoped<IReportRenderer, ReportRenderer>();
            services.AddSingleton<IReportDelivery>(Delivery);
            _provider = services.BuildServiceProvider();
        }

        public ScheduledReportWorker CreateWorker() => new(
            _provider.GetRequiredService<IServiceScopeFactory>(),
            _provider.GetRequiredService<ITenantScopeAccessor>(),
            NullLogger<ScheduledReportWorker>.Instance);

        public ErpRfqAutomationContext UnscopedContext() => new(_rawOptions, new StubTenant(null));

        /// <summary>
        /// A due deadline-board subscription plus, unless suppressed, one open enquiry so the report
        /// has rows. The lead is what makes the difference between DELIVERED and NOTHING_TO_REPORT.
        /// </summary>
        public async Task SeedDueSubscriptionAsync(long bu, long id, bool withData = true,
            bool isActive = true, string cadence = ReportCadences.Weekly, DateTime? nextRunOn = null,
            bool scheduled = true)
        {
            await using var seed = UnscopedContext();
            seed.Database.ExecuteSqlRaw("PRAGMA ignore_check_constraints = ON");

            Seed.EnsureBusinessUnit(seed, bu);
            await seed.SaveChangesAsync();

            if (withData)
            {
                var lead = Seed.Lead(seed, leadId: bu + id, businessUnitId: bu);
                lead.BidClosingDate = DateTime.UtcNow.AddDays(2);
                await seed.SaveChangesAsync();
            }

            seed.Set<ReportSubscription>().Add(new ReportSubscription
            {
                Id = id,
                BusinessUnitId = bu,
                ReportKey = ReportKeys.DeadlineBoard,
                Cadence = cadence,
                Format = ReportFormats.Xlsx,
                HourUtc = 6,
                DayOfWeek = 0,
                DayOfMonth = 1,
                WindowDays = 7,
                Recipients = "board@tenant.test",
                IsActive = isActive,
                // Explicitly in the past so the row is due on the very next sweep. `null` is the
                // "not scheduled" case and has its own test.
                NextRunOn = !scheduled || !isActive ? null : nextRunOn ?? Anchor.AddDays(-1),
                CreatedOn = Anchor,
                CreatedBy = "seed"
            });
            await seed.SaveChangesAsync();
        }

        public void Dispose()
        {
            _provider.Dispose();
            _connection.Dispose();
        }
    }
}
