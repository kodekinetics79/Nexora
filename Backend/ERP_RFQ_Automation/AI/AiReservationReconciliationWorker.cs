using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.MultiTenancy;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.AI;

public interface IAiReservationReconciler
{
    Task<int> ReconcileAsync(DateTime staleBeforeUtc, CancellationToken ct);
}

public sealed class AiReservationReconciler : IAiReservationReconciler
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ITenantScopeAccessor _tenantScope;

    public AiReservationReconciler(IServiceScopeFactory scopeFactory, ITenantScopeAccessor tenantScope)
    {
        _scopeFactory = scopeFactory;
        _tenantScope = tenantScope;
    }

    /// <summary>
    /// Settles AI reservations that were taken and never completed, across every business unit.
    ///
    /// <para><b>This sweep deliberately does NOT consult <c>ITenantWorkGate</c>, and that is not an
    /// oversight.</b> It looks like an AI path and is the opposite of one: it calls no provider and
    /// spends no tokens. It is the janitor that hands a leaked reservation BACK, moving
    /// <c>ReservedTokens</c> into <c>SettledTokens</c> on the budget period.</para>
    ///
    /// <para>Gating it on tenant status would make suspension actively harmful in two directions.
    /// A tenant is usually suspended mid-flight, which is exactly when reservations are left
    /// dangling — so the suspended tenants are the ones with the most to reconcile. Skip them and
    /// their <c>ReservedTokens</c> stays inflated for the whole suspension, so on reinstatement the
    /// budget reports headroom it does not have and the tenant hits its hard limit on the first
    /// call. Worse, <c>SettledTokens</c> is what the usage meter bills from: an unsettled
    /// reservation is consumption that was incurred and never charged. Withholding this work to
    /// save a suspended tenant money would UNDER-bill them and corrupt the ledger that closes their
    /// final invoice.</para>
    ///
    /// <para>What it costs to leave ungated is a per-cycle scan of business units every two
    /// minutes. That is the right trade against a wrong budget.</para>
    /// </summary>
    public async Task<int> ReconcileAsync(DateTime staleBeforeUtc, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ErpRfqAutomationContext>();
        var businessUnitIds = await db.BusinessUnits.IgnoreQueryFilters().AsNoTracking()
            .OrderBy(b => b.Id)
            .Select(b => b.Id)
            .ToListAsync(ct);
        var staleRequests = new List<(long BusinessUnitId, Guid RequestId)>();
        foreach (var businessUnitId in businessUnitIds)
        {
            using var tenant = _tenantScope.Push(businessUnitId);
            using var tenantScope = _scopeFactory.CreateScope();
            var tenantDb = tenantScope.ServiceProvider.GetRequiredService<ErpRfqAutomationContext>();
            var remaining = 100 - staleRequests.Count;
            var ids = await tenantDb.AiRequests.AsNoTracking()
                .Where(r => r.CompletedOn == null
                         && (r.Status == AiCallStatuses.Reserved || r.Status == AiCallStatuses.Running)
                         && r.CreatedOn < staleBeforeUtc)
                .OrderBy(r => r.CreatedOn)
                .Select(r => r.Id)
                .Take(remaining)
                .ToListAsync(ct);
            staleRequests.AddRange(ids.Select(id => (businessUnitId, id)));
            if (staleRequests.Count == 100)
                break;
        }

        var reconciled = 0;
        foreach (var (businessUnitId, requestId) in staleRequests)
        {
            using var tenant = _tenantScope.Push(businessUnitId);
            using var operationScope = _scopeFactory.CreateScope();
            var operationDb = operationScope.ServiceProvider.GetRequiredService<ErpRfqAutomationContext>();
            var strategy = operationDb.Database.CreateExecutionStrategy();
            reconciled += await strategy.ExecuteAsync(async () =>
            {
                using var retryScope = _scopeFactory.CreateScope();
                var retryDb = retryScope.ServiceProvider.GetRequiredService<ErpRfqAutomationContext>();
                await using var tx = await retryDb.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, ct);
                var request = await retryDb.AiRequests.IgnoreQueryFilters()
                    .SingleOrDefaultAsync(r => r.Id == requestId, ct);
                if (request is null || request.CompletedOn is not null
                    || request.Status is not (AiCallStatuses.Reserved or AiCallStatuses.Running))
                    return 0;

                var period = new DateTime(request.CreatedOn.Year, request.CreatedOn.Month, 1, 0, 0, 0, DateTimeKind.Utc);
                var budget = await retryDb.AiBudgetPeriods.IgnoreQueryFilters().SingleAsync(
                    b => b.BusinessUnitId == request.BusinessUnitId && b.PeriodStartUtc == period, ct);
                var conservativeTokens = Math.Max(0, request.ReservedTokens);
                var hasAttempts = await retryDb.AiCallAttempts.IgnoreQueryFilters()
                    .AnyAsync(a => a.RequestId == request.Id, ct);
                var completedOn = DateTime.UtcNow;
                if (!hasAttempts)
                {
                    retryDb.AiCallAttempts.Add(new AiCallAttempt
                    {
                        RequestId = request.Id,
                        BusinessUnitId = request.BusinessUnitId,
                        AttemptNumber = 1,
                        Provider = request.Provider,
                        Model = request.Model,
                        Status = AiCallStatuses.Unknown,
                        InputTokens = conservativeTokens,
                        OutputTokens = 0,
                        TokenSource = AiTokenSources.Estimated,
                        ErrorCode = "stale_reservation_reconciled",
                        StartedOn = request.StartedOn ?? request.CreatedOn,
                        CompletedOn = completedOn
                    });
                }
                request.Status = AiCallStatuses.Unknown;
                request.InputTokens = conservativeTokens;
                request.OutputTokens = 0;
                request.TokenSource = AiTokenSources.Estimated;
                request.ErrorCode = "stale_reservation_reconciled";
                request.CompletedOn = completedOn;
                budget.ReservedTokens = Math.Max(0, budget.ReservedTokens - conservativeTokens);
                budget.SettledTokens = checked(budget.SettledTokens + conservativeTokens);
                budget.Version++;
                budget.UpdatedOn = DateTime.UtcNow;
                await retryDb.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
                return 1;
            });
        }
        return reconciled;
    }
}

