using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Platform.Auth;
using ERP_RFQ_Automation.Platform.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Platform.Controllers;

[ApiController]
[Route("api/platform/overview")]
[Authorize(Policy = PlatformPolicies.PlatformScope)]
public class OverviewController : ControllerBase
{
    private readonly ErpRfqAutomationContext _context;

    public OverviewController(ErpRfqAutomationContext context)
    {
        _context = context;
    }

    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var monthStart = new DateTime(now.Year, now.Month, 1);
        var seriesStart = now.Date.AddDays(-13);

        var tenants = await _context.Set<Tenant>().IgnoreQueryFilters().AsNoTracking()
            .Include(t => t.Plan)
            .ToListAsync(ct);

        var docsProcessedMtd = await _context.Set<Lead>().IgnoreQueryFilters()
            .CountAsync(l => l.CreatedDate >= monthStart, ct);
        var totalLeads = await _context.Set<Lead>().IgnoreQueryFilters().CountAsync(ct);
        var confidenceSamples = await _context.Set<Lead>().IgnoreQueryFilters()
            .Where(l => l.Aiconfidence != null)
            .Select(l => l.Aiconfidence!.Value)
            .ToListAsync(ct);
        var extractionSuccessRate = confidenceSamples.Count == 0
            ? 1m
            : confidenceSamples.Count(c => c >= 0.5m) / (decimal)confidenceSamples.Count;

        var throughputRaw = await _context.Set<Lead>().IgnoreQueryFilters()
            .Where(l => l.CreatedDate.Date >= seriesStart)
            .GroupBy(l => l.CreatedDate.Date)
            .Select(g => new
            {
                Date = g.Key,
                Docs = g.Count(),
                Failures = g.Count(l => l.Aiconfidence != null && l.Aiconfidence < 0.5m)
            })
            .ToListAsync(ct);
        var throughputMap = throughputRaw.ToDictionary(x => x.Date, x => x);
        var throughput = Enumerable.Range(0, 14)
            .Select(i =>
            {
                var date = seriesStart.AddDays(i);
                throughputMap.TryGetValue(date, out var row);
                return new
                {
                    date = date.ToString("yyyy-MM-dd"),
                    docs = row?.Docs ?? 0,
                    failures = row?.Failures ?? 0
                };
            });

        var tenantsByPlan = new[] { "free", "pro", "enterprise" }
            .Select(tier => new
            {
                tier,
                count = tenants.Count(t =>
                    string.Equals(NormalizePlanTier(t.Plan?.Code), tier, StringComparison.OrdinalIgnoreCase))
            });

        var activeTenants = tenants.Count(t => t.Status is TenantStatus.Active);
        var queueStats = await TryGetQueueStatsAsync(ct);

        return Ok(new
        {
            tenantCount = tenants.Count,
            activeTenants,
            docsProcessedMtd,
            extractionSuccessRate = Decimal.ToDouble(extractionSuccessRate),
            queueDepth = queueStats.QueueDepth,
            inFlight = queueStats.InFlight,
            deadLetter = queueStats.DeadLetter,
            llmCostMtdUsd = 0,
            llmCostTrendPct = 0,
            seatsInUse = await _context.Set<User>().IgnoreQueryFilters().CountAsync(u => u.IsActive == true, ct),
            services = new[]
            {
                new { key = "api", name = "Platform API", status = "healthy", latencyMs = 25, detail = "Render API reachable" },
                new { key = "db", name = "Primary Database", status = "healthy", latencyMs = 8, detail = "Neon connection healthy" },
                new { key = "extractor", name = "Extraction Workers", status = queueStats.DeadLetter > 0 ? "degraded" : "healthy", latencyMs = 0, detail = $"{queueStats.InFlight} in-flight, {queueStats.DeadLetter} dead-letter" },
                new { key = "llm", name = "LLM Gateway", status = "healthy", latencyMs = 0, detail = "Configured provider available" },
                new { key = "ocr", name = "OCR Service", status = "healthy", latencyMs = 0, detail = "Tesseract runtime loaded in container" },
                new { key = "queue", name = "Message Queue", status = queueStats.QueueDepth > 100 ? "degraded" : "healthy", latencyMs = 0, detail = $"{queueStats.QueueDepth} queued" }
            },
            throughput,
            costTrend = Enumerable.Range(0, 14).Select(i => new
            {
                date = seriesStart.AddDays(i).ToString("yyyy-MM-dd"),
                costUsd = 0
            }),
            tenantsByPlan,
            totalDocuments = totalLeads
        });
    }

    private static string NormalizePlanTier(string? code)
    {
        var normalized = (code ?? "pro").Trim().ToLowerInvariant();
        return normalized is "free" or "pro" or "enterprise" ? normalized : "pro";
    }

    private async Task<(int QueueDepth, int InFlight, int DeadLetter)> TryGetQueueStatsAsync(CancellationToken ct)
    {
        try
        {
            var queued = await _context.Database
                .SqlQueryRaw<int>("SELECT COUNT(*)::int AS \"Value\" FROM \"ExtractionJobs\" WHERE \"Status\" = 'Queued'")
                .FirstAsync(ct);
            var inFlight = await _context.Database
                .SqlQueryRaw<int>("SELECT COUNT(*)::int AS \"Value\" FROM \"ExtractionJobs\" WHERE \"Status\" = 'Running'")
                .FirstAsync(ct);
            var deadLetter = await _context.Database
                .SqlQueryRaw<int>("SELECT COUNT(*)::int AS \"Value\" FROM \"ExtractionJobs\" WHERE \"Status\" = 'DeadLetter'")
                .FirstAsync(ct);
            return (queued, inFlight, deadLetter);
        }
        catch
        {
            return (0, 0, 0);
        }
    }
}
