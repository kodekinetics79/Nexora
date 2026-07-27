using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ERP_RFQ_Automation.Authorization;
using ERP_RFQ_Automation.CommercialLearning;
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
        [RequireModulePermission("Customers", PermissionAction.View)]
        [RequireModulePermission("RFQ Management", PermissionAction.View)]
        [RequireModulePermission("Quotations", PermissionAction.View)]
        [RequireModulePermission("Orders", PermissionAction.View)]
        public async Task<ActionResult<CustomerContextDTO>> GetContext(long customerId, CancellationToken ct)
        {
            var businessUnitId = GetBusinessUnitId();
            if (businessUnitId <= 0) return BadRequest("Business Unit ID is required.");

            var customer = await _db.Customers.AsNoTracking()
                .Where(c => c.Id == customerId && c.Buid == businessUnitId)
                .Select(c => new { c.Id, c.Name })
                .FirstOrDefaultAsync(ct);
            if (customer is null) return NotFound($"Customer {customerId} was not found.");

            var now = DateTime.UtcNow;

            // ── All quotes to this customer (header level, one query) ──
            var quotes = await _db.Quotes.AsNoTracking()
                .Include(q => q.Status)
                .Where(q => q.BusinessUnitId == businessUnitId && q.CustomerId == customer.Id)
                .ToListAsync(ct);

            // A quote is decided only by an explicit outcome or a customer-award
            // order. This is the same authoritative rule as Commercial Learning.
            var orderWins = await _db.Orders.AsNoTracking()
                .Where(o => o.BusinessUnitId == businessUnitId
                            && o.CustomerId == customer.Id
                            && o.SourceType == OrderSourceTypes.CustomerAward
                            && o.QuoteId.HasValue)
                .GroupBy(o => o.QuoteId!.Value)
                .ToDictionaryAsync(group => group.Key, group => group.Max(o => o.OrderDate), ct);
            var decidedQuotes = quotes.Where(q => q.OutcomeOn.HasValue || orderWins.ContainsKey(q.Id)).ToArray();
            var wonQuoteIds = decidedQuotes
                .Where(q => orderWins.ContainsKey(q.Id) || CommercialLearningRules.ResolveQuoteOutcome(q) == "WON")
                .Select(q => q.Id).ToHashSet();
            var lostQuoteIds = decidedQuotes
                .Where(q => !orderWins.ContainsKey(q.Id)
                            && CommercialLearningRules.ResolveQuoteOutcome(q) is "LOST" or "EXPIRED")
                .Select(q => q.Id).ToHashSet();
            var won = wonQuoteIds.Count;
            var lost = lostQuoteIds.Count;
            var decided = decidedQuotes.Length;

            // ── Orders, last 24 months (the CustomerHistory definition) ──
            var since = now.AddMonths(-OrderLookbackMonths);
            var ordersInWindow = _db.Orders.AsNoTracking()
                .Where(o => o.CustomerId == customer.Id
                            && o.BusinessUnitId == businessUnitId
                            && o.OrderDate >= since);
            var orderCount = await ordersInWindow.CountAsync(ct);
            var orderValue = await ordersInWindow.SumAsync(o => (decimal?)o.TotalAmount, ct) ?? 0m;

            // ── Last 10 quotes with their top line prices ──
            var recentQuoteIds = quotes
                .OrderByDescending(q => q.QuoteDate ?? DateTime.MinValue)
                .Take(RecentQuotesCount)
                .Select(q => q.Id)
                .ToList();

            var quoteIdsForLines = recentQuoteIds.Concat(orderWins.Keys).Distinct().ToList();
            var recentItems = await _db.QuoteItems.AsNoTracking()
                .Where(qi => quoteIdsForLines.Contains(qi.QuoteId))
                .Select(qi => new
                {
                    qi.QuoteId,
                    qi.ProductId,
                    Description = qi.ItemDescription ?? (qi.Product != null ? qi.Product.ProductName : null),
                    qi.UnitPrice,
                    qi.Quantity,
                    qi.TotalAmount
                })
                .ToListAsync(ct);
            var itemsByQuote = recentItems.ToLookup(i => i.QuoteId);

            // Outcome reason names — loose FK to SetupMaster, batch-resolved.
            var reasonIds = quotes.Where(q => q.OutcomeReasonId.HasValue)
                .Select(q => q.OutcomeReasonId!.Value).Distinct().ToList();
            var reasonNames = reasonIds.Count == 0
                ? new Dictionary<long, string>()
                : await _db.SetupMasters.AsNoTracking()
                    .Where(s => s.BusinessUnitId == businessUnitId && reasonIds.Contains(s.SetupId))
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
                    StatusValue = q.Status != null ? q.Status.SetupValue : null,
                    Outcome = wonQuoteIds.Contains(q.Id) ? "won"
                        : lostQuoteIds.Contains(q.Id) ? "lost"
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
            var soldQuoteIds = orderWins.Keys.ToHashSet();
            var quoteDateById = quotes.ToDictionary(q => q.Id, q => q.QuoteDate);
            var recentItemPrices = recentItems
                .Where(i => soldQuoteIds.Contains(i.QuoteId))
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

            var recentRfqs = await _db.Rfqs.AsNoTracking()
                .Where(r => r.BusinessUnitId == businessUnitId && r.CustomerId == customer.Id)
                .OrderByDescending(r => r.RecDate)
                .Take(10)
                .Select(r => new CustomerRfqSummaryDTO
                {
                    RfqId = r.Id,
                    RfqNo = r.Rfqno,
                    ReceivedOn = r.RecDate,
                    BidClosingOn = r.BidClosingDate,
                    Status = r.Rfqstatus != null ? r.Rfqstatus.SetupValue : null,
                    LineCount = r.Rfqitems.Count
                })
                .ToListAsync(ct);

            var recentOrderSummaries = await _db.Orders.AsNoTracking()
                .Where(o => o.BusinessUnitId == businessUnitId && o.CustomerId == customer.Id)
                .OrderByDescending(o => o.OrderDate)
                .Take(10)
                .Select(o => new CustomerOrderSummaryDTO
                {
                    OrderId = o.Id,
                    OrderNo = o.OrderNo,
                    OrderDate = o.OrderDate,
                    Status = o.Status.SetupValue,
                    TotalAmount = o.TotalAmount,
                    QuoteId = o.QuoteId
                })
                .ToListAsync(ct);

            var demandLines = await _db.Rfqitems.AsNoTracking()
                .Where(line => line.Rfq.BusinessUnitId == businessUnitId && line.Rfq.CustomerId == customer.Id)
                .Select(line => new
                {
                    line.Rfqid,
                    line.ProductId,
                    PartNumber = line.ManufacturerPartNumber ?? line.ItemMaterialCode ?? line.AlternatePartNumber,
                    Description = line.ProductShortName ?? line.ProductShortDescription ?? line.CommodityProduct ?? line.ItemText,
                    line.Quantity
                })
                .ToListAsync(ct);
            var demandProfile = demandLines
                .GroupBy(line => new { line.ProductId, line.PartNumber, line.Description })
                .Select(group => new CustomerDemandSummaryDTO
                {
                    ProductId = group.Key.ProductId,
                    PartNumber = group.Key.PartNumber,
                    Description = group.Key.Description,
                    InquiryCount = group.Select(line => line.Rfqid).Distinct().Count(),
                    RequestedQuantity = group.Sum(line => line.Quantity)
                })
                .OrderByDescending(line => line.InquiryCount)
                .ThenByDescending(line => line.RequestedQuantity)
                .Take(8)
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
                AvgQuoteTotal = quotes.Any(q => q.TotalAmount.HasValue)
                    ? Math.Round(quotes.Where(q => q.TotalAmount.HasValue).Average(q => q.TotalAmount!.Value), 2, MidpointRounding.AwayFromZero)
                    : null,
                // QuoteItem does not carry the immutable cost snapshot needed to
                // calculate historical realized margin. Null is more truthful than
                // applying today's product cost to an older sale.
                AvgMarginPct = null,
                LastQuoteDate = quotes.Select(q => q.QuoteDate).DefaultIfEmpty().Max(),
                RecentQuotes = recentQuotes,
                RecentItemPrices = recentItemPrices,
                RecentRfqs = recentRfqs,
                RecentOrders = recentOrderSummaries,
                DemandProfile = demandProfile,
                GeneratedAt = now
            });
        }

        private static int MonthsBetween(DateTime from, DateTime to) =>
            (to.Year - from.Year) * 12 + (to.Month - from.Month);

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

        /// <summary>Historical realized margin; null until an immutable quote-time cost snapshot exists.</summary>
        public decimal? AvgMarginPct { get; set; }

        public DateTime? LastQuoteDate { get; set; }

        /// <summary>The last 10 quotes to this customer, newest first.</summary>
        public List<CustomerQuoteSummaryDTO> RecentQuotes { get; set; } = new();

        /// <summary>Most recent unit price per distinct line ("Last sold at X, N months ago").</summary>
        public List<CustomerItemPriceDTO> RecentItemPrices { get; set; } = new();
        public List<CustomerRfqSummaryDTO> RecentRfqs { get; set; } = new();
        public List<CustomerOrderSummaryDTO> RecentOrders { get; set; } = new();
        public List<CustomerDemandSummaryDTO> DemandProfile { get; set; } = new();

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

    public sealed class CustomerRfqSummaryDTO
    {
        public long RfqId { get; set; }
        public string RfqNo { get; set; } = string.Empty;
        public DateTime ReceivedOn { get; set; }
        public DateTime? BidClosingOn { get; set; }
        public string? Status { get; set; }
        public int LineCount { get; set; }
    }

    public sealed class CustomerOrderSummaryDTO
    {
        public long OrderId { get; set; }
        public string OrderNo { get; set; } = string.Empty;
        public DateTime OrderDate { get; set; }
        public string? Status { get; set; }
        public decimal TotalAmount { get; set; }
        public long? QuoteId { get; set; }
    }

    public sealed class CustomerDemandSummaryDTO
    {
        public long? ProductId { get; set; }
        public string? PartNumber { get; set; }
        public string? Description { get; set; }
        public int InquiryCount { get; set; }
        public int RequestedQuantity { get; set; }
    }
}