public sealed class AiReservationReconciliationWorker : BackgroundService
{
    private readonly IAiReservationReconciler _reconciler;
    private readonly ERP_RFQ_Automation.HealthChecks.IBackgroundWorkerHeartbeats? _heartbeats;
    private readonly ILogger<AiReservationReconciliationWorker> _log;
    private readonly TimeSpan _staleAfter;
    private readonly TimeSpan _interval;

    public AiReservationReconciliationWorker(
        IAiReservationReconciler reconciler,
        IConfiguration configuration,
        ILogger<AiReservationReconciliationWorker> log,
        ERP_RFQ_Automation.HealthChecks.IBackgroundWorkerHeartbeats? heartbeats = null)
    {
        _reconciler = reconciler;
        _log = log;
        _heartbeats = heartbeats;
        _staleAfter = TimeSpan.FromMinutes(Math.Clamp(
            configuration.GetValue("AiGovernance:ReservationStaleAfterMinutes", 15), 5, 1440));
        _interval = TimeSpan.FromMinutes(Math.Clamp(
            configuration.GetValue("AiGovernance:ReconciliationIntervalMinutes", 2), 1, 60));
        // Without this the worker could fault once (BackgroundServiceExceptionBehavior
        // is Ignore) and stay dead for the process lifetime while /ready stayed green;
        // AI budget reservations would then leak indefinitely.
        _heartbeats?.Register(
            ERP_RFQ_Automation.HealthChecks.BackgroundWorkerNames.AiReservationReconciliation, _interval);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _heartbeats?.Beat(
            ERP_RFQ_Automation.HealthChecks.BackgroundWorkerNames.AiReservationReconciliation, _interval);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var count = await _reconciler.ReconcileAsync(DateTime.UtcNow - _staleAfter, stoppingToken);
                if (count > 0)
                    _log.LogWarning("Reconciled {Count} stale AI reservation(s) as unknown usage.", count);
                _heartbeats?.Beat(
                    ERP_RFQ_Automation.HealthChecks.BackgroundWorkerNames.AiReservationReconciliation, _interval);
                await Task.Delay(_interval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "AI reservation reconciliation failed; retrying.");
                _heartbeats?.Beat(
                    ERP_RFQ_Automation.HealthChecks.BackgroundWorkerNames.AiReservationReconciliation, _interval);
                await Task.Delay(_interval, stoppingToken);
            }
        }
    }
}
