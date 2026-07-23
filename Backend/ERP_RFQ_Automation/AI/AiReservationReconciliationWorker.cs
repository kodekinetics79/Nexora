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
    private readonly ILogger<AiReservationReconciliationWorker> _log;
    private readonly TimeSpan _staleAfter;
    private readonly TimeSpan _interval;

    public AiReservationReconciliationWorker(
        IAiReservationReconciler reconciler,
        IConfiguration configuration,
        ILogger<AiReservationReconciliationWorker> log)
    {
        _reconciler = reconciler;
        _log = log;
        _staleAfter = TimeSpan.FromMinutes(Math.Clamp(
            configuration.GetValue("AiGovernance:ReservationStaleAfterMinutes", 15), 5, 1440));
        _interval = TimeSpan.FromMinutes(Math.Clamp(
            configuration.GetValue("AiGovernance:ReconciliationIntervalMinutes", 2), 1, 60));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var count = await _reconciler.ReconcileAsync(DateTime.UtcNow - _staleAfter, stoppingToken);
                if (count > 0)
                    _log.LogWarning("Reconciled {Count} stale AI reservation(s) as unknown usage.", count);
                await Task.Delay(_interval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "AI reservation reconciliation failed; retrying.");
                await Task.Delay(_interval, stoppingToken);
            }
        }
    }
}
