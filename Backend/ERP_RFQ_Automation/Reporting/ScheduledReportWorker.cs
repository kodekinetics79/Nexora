using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ERP_RFQ_Automation.HealthChecks;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.MultiTenancy;
using ERP_RFQ_Automation.Sla;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ERP_RFQ_Automation.Reporting;

/// <summary>
/// FR-DSH-06 — delivers each tenant's due report subscriptions by email.
///
/// <para><b>TENANT SCOPE IS MANDATORY, and this worker copies
/// <see cref="SlaSweepWorker"/>'s contract line for line.</b> A background service has no
/// HttpContext, so <c>TenantRlsCommandInterceptor.ResolveDatabaseRole</c> hands it the BYPASSRLS
/// <c>nexora_pipeline_app</c> role, and with a null tenant every EF global filter
/// (<c>CurrentTenantId == null || ...</c>) is a no-op — BOTH isolation layers off at once. A
/// reporting worker is the worst possible place for that, because reporting reads across the whole
/// tenant by design and one missing predicate mails another company's order book to this one's
/// board. So: ONE ids-only discovery query runs unscoped, then every subsequent query runs inside a
/// pushed tenant scope, and a fail-closed check refuses to proceed if the DbContext did not pick
/// that scope up. Explicit <c>BusinessUnitId</c> predicates are kept as defence in depth.</para>
///
/// <para><b>Send-once is the database's job</b>, via the existing <c>SlaEvents</c> claim ledger and
/// its unique <c>(BusinessUnitId, DedupKey)</c> index. The claim goes in BEFORE the mail leaves, so
/// on a scaled-out deployment the losing instance takes a 23505 and skips instead of sending a
/// second copy. A claim whose send then fails is released, so the next cycle retries.</para>
///
/// <para><b>A subscription that produces nothing is not sent.</b> Reports with no rows record
/// <c>NOTHING_TO_REPORT</c> and advance the schedule without mailing. An empty report arriving every
/// morning is exactly how a reporting channel gets filtered, and once filtered the real one is lost
/// with it.</para>
/// </summary>
public sealed class ScheduledReportWorker : BackgroundService
{
    /// <summary>
    /// Sweep period. Fifteen minutes, not five: subscriptions are due on the hour, so a quarter-hour
    /// resolution is ample and it keeps report rendering off the SLA sweep's cadence.
    /// </summary>
    public static readonly TimeSpan Period = TimeSpan.FromMinutes(15);

    /// <summary>SlaEvent vocabulary for a report send. No schema change: EntityType is varchar(40)
    /// with no CHECK constraint, and this value is 17 characters.</summary>
    internal const string ClaimEntityType = "scheduled-report";
    internal const string ClaimLevel = "sent";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ITenantScopeAccessor _tenantScope;
    private readonly IBackgroundWorkerHeartbeats? _heartbeats;
    private readonly ILogger<ScheduledReportWorker> _log;

    public ScheduledReportWorker(
        IServiceScopeFactory scopeFactory,
        ITenantScopeAccessor tenantScope,
        ILogger<ScheduledReportWorker> log,
        IBackgroundWorkerHeartbeats? heartbeats = null)
    {
        _scopeFactory = scopeFactory;
        _tenantScope = tenantScope;
        _log = log;
        _heartbeats = heartbeats;
        _heartbeats?.Register(BackgroundWorkerNames.ScheduledReports, Period);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _log.LogInformation("ScheduledReportWorker starting; period {Period}.", Period);
        _heartbeats?.Beat(BackgroundWorkerNames.ScheduledReports, Period);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SweepOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Scheduled report sweep failed; will retry next period.");
            }

            _heartbeats?.Beat(BackgroundWorkerNames.ScheduledReports, Period);

            try { await Task.Delay(Period, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }

        _log.LogInformation("ScheduledReportWorker stopped.");
    }

    /// <summary>Resolves the tenant list once, then does all work inside a pushed tenant scope.</summary>
    internal async Task<int> SweepOnceAsync(CancellationToken ct)
    {
        var businessUnits = await ResolveBusinessUnitsWithDueReportsAsync(ct);

        foreach (var bu in businessUnits)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await SweepTenantAsync(bu, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                // One misbehaving tenant must never block the others.
                _log.LogError(ex, "Scheduled reports failed for BU {Bu}; continuing with next tenant.", bu);
            }
        }

