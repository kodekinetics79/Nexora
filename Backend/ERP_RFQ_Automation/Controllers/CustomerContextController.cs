using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ERP_RFQ_Automation.Authorization;
using ERP_RFQ_Automation.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Controllers
{
    /// <summary>
    /// WP-B2: "This customer" context at quoting time — win rate, recent order
    /// history, average margin era and last-sold line prices, in one payload.
    ///
    /// Mirrors the CustomerHistory block of the Lead Decision Brief
    /// (Intelligence/Decision/LeadDecisionService.ResolveCustomerHistoryAsync)
    /// but keyed by customer id rather than by lead — that method is private and
    /// lead-shaped, so the shared semantics (24-month order lookback, honest
    /// nulls for unknowable numbers) are reproduced here instead of forced
    /// through a lead. The business unit ALWAYS comes from the JWT claim.
    /// </summary>
    [ApiController]
    [Authorize]
    [Route("api/intelligence/customers")]
    public class CustomerContextController : ControllerBase
    {
        /// <summary>Same lookback as LeadDecisionService.OrderLookbackMonths.</summary>
        private const int OrderLookbackMonths = 24;

        private const int RecentQuotesCount = 10;
        private const int KeyLinesPerQuote = 3;
        private const int RecentItemPricesCount = 8;

        private readonly ErpRfqAutomationContext _db;

        public CustomerContextController(ErpRfqAutomationContext db)
        {
            _db = db;
        }

        /// <summary>GET /api/intelligence/customers/{customerId}/context</summary>
        [HttpGet("{customerId:long}/context")]
        [RequireModulePermission("Quotations", PermissionAction.View)]
        public async Task<ActionResult<CustomerContextDTO>> GetContext(long customerId, CancellationToken ct)
        {
            var businessUnitId = GetBusinessUnitId();
            if (businessUnitId <= 0) return BadRequest("Business Unit ID is required.");

            // Customers are master data (Buid == null = shared) — the same
            // visibility predicate as LeadDecisionService / the global filter.
            var customer = await _db.Customers.AsNoTracking()
                .Where(c => c.Id == customerId && (c.Buid == null || c.Buid == businessUnitId))
                .Select(c => new { c.Id, c.Name })
                .FirstOrDefaultAsync(ct);
            if (customer is null) return NotFound($"Customer {customerId} was not found.");

            var now = DateTime.UtcNow;

            // Statuses resolved by SetupType "QuoteStatus" + SetupCode with the
            // documented legacy fallback ids (never bare magic ids). Won covers
            // ACCEPTED and ORDERED (converted to an order); lost covers
            // REJECTED and EXPIRED — the same mapping QuoteOutcomeService writes.
            var wonStatusIds = (await GetQuoteStatusIdsAsync("ACCEPTED", 44, ct))
                .Concat(await GetQuoteStatusIdsAsync("ORDERED", null, ct)).Distinct().ToList();
            var lostStatusIds = (await GetQuoteStatusIdsAsync("REJECTED", 45, ct))
                .Concat(await GetQuoteStatusIdsAsync("EXPIRED", null, ct)).Distinct().ToList();

            // ── All quotes to this customer (header level, one query) ──
            var quotes = await _db.Quotes.AsNoTracking()
                .Where(q => q.BusinessUnitId == businessUnitId && q.CustomerId == customer.Id)
                .Select(q => new
                {
                    q.Id,
                    q.QuoteNo,
                    q.QuoteDate,
                    q.TotalAmount,
                    q.StatusId,
                    StatusValue = q.Status != null ? q.Status.SetupValue : null,
                    q.OutcomeReasonId
                })
                .ToListAsync(ct);

            var won = quotes.Count(q => q.StatusId.HasValue && wonStatusIds.Contains(q.StatusId.Value));
            var lost = quotes.Count(q => q.StatusId.HasValue && lostStatusIds.Contains(q.StatusId.Value));
            var decided = won + lost;

            // ── Orders, last 24 months (the CustomerHistory definition) ──
            var since = now.AddMonths(-OrderLookbackMonths);
            var recentOrders = _db.Orders.AsNoTracking()
                .Where(o => o.CustomerId == customer.Id
                            && o.BusinessUnitId == businessUnitId
                            && o.OrderDate >= since);
            var orderCount = await recentOrders.CountAsync(ct);
            var orderValue = await recentOrders.SumAsync(o => (decimal?)o.TotalAmount, ct) ?? 0m;

            // ── Last 10 quotes with their top line prices ──
            var recentQuoteIds = quotes
                .OrderByDescending(q => q.QuoteDate ?? DateTime.MinValue)
                .Take(RecentQuotesCount)
                .Select(q => q.Id)
                .ToList();

            var recentItems = await _db.QuoteItems.AsNoTracking()
                .Where(qi => recentQuoteIds.Contains(qi.QuoteId))
                .Select(qi => new
                {
                    qi.QuoteId,
                    qi.ProductId,
                    Description = qi.ItemDescription ?? (qi.Product != null ? qi.Product.ProductName : null),
                    qi.UnitPrice,
                    qi.Quantity,
                    qi.TotalAmount,
                    Cost = qi.Product != null ? (qi.Product.FinalLandedCost ?? qi.Product.UnitCost) : null
                })
                .ToListAsync(ct);
            var itemsByQuote = recentItems.ToLookup(i => i.QuoteId);

            // Outcome reason names — loose FK to SetupMaster, batch-resolved.
            var reasonIds = quotes.Where(q => q.OutcomeReasonId.HasValue)
                .Select(q => q.OutcomeReasonId!.Value).Distinct().ToList();
            var reasonNames = reasonIds.Count == 0
                ? new Dictionary<long, string>()
                : await _db.SetupMasters.AsNoTracking()
                    .Where(s => reasonIds.Contains(s.SetupId))
                    .ToDictionaryAsync(s => s.SetupId, s => s.Description ?? s.SetupValue, ct);

            var recentQuotes = quotes
                .Where(q => recentQuoteIds.Contains(q.Id))
                .OrderByDescending(q => q.QuoteDate ?? DateTime.MinValue)
                .Select(q => new CustomerQuoteSummaryDTO
                {
                    QuoteId = q.Id,
                    QuoteNo = q.QuoteNo,
                    QuoteDate = q.QuoteDate,
                    TotalAmount = q.TotalAmount,
                    StatusValue = q.StatusValue,
                    Outcome = q.StatusId.HasValue && wonStatusIds.Contains(q.StatusId.Value) ? "won"
                        : q.StatusId.HasValue && lostStatusIds.Contains(q.StatusId.Value) ? "lost"
                        : "open",
                    OutcomeReasonName = q.OutcomeReasonId.HasValue && reasonNames.TryGetValue(q.OutcomeReasonId.Value, out var rn) ? rn : null,
                    KeyLines = itemsByQuote[q.Id]
                        .OrderByDescending(i => i.TotalAmount)
                        .Take(KeyLinesPerQuote)
                        .Select(i => new CustomerKeyLineDTO
                        {
                            Description = i.Description,
                            Quantity = i.Quantity,
                            UnitPrice = i.UnitPrice
                        })
                        .ToList()
                })
                .ToList();

            // ── "Last sold at X, N months ago": most recent price per distinct
            //    line (by product id when known, else by description) ──
            var quoteDateById = quotes.ToDictionary(q => q.Id, q => q.QuoteDate);
            var recentItemPrices = recentItems
                .Where(i => i.UnitPrice > 0)
                .Select(i => new
                {
                    Item = i,
                    Key = i.ProductId.HasValue ? $"p:{i.ProductId}" : $"d:{i.Description?.Trim().ToLowerInvariant()}",
                    Date = quoteDateById.TryGetValue(i.QuoteId, out var d) ? d : null
                })
                .Where(x => x.Key != "d:" && x.Key != "d:null")
                .GroupBy(x => x.Key)
                .Select(g => g.OrderByDescending(x => x.Date ?? DateTime.MinValue).First())
                .OrderByDescending(x => x.Date ?? DateTime.MinValue)
                .Take(RecentItemPricesCount)
                .Select(x => new CustomerItemPriceDTO
                {
                    ProductId = x.Item.ProductId,
                    Description = x.Item.Description,
                    UnitPrice = x.Item.UnitPrice,
                    QuoteDate = x.Date,
                    MonthsAgo = x.Date.HasValue ? Math.Max(0, MonthsBetween(x.Date.Value, now)) : null
                })
                .ToList();

            // ── Average margin era: (price − cost)/price over this customer's
            //    recent lines where the product has a known cost floor ──
            var marginSamples = recentItems
                .Where(i => i.UnitPrice > 0 && i.Cost.HasValue && i.Cost.Value > 0)
                .Select(i => (i.UnitPrice - i.Cost!.Value) / i.UnitPrice)
                .ToList();

            return Ok(new CustomerContextDTO
            {
                CustomerId = customer.Id,
                CustomerName = customer.Name,
                TotalQuotes = quotes.Count,
                WonQuotes = won,
                LostQuotes = lost,
                WinRatePct = decided > 0
                    ? Math.Round(100m * won / decided, 1, MidpointRounding.AwayFromZero)
                    : null,
                OrdersLast24Months = orderCount,
                OrderValueLast24Months = Math.Round(orderValue, 2, MidpointRounding.AwayFromZero),
                AvgQuoteTotal = quotes.Count > 0
                    ? Math.Round(quotes.Average(q => q.TotalAmount ?? 0m), 2, MidpointRounding.AwayFromZero)
                    : null,
                AvgMarginPct = marginSamples.Count > 0
                    ? Math.Round(marginSamples.Average() * 100m, 1, MidpointRounding.AwayFromZero)
                    : null,
                LastQuoteDate = quotes.Max(q => q.QuoteDate),
                RecentQuotes = recentQuotes,
                RecentItemPrices = recentItemPrices,
                GeneratedAt = now
            });
        }

        private static int MonthsBetween(DateTime from, DateTime to) =>
            (to.Year - from.Year) * 12 + (to.Month - from.Month);

        /// <summary>SetupType "QuoteStatus" + SetupCode resolution with documented legacy fallback (never magic ids alone).</summary>
        private async Task<List<long>> GetQuoteStatusIdsAsync(string code, long? legacyId, CancellationToken ct)
        {
            var ids = await _db.SetupMasters.AsNoTracking()
                .Where(s => s.SetupType == "QuoteStatus" && s.SetupCode == code)
                .Select(s => s.SetupId)
                .ToListAsync(ct);
            if (legacyId.HasValue && !ids.Contains(legacyId.Value)) ids.Add(legacyId.Value);
            return ids;
        }

        private long GetBusinessUnitId() =>
            long.TryParse(User.FindFirst("businessUnitId")?.Value, out var id) ? id : 0;
    }

    // ─── Wire contracts (camelCase via app-wide JSON defaults) ──────────────

    public sealed class CustomerContextDTO
    {
        public long CustomerId { get; set; }
        public string? CustomerName { get; set; }

        public int TotalQuotes { get; set; }
        public int WonQuotes { get; set; }
        public int LostQuotes { get; set; }

        /// <summary>won / (won + lost) as 0–100; null while nothing has been decided yet.</summary>
        public decimal? WinRatePct { get; set; }

        public int OrdersLast24Months { get; set; }
        public decimal OrderValueLast24Months { get; set; }

        public decimal? AvgQuoteTotal { get; set; }

        /// <summary>Average (price − cost)/price over recent lines with a known cost floor; null when unknowable.</summary>
        public decimal? AvgMarginPct { get; set; }

        public DateTime? LastQuoteDate { get; set; }

        /// <summary>The last 10 quotes to this customer, newest first.</summary>
        public List<CustomerQuoteSummaryDTO> RecentQuotes { get; set; } = new();

        /// <summary>Most recent unit price per distinct line ("Last sold at X, N months ago").</summary>
        public List<CustomerItemPriceDTO> RecentItemPrices { get; set; } = new();

        public DateTime GeneratedAt { get; set; }
    }

    public sealed class CustomerQuoteSummaryDTO
    {
        public long QuoteId { get; set; }
        public string QuoteNo { get; set; } = string.Empty;
        public DateTime? QuoteDate { get; set; }
        public decimal? TotalAmount { get; set; }
        public string? StatusValue { get; set; }

        /// <summary>"won" | "lost" | "open".</summary>
        public string Outcome { get; set; } = "open";

        public string? OutcomeReasonName { get; set; }
        public List<CustomerKeyLineDTO> KeyLines { get; set; } = new();
    }

    public sealed class CustomerKeyLineDTO
    {
        public string? Description { get; set; }
        public decimal Quantity { get; set; }
        public decimal UnitPrice { get; set; }
    }

    public sealed class CustomerItemPriceDTO
    {
        public long? ProductId { get; set; }
        public string? Description { get; set; }
        public decimal UnitPrice { get; set; }
        public DateTime? QuoteDate { get; set; }
        public int? MonthsAgo { get; set; }
    }
}
