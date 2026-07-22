using ERP_RFQ_Automation.Models;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.CommercialRouting;

public sealed class RoutingReconciliationWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<RoutingReconciliationWorker> _logger;

    public RoutingReconciliationWorker(
        IServiceScopeFactory scopeFactory,
        ILogger<RoutingReconciliationWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
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

            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
        }
    }

    internal async Task<int> ReconcileBatchAsync(CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ErpRfqAutomationContext>();
        var routing = scope.ServiceProvider.GetRequiredService<ICommercialRoutingApplicationService>();
        var candidates = await db.Leads.IgnoreQueryFilters().AsNoTracking()
            .Where(l => l.AssignTo == null)
            .Where(l => !db.Set<LeadRoutingDecision>().IgnoreQueryFilters().Any(d =>
                d.BusinessUnitId == l.BusinessUnitId && d.LeadId == l.Id))
            .OrderBy(l => l.CreatedDate)
            .ThenBy(l => l.Id)
            .Select(l => new { l.Id, l.BusinessUnitId })
            .Take(100)
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
