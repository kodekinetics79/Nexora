using ERP_RFQ_Automation.AI;
using ERP_RFQ_Automation.Extraction;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Platform.Auth;
using ERP_RFQ_Automation.Platform.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace ERP_RFQ_Automation.Platform.Controllers;

[ApiController]
[Route("api/platform/overview")]
[Authorize(Policy = PlatformPolicies.PlatformScope)]
public class OverviewController(
    ErpRfqAutomationContext context,
    HealthCheckService healthChecks) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var monthStart = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var priorPeriodStart = monthStart.AddMonths(-1);
        var priorPeriodEnd = priorPeriodStart.Add(now - monthStart);
        var seriesStart = now.Date.AddDays(-13);
        var activeStatuses = new[]
        {
            ExtractionStatus.Leased,
            ExtractionStatus.Extracting,
            ExtractionStatus.Persisting
        };
        var terminalStatuses = new[]
        {
            ExtractionStatus.Succeeded,
            ExtractionStatus.Failed,
            ExtractionStatus.DeadLetter
        };

        var tenants = await context.Set<Tenant>().IgnoreQueryFilters().AsNoTracking()
            .Include(t => t.Plan)
            .ToListAsync(ct);
        var jobs = context.Set<ExtractionJob>().IgnoreQueryFilters().AsNoTracking();
        var terminalCount = await jobs.CountAsync(j => terminalStatuses.Contains(j.Status), ct);
        var succeededCount = await jobs.CountAsync(j => j.Status == ExtractionStatus.Succeeded, ct);

        var throughputRaw = await jobs
            .Where(j => j.CreatedOn >= seriesStart)
            .GroupBy(j => j.CreatedOn.Date)
            .Select(g => new
            {
                Date = g.Key,
                Docs = g.Count(j => j.Status == ExtractionStatus.Succeeded),
                Failures = g.Count(j => j.Status == ExtractionStatus.Failed || j.Status == ExtractionStatus.DeadLetter)
            })
            .ToListAsync(ct);
        var throughputMap = throughputRaw.ToDictionary(x => x.Date);

        var costRows = await context.Set<AiRequest>().IgnoreQueryFilters().AsNoTracking()
            .Where(r => r.CreatedOn >= seriesStart && r.CostCurrency == "USD" && r.EstimatedCost != null)
            .GroupBy(r => r.CreatedOn.Date)
            .Select(g => new { Date = g.Key, Cost = g.Sum(r => r.EstimatedCost!.Value) })
            .ToListAsync(ct);
        var costMap = costRows.ToDictionary(x => x.Date, x => x.Cost);
        var currentCost = await context.Set<AiRequest>().IgnoreQueryFilters().AsNoTracking()
            .Where(r => r.CreatedOn >= monthStart && r.CostCurrency == "USD" && r.EstimatedCost != null)
            .SumAsync(r => r.EstimatedCost ?? 0m, ct);
        var priorCost = await context.Set<AiRequest>().IgnoreQueryFilters().AsNoTracking()
            .Where(r => r.CreatedOn >= priorPeriodStart && r.CreatedOn < priorPeriodEnd &&
                r.CostCurrency == "USD" && r.EstimatedCost != null)
            .SumAsync(r => r.EstimatedCost ?? 0m, ct);

        var health = await healthChecks.CheckHealthAsync(
            registration => registration.Tags.Contains("ready"), ct);
        var services = health.Entries.OrderBy(entry => entry.Key).Select(entry => new
        {
            key = entry.Key,
            name = Humanize(entry.Key),
            status = entry.Value.Status switch
            {
                HealthStatus.Healthy => "healthy",
                HealthStatus.Degraded => "degraded",
                _ => "down"
            },
            latencyMs = Math.Round(entry.Value.Duration.TotalMilliseconds),
            detail = string.IsNullOrWhiteSpace(entry.Value.Description)
                ? entry.Value.Status.ToString()
                : entry.Value.Description
        });

        return Ok(new
        {
            tenantCount = tenants.Count,
            activeTenants = tenants.Count(t => t.Status == TenantStatus.Active),
            docsProcessedMtd = await jobs.CountAsync(
                j => j.UpdatedOn >= monthStart && j.Status == ExtractionStatus.Succeeded, ct),
            extractionSuccessRate = terminalCount == 0 ? 0d : (double)succeededCount / terminalCount,
            queueDepth = await jobs.CountAsync(j => j.Status == ExtractionStatus.Pending, ct),
            inFlight = await jobs.CountAsync(j => activeStatuses.Contains(j.Status), ct),
            deadLetter = await jobs.CountAsync(j => j.Status == ExtractionStatus.DeadLetter, ct),
            llmCostMtdUsd = currentCost,
            llmCostTrendPct = priorCost > 0
                ? (double?)((currentCost - priorCost) / priorCost * 100m)
                : null,
            seatsInUse = await context.Set<User>().IgnoreQueryFilters().CountAsync(u => u.IsActive == true, ct),
            services,
            throughput = Enumerable.Range(0, 14).Select(i =>
            {
                var date = seriesStart.AddDays(i);
                throughputMap.TryGetValue(date, out var row);
                return new { date = date.ToString("yyyy-MM-dd"), docs = row?.Docs ?? 0, failures = row?.Failures ?? 0 };
            }),
            costTrend = Enumerable.Range(0, 14).Select(i =>
            {
                var date = seriesStart.AddDays(i);
                return new { date = date.ToString("yyyy-MM-dd"), costUsd = costMap.GetValueOrDefault(date) };
            }),
            tenantsByPlan = new[] { "free", "pro", "enterprise" }.Select(tier => new
            {
                tier,
                count = tenants.Count(t => NormalizePlanTier(t.Plan?.Code) == tier)
            })
        });
    }

    private static string NormalizePlanTier(string? code)
    {
        var normalized = (code ?? "pro").Trim().ToLowerInvariant();
        return normalized is "free" or "pro" or "enterprise" ? normalized : "pro";
    }

    private static string Humanize(string value)
        => string.Join(' ', value.Split('-', StringSplitOptions.RemoveEmptyEntries)
            .Select(word => char.ToUpperInvariant(word[0]) + word[1..]));
}
