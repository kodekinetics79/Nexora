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

/// <summary>
/// Fleet-wide operator metrics. Every figure on this endpoint is read from the database or
/// from the live health-check registry at request time; nothing is seeded, sampled or
/// remembered between calls.
///
/// THE RULES THIS ENDPOINT FOLLOWS, and why each one exists:
///
/// 1. A ratio with an empty denominator is <c>null</c>, never 0. The overview used to report
///    "Extraction Success 0.0%" on a fleet that had never run a single job — a catastrophic
///    failure reading, produced by dividing by nothing. Null means "no data yet" and the
///    console renders it as an em dash.
/// 2. Money is never blended across currencies. Order value is returned grouped BY currency
///    code, the same stance <c>FxConversionService</c> takes on the tenant dashboard: a fleet
///    of tenants trading in SAR, AED and USD has no meaningful single total, and inventing one
///    by adding the numerals is the most expensive kind of fake metric.
/// 3. Conversion is measured on LINKED records, not on two counts divided by each other.
///    "RFQs quoted" is the share of RFQs raised IN THE WINDOW that now carry at least one
///    quote — a cohort, so it stays true even when the quote lands days after the RFQ.
/// 4. Tenant lifecycle is reported in full. A fleet where every tenant sits in Provisioning is
///    indistinguishable from a healthy one if the only published numbers are "tenants" and
///    "active"; the whole status histogram is returned so the console can say so.
/// </summary>
[ApiController]
[Route("api/platform/overview")]
[Authorize(Policy = PlatformPolicies.PlatformScope)]
public class OverviewController(
    ErpRfqAutomationContext context,
    HealthCheckService healthChecks) : ControllerBase
{
    /// <summary>Windows the operator may ask for. Anything else is refused rather than
    /// silently rounded — a chart captioned "30 days" that holds 14 is a lie the operator
    /// cannot see.</summary>
    private static readonly int[] AllowedWindows = [7, 14, 30, 90];

    private const int TopTenantLimit = 8;

    [HttpGet]
    public async Task<IActionResult> Get([FromQuery] int windowDays = 14, CancellationToken ct = default)
    {
        if (!AllowedWindows.Contains(windowDays))
        {
            ModelState.AddModelError(nameof(windowDays),
                $"windowDays must be one of {string.Join(", ", AllowedWindows)}.");
            // The descriptor overload, not ValidationProblem(ModelState): the latter builds its
            // result through ProblemDetailsFactory and returns an ObjectResult with no status
            // code of its own, so the refusal is only a 400 once the MVC pipeline fills it in.
            return ValidationProblem(new ValidationProblemDetails(ModelState)
            {
                Status = StatusCodes.Status400BadRequest
            });
        }

        var now = DateTime.UtcNow;
        var monthStart = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var priorPeriodStart = monthStart.AddMonths(-1);
        var priorPeriodEnd = priorPeriodStart.Add(now - monthStart);
        // Inclusive of today, so a 14-day window is the 13 days before today plus today.
        var seriesStart = now.Date.AddDays(-(windowDays - 1));

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
        var failedStatuses = new[] { ExtractionStatus.Failed, ExtractionStatus.DeadLetter };

        // ---- fleet ---------------------------------------------------------------------
        var tenants = await context.Set<Tenant>().IgnoreQueryFilters().AsNoTracking()
            .Include(t => t.Plan)
            .ToListAsync(ct);

        // ---- extraction ----------------------------------------------------------------
        var jobs = context.Set<ExtractionJob>().IgnoreQueryFilters().AsNoTracking();
        var terminalCount = await jobs.CountAsync(j => terminalStatuses.Contains(j.Status), ct);
        var succeededCount = await jobs.CountAsync(j => j.Status == ExtractionStatus.Succeeded, ct);
        var windowTerminal = await jobs.CountAsync(
            j => j.UpdatedOn >= seriesStart && terminalStatuses.Contains(j.Status), ct);
        var windowSucceeded = await jobs.CountAsync(
            j => j.UpdatedOn >= seriesStart && j.Status == ExtractionStatus.Succeeded, ct);
        var queueDepth = await jobs.CountAsync(j => j.Status == ExtractionStatus.Pending, ct);
        // The age of the oldest thing still waiting is what tells an operator whether a queue
        // depth of 40 is a busy minute or a stalled worker. A depth alone cannot say.
        var oldestPending = await jobs.Where(j => j.Status == ExtractionStatus.Pending)
            .OrderBy(j => j.CreatedOn)
            .Select(j => (DateTime?)j.CreatedOn)
            .FirstOrDefaultAsync(ct);

        // Bucketed by the day the job REACHED a terminal state, not the day it was created.
        // The series used to key on CreatedOn while the headline counts keyed on UpdatedOn, so
        // the "documents processed" tile and the chart underneath it disagreed about the same
        // fortnight — the failure count in the caption did not add up to the red area below it.
        var throughputRaw = await jobs
            .Where(j => j.UpdatedOn >= seriesStart)
            .GroupBy(j => j.UpdatedOn.Date)
            .Select(g => new
            {
                Date = g.Key,
                Docs = g.Count(j => j.Status == ExtractionStatus.Succeeded),
                Failures = g.Count(j => j.Status == ExtractionStatus.Failed || j.Status == ExtractionStatus.DeadLetter)
            })
            .ToListAsync(ct);
        // Keyed on the DATE STRING the series will publish, not on a DateTime. The grouping
        // key comes back from the provider with DateTimeKind.Unspecified while the series walks
        // forward from a Utc midnight, and matching those two as dictionary keys is a trap that
        // silently produced an all-zero chart above correct headline totals. The string is the
        // same thing the response emits, so a bucket that matches the label matches the data.
        var throughputMap = throughputRaw.ToDictionary(x => x.Date.ToString("yyyy-MM-dd"));

        // ---- gateway cost --------------------------------------------------------------
        var costRows = await context.Set<AiRequest>().IgnoreQueryFilters().AsNoTracking()
            .Where(r => r.CreatedOn >= seriesStart && r.CostCurrency == "USD" && r.EstimatedCost != null)
            .GroupBy(r => r.CreatedOn.Date)
            .Select(g => new { Date = g.Key, Cost = g.Sum(r => r.EstimatedCost!.Value) })
            .ToListAsync(ct);
        var costMap = costRows.ToDictionary(x => x.Date.ToString("yyyy-MM-dd"), x => x.Cost);
        var currentCost = await context.Set<AiRequest>().IgnoreQueryFilters().AsNoTracking()
            .Where(r => r.CreatedOn >= monthStart && r.CostCurrency == "USD" && r.EstimatedCost != null)
            .SumAsync(r => r.EstimatedCost ?? 0m, ct);
        var priorCost = await context.Set<AiRequest>().IgnoreQueryFilters().AsNoTracking()
            .Where(r => r.CreatedOn >= priorPeriodStart && r.CreatedOn < priorPeriodEnd &&
                r.CostCurrency == "USD" && r.EstimatedCost != null)
            .SumAsync(r => r.EstimatedCost ?? 0m, ct);

        // ---- the commercial spine ------------------------------------------------------
        // What the product is FOR. An operator console that reports document counts and gateway
        // spend but not whether a single quote left the building can tell you the machine is
        // running and not whether it is doing anything.
        var leadsCaptured = await context.Set<Lead>().IgnoreQueryFilters().AsNoTracking()
            .CountAsync(l => l.CreatedDate >= seriesStart, ct);
        var rfqs = context.Set<Rfq>().IgnoreQueryFilters().AsNoTracking()
            .Where(r => r.CreatedDate >= seriesStart);
        var rfqsCaptured = await rfqs.CountAsync(ct);
        var rfqsQuoted = await rfqs.CountAsync(r => r.Quotes.Any(), ct);
        var quotes = context.Set<Quote>().IgnoreQueryFilters().AsNoTracking()
            .Where(q => q.CreatedDate != null && q.CreatedDate >= seriesStart);
        var quotesIssued = await quotes.CountAsync(ct);
        var quotesOrdered = await quotes.CountAsync(q => q.Orders.Any(), ct);
        var ordersWon = await context.Set<Order>().IgnoreQueryFilters().AsNoTracking()
            .CountAsync(o => o.CreatedOn >= seriesStart, ct);

        // Grouped by currency and NEVER summed across them (rule 2). A currency-less order is
        // reported under "unknown" rather than folded into whichever code happens to be first.
        var orderValue = await context.Set<Order>().IgnoreQueryFilters().AsNoTracking()
            .Where(o => o.CreatedOn >= seriesStart)
            .GroupBy(o => o.Currency!.Code)
            .Select(g => new { Currency = g.Key, Orders = g.Count(), Amount = g.Sum(o => o.TotalAmount) })
            .ToListAsync(ct);

        // ---- per-tenant activity -------------------------------------------------------
        // The data plane keys on BusinessUnitId; platform.Tenants maps to it through
        // PrimaryBusinessUnitId. That mapping is NOT guaranteed unique (two tenants sharing a
        // primary business unit is a real state this console already survives on /pipeline),
        // so activity is looked up per business unit and then attributed to every tenant that
        // claims it — rather than joined in a way that silently drops or doubles a row.
        var tenantUnits = tenants
            .Where(t => t.PrimaryBusinessUnitId.HasValue)
            .Select(t => t.PrimaryBusinessUnitId!.Value)
            .Distinct()
            .ToList();

        var docsByUnit = await jobs
            .Where(j => j.UpdatedOn >= seriesStart && tenantUnits.Contains(j.BusinessUnitId))
            .GroupBy(j => j.BusinessUnitId)
            .Select(g => new
            {
                Unit = g.Key,
                Docs = g.Count(j => j.Status == ExtractionStatus.Succeeded),
                Failures = g.Count(j => failedStatuses.Contains(j.Status))
            })
            .ToDictionaryAsync(x => x.Unit, ct);
        var rfqsByUnit = await rfqs
            .Where(r => tenantUnits.Contains(r.BusinessUnitId))
            .GroupBy(r => r.BusinessUnitId)
            .Select(g => new { Unit = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Unit, x => x.Count, ct);
        var quotesByUnit = await quotes
            .Where(q => tenantUnits.Contains(q.BusinessUnitId))
            .GroupBy(q => q.BusinessUnitId)
            .Select(g => new { Unit = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Unit, x => x.Count, ct);
        var ordersByUnit = await context.Set<Order>().IgnoreQueryFilters().AsNoTracking()
            .Where(o => o.CreatedOn >= seriesStart && tenantUnits.Contains(o.BusinessUnitId))
            .GroupBy(o => o.BusinessUnitId)
            .Select(g => new { Unit = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Unit, x => x.Count, ct);

        var topTenants = tenants
            .Select(t =>
            {
                var unit = t.PrimaryBusinessUnitId;
                var extraction = unit.HasValue && docsByUnit.TryGetValue(unit.Value, out var e) ? e : null;
                var rfqCount = unit.HasValue ? rfqsByUnit.GetValueOrDefault(unit.Value) : 0;
                var quoteCount = unit.HasValue ? quotesByUnit.GetValueOrDefault(unit.Value) : 0;
                var orderCount = unit.HasValue ? ordersByUnit.GetValueOrDefault(unit.Value) : 0;
                return new
                {
                    tenantId = t.Id,
                    name = t.Name,
                    slug = t.Slug,
                    status = t.Status.ToString(),
                    plan = string.IsNullOrWhiteSpace(t.Plan?.Code) ? null : t.Plan!.Code.Trim().ToLowerInvariant(),
                    docs = extraction?.Docs ?? 0,
                    failures = extraction?.Failures ?? 0,
                    rfqs = rfqCount,
                    quotes = quoteCount,
                    orders = orderCount
                };
            })
            // Ordered by everything the tenant actually DID in the window, then by name so the
            // list is stable across refreshes when a fleet is quiet.
            .OrderByDescending(t => t.docs + t.rfqs + t.quotes + t.orders)
            .ThenBy(t => t.name, StringComparer.OrdinalIgnoreCase)
            .Take(TopTenantLimit)
            .ToList();

        // ---- health --------------------------------------------------------------------
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
        }).ToList();

        return Ok(new
        {
            // Every number below was true AT this instant, for THIS window. Both are published
            // so a stale tab is identifiable as one.
            asOfUtc = now,
            windowDays,
            windowStartUtc = seriesStart,

            tenantCount = tenants.Count,
            activeTenants = tenants.Count(t => t.Status == TenantStatus.Active),
            // Zero-filled so the console can render every lifecycle bucket, including the ones
            // that are empty — an absent "Suspended" bucket and a zero one mean different
            // things to an operator deciding whether to look.
            tenantsByStatus = Enum.GetValues<TenantStatus>()
                .Select(status => new { status = status.ToString(), count = tenants.Count(t => t.Status == status) })
                .ToList(),
            newTenantsInWindow = tenants.Count(t => t.CreatedOn >= seriesStart),

            docsProcessedMtd = await jobs.CountAsync(
                j => j.UpdatedOn >= monthStart && j.Status == ExtractionStatus.Succeeded, ct),
            docsProcessedInWindow = windowSucceeded,
            failuresInWindow = windowTerminal - windowSucceeded,
            // Null, not zero, when nothing has finished (rule 1).
            extractionSuccessRate = terminalCount == 0 ? (double?)null : (double)succeededCount / terminalCount,
            extractionSuccessRateWindow = windowTerminal == 0
                ? (double?)null
                : (double)windowSucceeded / windowTerminal,
            queueDepth,
            inFlight = await jobs.CountAsync(j => activeStatuses.Contains(j.Status), ct),
            deadLetter = await jobs.CountAsync(j => j.Status == ExtractionStatus.DeadLetter, ct),
            oldestPendingMinutes = oldestPending.HasValue
                ? (double?)Math.Max(0d, Math.Round((now - oldestPending.Value).TotalMinutes, 1))
                : null,

            llmCostMtdUsd = currentCost,
            llmCostTrendPct = priorCost > 0
                ? (double?)((currentCost - priorCost) / priorCost * 100m)
                : null,

            // Clearly-labeled FLEET-WIDE total of active tenant users (all business
            // units). This is not a per-tenant seat count and no longer pretends to
            // be one; the old misleading "seatsInUse" key is gone.
            activeUsersFleetWide = await context.Set<User>().IgnoreQueryFilters().CountAsync(u => u.IsActive == true, ct),

            commercial = new
            {
                leadsCaptured,
                rfqsCaptured,
                quotesIssued,
                ordersWon,
                // Cohort conversion on linked records (rule 3), null when the cohort is empty.
                rfqsQuotedPct = rfqsCaptured == 0 ? (double?)null : (double)rfqsQuoted / rfqsCaptured,
                quotesOrderedPct = quotesIssued == 0 ? (double?)null : (double)quotesOrdered / quotesIssued,
                orderValueByCurrency = orderValue
                    .Select(v => new
                    {
                        currency = string.IsNullOrWhiteSpace(v.Currency) ? "unknown" : v.Currency,
                        orders = v.Orders,
                        amount = v.Amount
                    })
                    .OrderByDescending(v => v.amount)
                    .ToList()
            },

            health = new
            {
                worst = services.Any(s => s.status == "down") ? "down"
                    : services.Any(s => s.status == "degraded") ? "degraded"
                    : "healthy",
                healthy = services.Count(s => s.status == "healthy"),
                degraded = services.Count(s => s.status == "degraded"),
                down = services.Count(s => s.status == "down")
            },
            services,

            throughput = Enumerable.Range(0, windowDays).Select(i =>
            {
                var date = seriesStart.AddDays(i).ToString("yyyy-MM-dd");
                throughputMap.TryGetValue(date, out var row);
                return new { date, docs = row?.Docs ?? 0, failures = row?.Failures ?? 0 };
            }),
            costTrend = Enumerable.Range(0, windowDays).Select(i =>
            {
                var date = seriesStart.AddDays(i).ToString("yyyy-MM-dd");
                return new { date, costUsd = costMap.GetValueOrDefault(date) };
            }),

            // Buckets are the REAL plan codes present in the fleet; tenants without
            // a plan are reported under "none" — never silently defaulted to "pro".
            tenantsByPlan = tenants
                .GroupBy(t => string.IsNullOrWhiteSpace(t.Plan?.Code)
                    ? "none"
                    : t.Plan!.Code.Trim().ToLowerInvariant())
                .OrderBy(g => g.Key)
                .Select(g => new { tier = g.Key, count = g.Count() }),

            topTenants
        });
    }

    private static string Humanize(string value)
        => string.Join(' ', value.Split('-', StringSplitOptions.RemoveEmptyEntries)
            .Select(word => char.ToUpperInvariant(word[0]) + word[1..]));
}
