using ERP_RFQ_Automation.HealthChecks;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Platform.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ERP_RFQ_Automation.Billing;

/// <summary>What one sweep did. Returned so a test can drive a single pass and assert on it.</summary>
public sealed record BillingRunSummary(
    int TenantsSwept,
    int StatementsComputed,
    int TenantsAtRevenueRisk,
    int ExpiredTrials,
    int Failures)
{
    /// <summary>Tenants consuming the platform under terms nobody set (see <see cref="CommercialConfigurationStates"/>).</summary>
    public int TenantsNeedingCommercialConfiguration { get; init; }
}

/// <summary>
/// The scheduled billing run: sweeps every non-Archived tenant and computes the current
/// period's Draft, plus the prior period's until it is finalized.
///
/// <para><b>Why it exists.</b> <c>ComputeStatement</c> was reachable only from
/// <c>PlatformBillingController</c> — a human clicking in the console. Nothing in the
/// system billed anything on its own, so an environment where nobody clicked served every
/// tenant for free and reported no error while doing it. A revenue system whose only
/// trigger is a person remembering to press a button is not a revenue system.</para>
///
/// <para><b>Scale-out safety.</b> Two instances sweeping the same tenant-period is
/// expected and safe, and the guarantee is in the DATABASE, not in this loop:
/// <c>UX_BillingStatements_Tenant_PeriodStart</c> (one statement per tenant per period)
/// makes the second INSERT impossible, and <c>ComputeStatementAsync</c> catches that
/// unique violation, plus the <c>Version</c> concurrency conflict a rival recompute
/// raises, and folds into the winning row. No lease, no advisory lock, no leader
/// election — none of which would be as strong as the constraint already is.</para>
///
/// <para><b>No tenant scope, deliberately — the opposite of <c>SlaSweepWorker</c>.</b>
/// That worker pushes a tenant scope per business unit because it reads tenant tables and
/// must run under RLS. This one is a PLATFORM-plane job: it reads platform.Tenants and
/// aggregates every tenant's ledgers through <c>IgnoreQueryFilters</c>. Pushing a tenant
/// scope would route its commands to <c>nexora_tenant_app</c>, where RLS would hide the
/// very rows the meters are counting and every tenant would silently meter as zero — a
/// worker that runs green and bills nothing. With no scope the command interceptor hands
/// it <c>nexora_pipeline_app</c>, which is what a cross-tenant platform read needs.</para>
///
/// <para>Error containment follows the ExtractionWorker/SlaSweepWorker discipline: one
/// tenant's failure never blocks the rest, and the loop never dies.</para>
/// </summary>
public sealed class BillingRunWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOptionsMonitor<BillingRunOptions> _options;
    private readonly IBackgroundWorkerHeartbeats? _heartbeats;
    private readonly ILogger<BillingRunWorker> _log;
    private readonly string _workerId;

    public BillingRunWorker(
        IServiceScopeFactory scopeFactory,
        IOptionsMonitor<BillingRunOptions> options,
        ILogger<BillingRunWorker> log,
        IBackgroundWorkerHeartbeats? heartbeats = null)
    {
        _scopeFactory = scopeFactory;
        _options = options;
        _log = log;
        _heartbeats = heartbeats;
        _workerId = $"{Environment.MachineName}:{Environment.ProcessId}";

        // Registered in the CONSTRUCTOR so a worker that faults before it ever reaches its
        // loop still fails readiness once the startup grace expires — the failure mode that
        // BackgroundServiceExceptionBehavior.Ignore otherwise hides completely.
        //
        // Registered only when ENABLED, because the health check treats an unregistered
        // worker as not-expected-to-run: a deliberately disabled billing run must not hold
        // /ready red forever. The startup grace covers the configured initial delay plus the
        // first sweep, so a slow first pass over a large fleet is not read as a dead loop.
        var options0 = options.CurrentValue;
        if (options0.Enabled)
            _heartbeats?.Register(
                BackgroundWorkerNames.BillingRun,
                options0.Interval,
                options0.InitialDelay + TimeSpan.FromMinutes(5));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.CurrentValue.Enabled)
        {
            // Loud on purpose: with this off, nothing in the deployment bills anything.
            _log.LogWarning(
                "BillingRunWorker is DISABLED by configuration ({Section}:Enabled=false). No statement will be computed "
                + "unless a human clicks Compute in the platform console.", BillingRunOptions.SectionName);
            return;
        }

        _log.LogInformation(
            "BillingRunWorker {WorkerId} starting; interval {Interval}, prior-period catch-up {CatchUp}.",
            _workerId, _options.CurrentValue.Interval, _options.CurrentValue.CatchUpPriorPeriod);

        try { await Task.Delay(_options.CurrentValue.InitialDelay, stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var summary = await SweepOnceAsync(stoppingToken);
                _log.LogInformation(
                    "Billing run swept {Tenants} tenant(s): {Computed} statement(s) computed, {AtRisk} tenant(s) at revenue "
                    + "risk, {NeedConfiguration} needing commercial configuration, {ExpiredTrials} expired trial(s), "
                    + "{Failures} failure(s).",
                    summary.TenantsSwept, summary.StatementsComputed, summary.TenantsAtRevenueRisk,
                    summary.TenantsNeedingCommercialConfiguration, summary.ExpiredTrials, summary.Failures);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // The run must never die on an unexpected error (e.g. a transient DB
                // issue): a dead billing loop is indistinguishable from a healthy one
                // until the month closes and there is nothing to invoice.
                _log.LogError(ex, "Billing run iteration failed; will retry next interval.");
            }

            // Beat AFTER the iteration, failed or not: the heartbeat asserts the loop is
            // alive, and a loop that keeps failing is caught by the error logs, not by
            // flipping /ready red on one transient DB blip.
            _heartbeats?.Beat(BackgroundWorkerNames.BillingRun, _options.CurrentValue.Interval);

            var options = _options.CurrentValue;
            var delay = options.Interval + RandomJitter(options.MaximumJitter);
            try { await Task.Delay(delay, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }

        _log.LogInformation("BillingRunWorker {WorkerId} stopped.", _workerId);
    }

    /// <summary>
    /// Resolves the working set once, then computes per tenant in its own DI scope so one
    /// tenant's DbContext state can never contaminate the next one's compute.
    /// </summary>
    internal async Task<BillingRunSummary> SweepOnceAsync(CancellationToken ct)
    {
        var nowUtc = DateTime.UtcNow;
        var currentPeriod = BillingPeriod.Containing(nowUtc);
        var priorPeriod = BillingPeriod.Containing(currentPeriod.StartUtc.AddDays(-1));

        var tenantIds = await ResolveBillableTenantIdsAsync(ct);
        int computed = 0, atRisk = 0, expiredTrials = 0, failures = 0, needConfiguration = 0;

        foreach (var tenantId in tenantIds)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var outcome = await SweepTenantAsync(
                    tenantId, currentPeriod, priorPeriod, _options.CurrentValue.CatchUpPriorPeriod, nowUtc, ct);
                computed += outcome.Computed;
                if (outcome.AtRisk) atRisk++;
                if (outcome.TrialExpired) expiredTrials++;
                if (outcome.NeedsCommercialConfiguration) needConfiguration++;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (BillingConflictException ex)
            {
                // A tenant that cannot be priced at all — no active rate card, a dangling
                // pin, a legacy non-USD card. It produces NO statement, which is free
                // service, so it is a warning with the cause named rather than a debug line.
                failures++;
                _log.LogWarning(
                    "REVENUE RISK: no statement could be computed for tenant {TenantId} in period {Period}: {Reason}",
                    tenantId, currentPeriod.Key, ex.Message);
            }
            catch (Exception ex)
            {
                // One misbehaving tenant must never block the others.
                failures++;
                _log.LogError(ex, "Billing run failed for tenant {TenantId}; continuing with the next tenant.", tenantId);
            }
        }

        return new BillingRunSummary(tenantIds.Count, computed, atRisk, expiredTrials, failures)
        {
            TenantsNeedingCommercialConfiguration = needConfiguration
        };
    }

    /// <summary>
    /// Every tenant the platform should be billing. Archived tenants are offboarded — they
    /// consume nothing and charging them would be wrong — so they are the only exclusion:
    /// Suspended tenants still hold data and Provisioning tenants can already be metering,
    /// and both need a statement and a revenue-risk verdict like anyone else.
    /// </summary>
    private async Task<IReadOnlyList<long>> ResolveBillableTenantIdsAsync(CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ErpRfqAutomationContext>();
        return await db.Set<Tenant>().IgnoreQueryFilters().AsNoTracking()
            .Where(t => t.Status != TenantStatus.Archived)
            .OrderBy(t => t.Id)
            .Select(t => t.Id)
            .ToListAsync(ct);
    }

    private async Task<(int Computed, bool AtRisk, bool TrialExpired, bool NeedsCommercialConfiguration)> SweepTenantAsync(
        long tenantId, BillingPeriod currentPeriod, BillingPeriod priorPeriod,
        bool catchUpPriorPeriod, DateTime nowUtc, CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ErpRfqAutomationContext>();
        var billing = scope.ServiceProvider.GetRequiredService<IBillingStatementService>();

        var current = await billing.ComputeStatementAsync(tenantId, currentPeriod, rateCardId: null, ct);
        var computed = 1;

        // The prior period keeps changing after it closes (late AI reconciliation,
        // dead-letter re-drives), and a Final statement is immutable, so it is recomputed
        // until somebody freezes it. ComputeStatementAsync returns a Final row untouched,
        // which makes the "until it is finalized" condition the service's job, not a
        // status check here that could race a concurrent finalize.
        if (catchUpPriorPeriod)
        {
            await billing.ComputeStatementAsync(tenantId, priorPeriod, rateCardId: null, ct);
            computed++;
        }

        var tenant = await db.Set<Tenant>().IgnoreQueryFilters().AsNoTracking()
            .Include(t => t.Plan)
            .FirstAsync(t => t.Id == tenantId, ct);
        var reasons = RevenueLeakEvaluator.Evaluate(tenant, tenant.Plan, current, nowUtc);
        var trialExpired = RevenueLeakEvaluator.IsTrialExpired(tenant, nowUtc);
        var configurationState = CommercialConfigurationStates.For(tenant);
        var needsConfiguration = CommercialConfigurationStates.IsRequired(configurationState);

        if (needsConfiguration)
            // The remediation signal for tenants that predate the provisioning rules, or
            // that were written out-of-band: the console badges them, the entitlement floor
            // bounds them, and this line makes them findable in the logs of any environment
            // whose console nobody has opened yet.
            _log.LogWarning(
                "COMMERCIAL CONFIGURATION REQUIRED: tenant {TenantId} ('{Slug}', mode {BillingMode}) is {State}. "
                + "It is consuming the platform under terms nobody set; resolve it on the platform console's billing "
                + "screen (assign a plan, or record the exemption reason).",
                tenantId, tenant.Slug, tenant.BillingMode, configurationState);

        if (trialExpired)
            // Deliberately NOT a suspension. Cutting a customer off is a commercial
            // decision with a relationship attached; a background job's job is to make
            // sure a human is looking, not to make the call for them.
            _log.LogWarning(
                "TRIAL EXPIRED: tenant {TenantId} ('{Slug}') has been in Trial billing mode since its trial ended on "
                + "{TrialEndsOn:yyyy-MM-dd} and is still charged nothing. The account needs converting; the billing run "
                + "does not suspend tenants.",
                tenantId, tenant.Slug, tenant.TrialEndsOn);

        if (reasons.Count > 0)
            _log.LogWarning(
                "REVENUE RISK: tenant {TenantId} ('{Slug}', mode {BillingMode}) — {Reasons}. Latest statement for {Period} "
                + "totals {Total} {Currency}.",
                tenantId, tenant.Slug, tenant.BillingMode, string.Join(", ", reasons),
                currentPeriod.Key, current.TotalAmount, current.Currency);

        return (computed, reasons.Count > 0, trialExpired, needsConfiguration);
    }

    private static TimeSpan RandomJitter(TimeSpan maximum)
        => maximum <= TimeSpan.Zero
            ? TimeSpan.Zero
            : TimeSpan.FromMilliseconds(Random.Shared.NextDouble() * maximum.TotalMilliseconds);
}
