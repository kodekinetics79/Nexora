using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Platform.Auth;
using ERP_RFQ_Automation.Platform.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace ERP_RFQ_Automation.Platform.Controllers;

/// <summary>
/// Cross-tenant observability. Reads existing tenant tables with
/// <c>IgnoreQueryFilters()</c> (the platform plane is explicitly allowed to read
/// across tenants). Any not-yet-migrated surface (platform tables, ExtractionJobs)
/// degrades gracefully to null rather than failing the whole endpoint. (ADR-0005 §4)
/// </summary>
[ApiController]
[Route("api/platform/observability")]
[Authorize(Policy = PlatformPolicies.PlatformScope)]
public class ObservabilityController : ControllerBase
{
    private readonly ErpRfqAutomationContext _context;
    private readonly ILogger<ObservabilityController> _logger;

    public ObservabilityController(ErpRfqAutomationContext context, ILogger<ObservabilityController> logger)
    {
        _context = context;
        _logger = logger;
    }

    // GET /api/platform/observability/stats
    [HttpGet("stats")]
    public async Task<IActionResult> Stats(CancellationToken ct)
    {
        // Per-BusinessUnit grouped counts across the whole fleet (filters ignored).
        var leadByBu = await _context.Set<Lead>().IgnoreQueryFilters()
            .GroupBy(x => x.BusinessUnitId)
            .Select(g => new { Bu = g.Key, Count = g.Count() }).ToListAsync(ct);
        var rfqByBu = await _context.Set<Rfq>().IgnoreQueryFilters()
            .GroupBy(x => x.BusinessUnitId)
            .Select(g => new { Bu = g.Key, Count = g.Count() }).ToListAsync(ct);
        var quoteByBu = await _context.Set<Quote>().IgnoreQueryFilters()
            .GroupBy(x => x.BusinessUnitId)
            .Select(g => new { Bu = g.Key, Count = g.Count() }).ToListAsync(ct);
        var orderByBu = await _context.Set<Order>().IgnoreQueryFilters()
            .GroupBy(x => x.BusinessUnitId)
            .Select(g => new { Bu = g.Key, Count = g.Count() }).ToListAsync(ct);

        var businessUnits = await _context.Set<BusinessUnit>().IgnoreQueryFilters().AsNoTracking()
            .Select(b => new { b.Id, b.BusinessUnitName, b.IsActive }).ToListAsync(ct);

        var leadMap = leadByBu.ToDictionary(x => x.Bu, x => x.Count);
        var rfqMap = rfqByBu.ToDictionary(x => x.Bu, x => x.Count);
        var quoteMap = quoteByBu.ToDictionary(x => x.Bu, x => x.Count);
        var orderMap = orderByBu.ToDictionary(x => x.Bu, x => x.Count);

        var perBu = businessUnits.Select(b => new
        {
            businessUnitId = b.Id,
            name = b.BusinessUnitName,
            isActive = b.IsActive,
            leads = leadMap.GetValueOrDefault(b.Id),
            rfqs = rfqMap.GetValueOrDefault(b.Id),
            quotes = quoteMap.GetValueOrDefault(b.Id),
            orders = orderMap.GetValueOrDefault(b.Id)
        }).OrderByDescending(x => x.leads + x.rfqs + x.quotes + x.orders).ToList();

        // Tenant count — degrades if the platform schema isn't migrated yet.
        int? tenantCount = null;
        try
        {
            tenantCount = await _context.Set<Tenant>().IgnoreQueryFilters().CountAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Observability: Tenant table unavailable (not migrated yet?)");
        }

        var totals = new
        {
            tenants = tenantCount,
            businessUnits = businessUnits.Count,
            leads = leadMap.Values.Sum(),
            rfqs = rfqMap.Values.Sum(),
            quotes = quoteMap.Values.Sum(),
            orders = orderMap.Values.Sum()
        };

        return Ok(new
        {
            generatedAtUtc = DateTime.UtcNow,
            totals,
            extractionJobs = await TryGetExtractionJobStatsAsync(ct),
            perBusinessUnit = perBu
        });
    }

    /// <summary>
    /// Best-effort extraction-job stats. The ExtractionJobs table does not exist in
    /// the current schema (arrives with the ADR-0003/§5 scale pipeline), so this
    /// returns a degraded marker rather than throwing. (ADR-0005 §4/§5)
    /// </summary>
    private async Task<object?> TryGetExtractionJobStatsAsync(CancellationToken ct)
    {
        try
        {
            // Guarded probe; if the relation is absent Npgsql throws and we degrade.
            var total = await _context.Database
                .SqlQueryRaw<int>("SELECT COUNT(*)::int AS \"Value\" FROM \"ExtractionJobs\"")
                .FirstAsync(ct);
            return new { available = true, total };
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UndefinedTable)
        {
            return new
            {
                available = false,
                reason = "migration_required",
                note = "ExtractionJobs is absent; the database migration level is incomplete."
            };
        }
    }
}
