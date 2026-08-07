using ERP_RFQ_Automation.HealthChecks;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.MultiTenancy;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.CommercialRouting;

/// <summary>
/// Re-routes leads that ended up with no assignment and no routing decision.
///
/// TENANT SCOPE IS MANDATORY (was not, before). Without an HttpContext the RLS
/// interceptor grants this worker the BYPASSRLS <c>nexora_pipeline_app</c> role, and
/// a null tenant turns every EF global filter into a no-op — so the previous
/// implementation's <c>IgnoreQueryFilters()</c> + hand-written BusinessUnitId
/// predicate was the ONLY thing standing between the reconciliation batch and a
/// cross-tenant read. Now the batch resolves the affected tenants first (the only
/// query that runs under the pipeline role, and it returns nothing but tenant ids)
/// and does all candidate selection and routing inside a pushed tenant scope, i.e.
/// as <c>nexora_tenant_app</c> under RLS with the EF filters live.
/// </summary>
public sealed class RoutingReconciliationWorker : BackgroundService
{
    private static readonly TimeSpan Period = TimeSpan.FromMinutes(1);
    private const int PerTenantBatchSize = 100;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ITenantScopeAccessor _tenantScope;
    private readonly IBackgroundWorkerHeartbeats? _heartbeats;
    private readonly ILogger<RoutingReconciliationWorker> _logger;

    public RoutingReconciliationWorker(
        IServiceScopeFactory scopeFactory,
        ITenantScopeAccessor tenantScope,
        ILogger<RoutingReconciliationWorker> logger,
        IBackgroundWorkerHeartbeats? heartbeats = null)
    {
        _scopeFactory = scopeFactory;
        _tenantScope = tenantScope;
        _logger = logger;
        _heartbeats = heartbeats;
        _heartbeats?.Register(BackgroundWorkerNames.RoutingReconciliation, Period);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _heartbeats?.Beat(BackgroundWorkerNames.RoutingReconciliation, Period);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ReconcileBatchAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Commercial-routing reconciliation batch failed.");
            }

            _heartbeats?.Beat(BackgroundWorkerNames.RoutingReconciliation, Period);

            try { await Task.Delay(Period, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    internal async Task<int> ReconcileBatchAsync(CancellationToken ct)
    {
        var businessUnits = await ResolveTenantsWithUnroutedLeadsAsync(ct);

        var completed = 0;
        foreach (var businessUnitId in businessUnits)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                completed += await ReconcileTenantAsync(businessUnitId, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Routing reconciliation failed for BU {BusinessUnitId}; continuing with next tenant.",
                    businessUnitId);
            }
        }

        return completed;
    }

    /// <summary>
    /// The ONLY query that runs without a tenant scope. It projects tenant ids and
    /// nothing else, so the bypass role can never surface another tenant's data.
    ///
    /// <para>Suspended and archived tenants are dropped HERE, in the one scope that has no tenant
    /// pushed — a hard requirement of the gate, whose platform read is refused at column level
    /// under the RLS tenant role and then fails OPEN (see <c>ITenantWorkGate</c>). Routing a
    /// suspended tenant's leads assigns them to owners who cannot sign in, and notifies them.</para>
    ///
    /// <para>Skipping is pure DEFERRAL and cannot lose a lead: the selection above IS the
    /// definition of unfinished work — no <c>AssignTo</c>, no <c>LeadRoutingDecision</c> — so a
    /// lead not routed this cycle still matches on the next one and is routed on the first cycle
    /// after reinstatement, however long that takes.</para>
    /// </summary>
    private async Task<IReadOnlyList<long>> ResolveTenantsWithUnroutedLeadsAsync(CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ErpRfqAutomationContext>();

        var businessUnits = await db.Leads.IgnoreQueryFilters().AsNoTracking()
            .Where(l => l.AssignTo == null)
            .Where(l => !db.Set<LeadRoutingDecision>().IgnoreQueryFilters().Any(d =>
                d.BusinessUnitId == l.BusinessUnitId && d.LeadId == l.Id))
            .Select(l => l.BusinessUnitId)
            .Distinct()
            .OrderBy(id => id)
            .ToListAsync(ct);

        // Resolved from THIS scope rather than injected: the worker is a singleton and the gate is
        // scoped, so a constructor dependency would fail startup scope validation.
        var gate = scope.ServiceProvider
            .GetService<ERP_RFQ_Automation.Platform.Lifecycle.ITenantWorkGate>();
        if (gate is null || businessUnits.Count == 0) return businessUnits;

        return await gate.FilterServiceableAsync(businessUnits, ct);
    }

    private async Task<int> ReconcileTenantAsync(long businessUnitId, CancellationToken ct)
    {
        using var tenant = _tenantScope.Push(businessUnitId);
        await using var scope = _scopeFactory.CreateAsyncScope();

        var db = scope.ServiceProvider.GetRequiredService<ErpRfqAutomationContext>();
        var routing = scope.ServiceProvider.GetRequiredService<ICommercialRoutingApplicationService>();

        // Fail closed: if the scope did not take, every query below would run
        // cross-tenant under the bypass role again.
        if (db.ScopedTenantId != businessUnitId)
        {
            throw new InvalidOperationException(
                $"Routing reconciliation refused to run for BU {businessUnitId}: the DbContext resolved tenant " +
                $"{db.ScopedTenantId?.ToString() ?? "<none>"}. Tenant scope is mandatory for this worker.");
        }

        var candidates = await db.Leads.AsNoTracking()
            .Where(l => l.BusinessUnitId == businessUnitId && l.AssignTo == null)
            .Where(l => !db.Set<LeadRoutingDecision>().Any(d =>
                d.BusinessUnitId == l.BusinessUnitId && d.LeadId == l.Id))
            .OrderBy(l => l.CreatedDate)
            .ThenBy(l => l.Id)
            .Select(l => new { l.Id, l.BusinessUnitId })
            .Take(PerTenantBatchSize)
            .ToListAsync(ct);

        var completed = 0;
        foreach (var lead in candidates)
        {
            try
            {
                await routing.RouteLeadAsync(lead.BusinessUnitId, new RouteLeadCommand(
                    lead.Id,
                    $"reconcile:lead:{lead.Id}:route:v1",
                    $"routing-reconciliation:{lead.BusinessUnitId}"), ct);
                completed++;
            }
            catch (RoutingConflictException)
            {
                // A foreground assignment won the race; no reconciliation is needed.
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not reconcile routing for lead {LeadId}.", lead.Id);
            }
        }

        return completed;
    }
}