        return businessUnits.Count;
    }

    /// <summary>
    /// The ONLY query in this worker without a tenant scope, and therefore the only one running
    /// under the BYPASSRLS pipeline role. It reads nothing but tenant ids.
    ///
    /// <para>Suspended and archived tenants are dropped HERE, in this scope, for the reason
    /// <see cref="SlaSweepWorker"/> documents: consulted under a pushed scope the work gate's
    /// platform read is refused at column level and fails OPEN, admitting everyone silently. Skipping
    /// is safe because a due date is recomputed from the clock every cycle — a suspension defers a
    /// report, it does not destroy one.</para>
    /// </summary>
    private async Task<IReadOnlyList<long>> ResolveBusinessUnitsWithDueReportsAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ErpRfqAutomationContext>();

        var now = DateTime.UtcNow;
        var businessUnits = await db.Set<ReportSubscription>().AsNoTracking().IgnoreQueryFilters()
            .Where(s => s.IsActive && s.NextRunOn != null && s.NextRunOn <= now)
            .Select(s => s.BusinessUnitId)
            .Distinct()
            .OrderBy(id => id)
            .ToListAsync(ct);

        var gate = scope.ServiceProvider
            .GetService<ERP_RFQ_Automation.Platform.Lifecycle.ITenantWorkGate>();
        if (gate is null || businessUnits.Count == 0) return businessUnits;

        return await gate.FilterServiceableAsync(businessUnits, ct);
    }

    private async Task SweepTenantAsync(long bu, CancellationToken ct)
    {
        using var tenant = _tenantScope.Push(bu);
        using var scope = _scopeFactory.CreateScope();

        var db = scope.ServiceProvider.GetRequiredService<ErpRfqAutomationContext>();

        // Fail closed. Without this every query below would silently run cross-tenant under the
        // bypass role, and a report is precisely the artefact that would then leave the building.
        if (db.ScopedTenantId != bu)
        {
            throw new InvalidOperationException(
                $"Scheduled reporting refused to run for BU {bu}: the DbContext resolved tenant " +
                $"{db.ScopedTenantId?.ToString() ?? "<none>"}. Tenant scope is mandatory for this worker.");
        }

        var builder = scope.ServiceProvider.GetRequiredService<IReportBuilder>();
        var renderer = scope.ServiceProvider.GetRequiredService<IReportRenderer>();
        var delivery = scope.ServiceProvider.GetRequiredService<IReportDelivery>();

        var now = DateTime.UtcNow;
        var due = await db.Set<ReportSubscription>()
            .Where(s => s.BusinessUnitId == bu && s.IsActive && s.NextRunOn != null && s.NextRunOn <= now)
            .ToListAsync(ct);
        if (due.Count == 0) return;

        var tenantLabel = await ResolveTenantLabelAsync(db, bu, ct);

        foreach (var subscription in due)
        {
            ct.ThrowIfCancellationRequested();
            await RunOneAsync(db, builder, renderer, delivery, bu, tenantLabel, subscription, now, ct);
        }
    }

    private async Task RunOneAsync(ErpRfqAutomationContext db, IReportBuilder builder,
        IReportRenderer renderer, IReportDelivery delivery, long bu, string tenantLabel,
        ReportSubscription subscription, DateTime now, CancellationToken ct)
    {
        var occurrence = subscription.NextRunOn!.Value;

        // Claim BEFORE anything is sent. The unique index is the correctness boundary; the key
        // carries the occurrence date so a daily report earns one claim per day rather than one ever.
        //
        // The claim is deliberately NOT recipient-scoped. An SLA escalation to three people is three
        // separate sends and therefore needs three independent claims; a scheduled report is ONE
        // message addressed to all its recipients, so there is exactly one send and one outcome, and
        // splitting the claim per address would invent evidence the transport never gave us.
        var claim = await SlaSweepWorker.TryClaimEventAsync(db, bu, ClaimEntityType, subscription.Id,
            ClaimLevel, occurrence.Date, ct);
        if (claim is null)
        {
            // Another instance owns this occurrence. Advance our copy of the schedule so this row
            // does not spin, and leave the outcome fields to whoever actually sends.
            subscription.NextRunOn = AdvanceSchedule(subscription, occurrence, DateTime.UtcNow);
            await db.SaveChangesAsync(ct);
            return;
        }

        var to = occurrence;
        var from = to.AddDays(-Math.Max(1, subscription.WindowDays));

        try
        {
            var document = await builder.BuildAsync(bu, subscription.ReportKey, tenantLabel, from, to, now, ct);

            if (document.IsEmpty)
            {
                // Nothing happened in the window. Record it and stay quiet: a report that arrives
                // empty every morning is how the whole channel gets filtered.
                await SlaSweepWorker.ReleaseEventClaimAsync(db, claim, ct);
                await CompleteAsync(db, subscription, occurrence, now, ReportRunOutcomes.NothingToReport,
                    "The report produced no rows for this period, so no email was sent.", ct);
                return;
            }

            var rendered = renderer.Render(document, subscription.Format);
            var result = await delivery.SendAsync(subscription.RecipientAddresses(), document, rendered, bu, ct);

            switch (result.Outcome)
            {
                case SlaSendOutcome.Sent:
                    await SlaSweepWorker.SettleClaimAsync(db, claim, SlaEventStatuses.Sent, null,
                        result.Provider, result.AcceptanceReference, ct);
                    await CompleteAsync(db, subscription, occurrence, now, ReportRunOutcomes.Delivered,
                        $"Sent {rendered.FileName} to {subscription.RecipientAddresses().Length} recipient(s).", ct);
                    return;

                case SlaSendOutcome.Uncertain:
                    // The claim is KEPT. The provider may already hold the report, and a second copy
                    // of a board report is not recoverable from the recipient's inbox. The run is
                    // still recorded as failed so a human can see it and resend by hand.
                    await SlaSweepWorker.SettleClaimAsync(db, claim, SlaEventStatuses.Uncertain,
                        result.Reason, null, null, ct);
                    await CompleteAsync(db, subscription, occurrence, now, ReportRunOutcomes.Failed,
                        $"Delivery is uncertain and will NOT be retried automatically: {result.Reason} "
                        + "Check the mail provider before resending by hand.", ct);
                    return;

                default:
                    // The provider demonstrably never had it. Release the claim so the next cycle
                    // retries, and leave the failure visible on the subscription rather than only in
                    // a log line nobody is reading.
                    await SlaSweepWorker.ReleaseEventClaimAsync(db, claim, ct);
                    await CompleteAsync(db, subscription, occurrence, now, ReportRunOutcomes.Failed,
                        $"The report was not sent: {result.Reason} It will be retried.", ct);
                    return;
            }
        }
        // RELEASING on an escaped exception is only safe because it cannot have come from the
        // transport: IReportDelivery never throws — it converts a transport fault into an Uncertain
        // result, which is handled above and KEEPS the claim. Anything reaching these handlers
        // failed while building or rendering, before a byte could have left, so a retry cannot
        // produce a duplicate. If delivery is ever changed to throw, these two handlers must settle
        // UNCERTAIN instead.
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await SlaSweepWorker.ReleaseEventClaimAsync(db, claim, ct);
            throw;
        }
        catch (Exception ex)
        {
            await SlaSweepWorker.ReleaseEventClaimAsync(db, claim, ct);
            _log.LogError(ex, "BU {Bu}: report subscription {Id} ({Key}) failed.", bu, subscription.Id,
                subscription.ReportKey);
            await CompleteAsync(db, subscription, occurrence, now, ReportRunOutcomes.Failed,
                Truncate(ex.Message, 1000), ct);
        }
    }

    /// <summary>
    /// Advances the schedule from the OCCURRENCE, then forward past <paramref name="now"/>.
    ///
    /// <para><b>From the occurrence</b> so the slot does not drift: advancing from the run time
    /// would let a worker that started fourteen minutes late shift a 06:00 report to 06:14, and keep
    /// shifting it every day after that.</para>
    ///
    /// <para><b>Past now</b> so a schedule that has been dormant — a paused instance, a restored
    /// backup, a tenant reinstated after suspension — delivers the current period ONCE and resumes,
    /// instead of replaying every occurrence it missed. Backfilling a quarter of daily reports into
    /// a director's inbox is how the whole channel gets filtered, and the real report goes with it.
    /// The loop preserves the configured hour and weekday because each step is the same cadence
    /// arithmetic.</para>
    /// </summary>
    internal static DateTime AdvanceSchedule(ReportSubscription subscription, DateTime occurrence, DateTime now)
    {
        var next = ReportSubscriptionService.NextOccurrence(subscription, occurrence);
        while (next <= now) next = ReportSubscriptionService.NextOccurrence(subscription, next);
        return next;
    }

    private static async Task CompleteAsync(ErpRfqAutomationContext db, ReportSubscription subscription,
        DateTime occurrence, DateTime now, string outcome, string detail, CancellationToken ct)
    {
        subscription.LastRunOn = now;
        subscription.LastRunOutcome = outcome;
        subscription.LastRunDetail = detail;
        subscription.NextRunOn = AdvanceSchedule(subscription, occurrence, now);
        await db.SaveChangesAsync(ct);
    }

    private static async Task<string> ResolveTenantLabelAsync(ErpRfqAutomationContext db, long bu,
        CancellationToken ct)
    {
        var name = await db.BusinessUnits.AsNoTracking()
            .Where(b => b.Id == bu)
            .Select(b => b.BusinessUnitName)
            .FirstOrDefaultAsync(ct);
        return string.IsNullOrWhiteSpace(name) ? $"Business unit {bu}" : name;
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max];
}
