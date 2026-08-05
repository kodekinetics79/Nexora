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
    /// Customer commercial context at quoting time. All tenant identity comes
    /// from the authenticated claim; monetary history is never mixed across
    /// currencies.
    /// </summary>
    [ApiController]
    [Authorize]
    [Route("api/intelligence/customers")]
    public class CustomerContextController : ControllerBase
    {
        private const int OrderLookbackMonths = 24;
        private const int DemandLookbackMonths = 24;
        private const int RecentQuotesCount = 10;
        private const int RecentRfqsCount = 10;
        private const int RecentOrdersCount = 10;
        private const int KeyLinesPerQuote = 3;
        private const int RecentItemPricesCount = 8;
        private const int DemandLineEvaluationLimit = 1_000;
        private const int SourceRfqIdsPerDemandLine = 50;
        private const int SoldOrderEvidenceLimit = 250;
        private const int QuoteItemEvidenceLimit = 5_000;

        private readonly ErpRfqAutomationContext _db;

        public CustomerContextController(ErpRfqAutomationContext db)
        {
            _db = db;
        }

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
            var quoteQuery = _db.Quotes.AsNoTracking()
                .Where(q => q.BusinessUnitId == businessUnitId && q.CustomerId == customer.Id);
            var totalQuotes = await quoteQuery.CountAsync(ct);

            // Outcome KPIs remain full-history SQL aggregates. Only an explicit
            // outcome or a customer-award order makes a quote decided.
            var won = await quoteQuery.CountAsync(q =>
                _db.Orders.Any(o => o.BusinessUnitId == businessUnitId
                    && o.CustomerId == customer.Id
                    && o.SourceType == OrderSourceTypes.CustomerAward
                    && o.QuoteId == q.Id)
                || (q.OutcomeOn.HasValue
                    && (q.StatusId == 44
                        || (q.Status != null
                            && (q.Status.SetupCode == "ACCEPTED" || q.Status.SetupCode == "ORDERED")))), ct);
            var lost = await quoteQuery.CountAsync(q => q.OutcomeOn.HasValue
                && q.StatusId != 44
                && (q.Status == null
                    || (q.Status.SetupCode != "ACCEPTED" && q.Status.SetupCode != "ORDERED"))
                && !_db.Orders.Any(o => o.BusinessUnitId == businessUnitId
                    && o.CustomerId == customer.Id
                    && o.SourceType == OrderSourceTypes.CustomerAward
                    && o.QuoteId == q.Id), ct);
            var decided = won + lost;

            var quoteCurrencyRows = await quoteQuery
                .Where(q => q.TotalAmount.HasValue)
                .GroupBy(q => new { q.CurrencyId, CurrencyCode = q.Currency != null ? q.Currency.Code : null })
                .Select(group => new
                {
                    group.Key.CurrencyId,
                    group.Key.CurrencyCode,
                    RecordCount = group.Count(),
                    TotalAmount = group.Sum(q => q.TotalAmount ?? 0m)
                })
                .ToListAsync(ct);
            var quoteValueGroups = ToCurrencyGroups(quoteCurrencyRows.Select(row =>
                (row.CurrencyId, row.CurrencyCode, row.RecordCount, row.TotalAmount)));

            var orderCohortFrom = now.AddMonths(-OrderLookbackMonths);
            var orderCurrencyRows = await _db.Orders.AsNoTracking()
                .Where(o => o.CustomerId == customer.Id
                    && o.BusinessUnitId == businessUnitId
                    && o.OrderDate >= orderCohortFrom)
                .GroupBy(o => new { o.CurrencyId, CurrencyCode = o.Currency != null ? o.Currency.Code : null })
                .Select(group => new
                {
                    group.Key.CurrencyId,
                    group.Key.CurrencyCode,
                    RecordCount = group.Count(),
                    TotalAmount = group.Sum(o => o.TotalAmount)
                })
                .ToListAsync(ct);
            var orderValueGroups = ToCurrencyGroups(orderCurrencyRows.Select(row =>
                (row.CurrencyId, row.CurrencyCode, row.RecordCount, row.TotalAmount)));
            var orderCount = orderValueGroups.Sum(group => group.RecordCount);

            var recentQuoteRows = await quoteQuery
                .OrderByDescending(q => q.QuoteDate ?? DateTime.MinValue)
                .ThenByDescending(q => q.Id)
                .Take(RecentQuotesCount)
                .Select(q => new
                {
                    q.Id,
                    q.QuoteNo,
                    q.QuoteDate,
                    q.TotalAmount,
                    q.CurrencyId,
                    CurrencyCode = q.Currency != null ? q.Currency.Code : null,
                    q.ContactId,
                    q.StatusId,
                    StatusCode = q.Status != null ? q.Status.SetupCode : null,
                    StatusValue = q.Status != null ? q.Status.SetupValue : null,
                    q.OutcomeOn,
                    q.OutcomeReasonId
                })
                .ToListAsync(ct);
            var recentQuoteIds = recentQuoteRows.Select(q => q.Id).ToList();

            var soldOrderCandidates = await _db.Orders.AsNoTracking()
                .Where(o => o.BusinessUnitId == businessUnitId
                    && o.CustomerId == customer.Id
                    && o.SourceType == OrderSourceTypes.CustomerAward
                    && o.QuoteId.HasValue)
                .OrderByDescending(o => o.OrderDate)
                .ThenByDescending(o => o.Id)
                .Take(SoldOrderEvidenceLimit + 1)
                .Select(o => new
                {
                    o.Id,
                    QuoteId = o.QuoteId!.Value,
                    o.OrderDate,
                    o.CurrencyId,
                    CurrencyCode = o.Currency != null ? o.Currency.Code : null
                })
                .ToListAsync(ct);
            var soldOrdersTruncated = soldOrderCandidates.Count > SoldOrderEvidenceLimit;
            var soldOrders = soldOrderCandidates.Take(SoldOrderEvidenceLimit).ToList();
            var latestSaleByQuote = soldOrders
                .GroupBy(order => order.QuoteId)
                .ToDictionary(group => group.Key, group => group
                    .OrderByDescending(order => order.OrderDate)
                    .ThenByDescending(order => order.Id)
                    .First());

            var quoteIdsForLines = recentQuoteIds.Concat(latestSaleByQuote.Keys).Distinct().ToList();
            var quoteItemCandidates = await _db.QuoteItems.AsNoTracking()
                .Where(qi => quoteIdsForLines.Contains(qi.QuoteId))
                .OrderByDescending(qi => qi.CreatedDate ?? DateTime.MinValue)
                .ThenByDescending(qi => qi.Id)
                .Take(QuoteItemEvidenceLimit + 1)
                .Select(qi => new
                {
                    qi.Id,
                    qi.QuoteId,
                    qi.ProductId,
                    Description = qi.ItemDescription ?? (qi.Product != null ? qi.Product.ProductName : null),
                    qi.UnitPrice,
                    qi.Quantity,
                    qi.TotalAmount
                })
                .ToListAsync(ct);
            var quoteItemsTruncated = quoteItemCandidates.Count > QuoteItemEvidenceLimit;
            var recentItems = quoteItemCandidates.Take(QuoteItemEvidenceLimit).ToList();
            var itemsByQuote = recentItems.ToLookup(item => item.QuoteId);

            var recentAwardQuoteIds = await _db.Orders.AsNoTracking()
                .Where(o => o.BusinessUnitId == businessUnitId
                    && o.CustomerId == customer.Id
                    && o.SourceType == OrderSourceTypes.CustomerAward
                    && o.QuoteId.HasValue
                    && recentQuoteIds.Contains(o.QuoteId.Value))
                .Select(o => o.QuoteId!.Value)
                .Distinct()
                .ToListAsync(ct);
            var wonRecentQuoteIds = recentQuoteRows
                .Where(q => recentAwardQuoteIds.Contains(q.Id)
                    || (q.OutcomeOn.HasValue && IsWinningStatus(q.StatusCode, q.StatusId)))
                .Select(q => q.Id)
                .ToHashSet();
            var lostRecentQuoteIds = recentQuoteRows
                .Where(q => q.OutcomeOn.HasValue && !wonRecentQuoteIds.Contains(q.Id))
                .Select(q => q.Id)
                .ToHashSet();

            var reasonIds = recentQuoteRows.Where(q => q.OutcomeReasonId.HasValue)
                .Select(q => q.OutcomeReasonId!.Value).Distinct().ToList();
            var reasonNames = reasonIds.Count == 0
                ? new Dictionary<long, string>()
                : await _db.SetupMasters.AsNoTracking()
                    .Where(s => s.BusinessUnitId == businessUnitId && reasonIds.Contains(s.SetupId))
                    .ToDictionaryAsync(s => s.SetupId, s => s.Description ?? s.SetupValue, ct);

            var recentRfqRows = await _db.Rfqs.AsNoTracking()
                .Where(r => r.BusinessUnitId == businessUnitId && r.CustomerId == customer.Id)
                .OrderByDescending(r => r.RecDate)
                .ThenByDescending(r => r.Id)
                .Take(RecentRfqsCount)
                .Select(r => new
                {
                    r.Id,
                    r.Rfqno,
                    r.RecDate,
                    r.BidClosingDate,
                    Status = r.Rfqstatus != null ? r.Rfqstatus.SetupValue : null,
                    LineCount = r.Rfqitems.Count,
                    r.ContactId
                })
                .ToListAsync(ct);
            var recentOrderRows = await _db.Orders.AsNoTracking()
                .Where(o => o.BusinessUnitId == businessUnitId && o.CustomerId == customer.Id)
                .OrderByDescending(o => o.OrderDate)
                .ThenByDescending(o => o.Id)
                .Take(RecentOrdersCount)
                .Select(o => new
                {
                    o.Id,
                    o.OrderNo,
                    o.OrderDate,
                    Status = o.Status.SetupValue,
                    o.TotalAmount,
                    o.QuoteId,
                    o.CurrencyId,
                    CurrencyCode = o.Currency != null ? o.Currency.Code : null,
                    o.ContactId
                })
                .ToListAsync(ct);

            var contactIds = recentQuoteRows.Select(row => row.ContactId)
                .Concat(recentRfqRows.Select(row => row.ContactId))
                .Concat(recentOrderRows.Select(row => row.ContactId))
                .Where(id => id.HasValue)
                .Select(id => id!.Value)
                .Distinct()
                .ToList();
            var contacts = contactIds.Count == 0
                ? new Dictionary<long, string>()
                : await _db.Contacts.AsNoTracking()
                    .Where(contact => contact.BusinessUnitId == businessUnitId
                        && contactIds.Contains(contact.Id))
                    .ToDictionaryAsync(contact => contact.Id,
                        contact => (contact.FirstName + " " + contact.LastName).Trim(), ct);

            var recentQuotes = recentQuoteRows.Select(q => new CustomerQuoteSummaryDTO
            {
                QuoteId = q.Id,
                QuoteNo = q.QuoteNo,
                QuoteDate = q.QuoteDate,
                TotalAmount = q.TotalAmount,
                CurrencyId = q.CurrencyId,
                CurrencyCode = q.CurrencyCode,
                ContactId = q.ContactId,
                ContactName = ContactName(q.ContactId, contacts),
                StatusValue = q.StatusValue,
                Outcome = wonRecentQuoteIds.Contains(q.Id) ? "won"
                    : lostRecentQuoteIds.Contains(q.Id) ? "lost"
                    : "open",
                OutcomeReasonName = q.OutcomeReasonId.HasValue
                    && reasonNames.TryGetValue(q.OutcomeReasonId.Value, out var reasonName)
                        ? reasonName
                        : null,
                KeyLines = itemsByQuote[q.Id]
                    .OrderByDescending(item => item.TotalAmount)
                    .Take(KeyLinesPerQuote)
                    .Select(item => new CustomerKeyLineDTO
                    {
                        Description = item.Description,
                        Quantity = item.Quantity,
                        UnitPrice = item.UnitPrice
                    })
                    .ToList()
            }).ToList();

            // The sale date and currency are authoritative customer-award order
            // facts. QuoteDate remains as a compatibility alias for SoldOn.
            var recentItemPrices = recentItems
                .Where(item => latestSaleByQuote.ContainsKey(item.QuoteId) && item.UnitPrice > 0)
                .Select(item => new
                {
                    Item = item,
                    Key = item.ProductId.HasValue
                        ? $"p:{item.ProductId}"
                        : $"d:{item.Description?.Trim().ToLowerInvariant()}",
                    Sale = latestSaleByQuote[item.QuoteId]
                })
                .Where(row => row.Key != "d:" && row.Key != "d:null")
                .GroupBy(row => row.Key)
                .Select(group => group.OrderByDescending(row => row.Sale.OrderDate)
                    .ThenByDescending(row => row.Sale.Id).First())
                .OrderByDescending(row => row.Sale.OrderDate)
                .Take(RecentItemPricesCount)
                .Select(row => new CustomerItemPriceDTO
                {
                    ProductId = row.Item.ProductId,
                    Description = row.Item.Description,
                    UnitPrice = row.Item.UnitPrice,
                    AwardOrderId = row.Sale.Id,
                    CurrencyId = row.Sale.CurrencyId,
                    CurrencyCode = row.Sale.CurrencyCode,
                    SoldOn = row.Sale.OrderDate,
                    QuoteDate = row.Sale.OrderDate,
                    MonthsAgo = Math.Max(0, MonthsBetween(row.Sale.OrderDate, now))
                })
                .ToList();

            var recentRfqs = recentRfqRows.Select(r => new CustomerRfqSummaryDTO
            {
                RfqId = r.Id,
                RfqNo = r.Rfqno,
                ReceivedOn = r.RecDate,
                BidClosingOn = r.BidClosingDate,
                Status = r.Status,
                LineCount = r.LineCount,
                ContactId = r.ContactId,
                ContactName = ContactName(r.ContactId, contacts)
            }).ToList();
            var recentOrders = recentOrderRows.Select(o => new CustomerOrderSummaryDTO
            {
                OrderId = o.Id,
                OrderNo = o.OrderNo,
                OrderDate = o.OrderDate,
                Status = o.Status,
                TotalAmount = o.TotalAmount,
                QuoteId = o.QuoteId,
                CurrencyId = o.CurrencyId,
                CurrencyCode = o.CurrencyCode,
                ContactId = o.ContactId,
                ContactName = ContactName(o.ContactId, contacts)
            }).ToList();

            var demandFrom = now.AddMonths(-DemandLookbackMonths);
            var demandLineCandidates = await _db.Rfqitems.AsNoTracking()
                .Where(line => line.Rfq.BusinessUnitId == businessUnitId
                    && line.Rfq.CustomerId == customer.Id
                    && line.Rfq.RecDate >= demandFrom)
                .OrderByDescending(line => line.Rfq.RecDate)
                .ThenByDescending(line => line.Rfqid)
                .ThenByDescending(line => line.Id)
                .Take(DemandLineEvaluationLimit + 1)
                .Select(line => new
                {
                    line.Id,
                    line.Rfqid,
                    line.ProductId,
                    PartNumber = line.ManufacturerPartNumber ?? line.ItemMaterialCode ?? line.AlternatePartNumber,
                    Description = line.ProductShortName ?? line.ProductShortDescription
                        ?? line.CommodityProduct ?? line.ItemText,
                    line.Quantity
                })
                .ToListAsync(ct);
            var demandTruncated = demandLineCandidates.Count > DemandLineEvaluationLimit;
            var demandLines = demandLineCandidates.Take(DemandLineEvaluationLimit).ToList();
            var demandProfile = demandLines
                .GroupBy(line => new { line.ProductId, line.PartNumber, line.Description })
                .Select(group =>
                {
                    var sourceRfqIds = group.Select(line => line.Rfqid).Distinct()
                        .OrderByDescending(id => id).ToList();
                    return new CustomerDemandSummaryDTO
                    {
                        ProductId = group.Key.ProductId,
                        PartNumber = group.Key.PartNumber,
                        Description = group.Key.Description,
                        InquiryCount = sourceRfqIds.Count,
                        RequestedQuantity = group.Sum(line => (long)line.Quantity),
                        SourceRfqIds = sourceRfqIds.Take(SourceRfqIdsPerDemandLine).ToList(),
                        SourceRfqsTruncated = sourceRfqIds.Count > SourceRfqIdsPerDemandLine
                    };
                })
                .OrderByDescending(line => line.InquiryCount)
                .ThenByDescending(line => line.RequestedQuantity)
                .Take(8)
                .ToList();

            return Ok(new CustomerContextDTO
            {
                CustomerId = customer.Id,
                CustomerName = customer.Name,
                TotalQuotes = totalQuotes,
                WonQuotes = won,
                LostQuotes = lost,
                WinRatePct = decided > 0
                    ? Math.Round(100m * won / decided, 1, MidpointRounding.AwayFromZero)
                    : null,
                OrdersLast24Months = orderCount,
                OrderValueLast24Months = SingleCurrencyValue(orderValueGroups, group => group.TotalAmount),
                OrderValueStatus = CurrencyAggregateStatus(orderValueGroups),
                OrderValueByCurrency = orderValueGroups,
                AvgQuoteTotal = SingleCurrencyValue(quoteValueGroups, group => group.AverageAmount),
                AvgQuoteTotalStatus = CurrencyAggregateStatus(quoteValueGroups),
                QuoteValueByCurrency = quoteValueGroups,
                AvgMarginPct = null,
                LastQuoteDate = await quoteQuery.MaxAsync(q => q.QuoteDate, ct),
                RecentQuotes = recentQuotes,
                RecentItemPrices = recentItemPrices,
                RecentRfqs = recentRfqs,
                RecentOrders = recentOrders,
                DemandProfile = demandProfile,
                Completeness = new CustomerContextCompletenessDTO
                {
                    QuoteAggregateScope = "all_history",
                    RecentQuoteLimit = RecentQuotesCount,
                    RecentQuotesTruncated = totalQuotes > RecentQuotesCount,
                    SoldOrderEvidenceLimit = SoldOrderEvidenceLimit,
                    SoldOrdersEvaluated = soldOrders.Count,
                    SoldOrderEvidenceTruncated = soldOrdersTruncated,
                    QuoteItemEvidenceLimit = QuoteItemEvidenceLimit,
                    QuoteItemsEvaluated = recentItems.Count,
                    QuoteItemEvidenceTruncated = quoteItemsTruncated,
                    DemandLookbackMonths = DemandLookbackMonths,
                    DemandCohortFrom = demandFrom,
                    DemandCohortTo = now,
                    DemandLineLimit = DemandLineEvaluationLimit,
                    DemandLinesEvaluated = demandLines.Count,
                    DemandLinesTruncated = demandTruncated
                },
                GeneratedAt = now
            });
        }

        private static List<CustomerCurrencyAggregateDTO> ToCurrencyGroups(
            IEnumerable<(long? CurrencyId, string? CurrencyCode, int RecordCount, decimal TotalAmount)> rows) =>
            rows.Select(row => new CustomerCurrencyAggregateDTO
                {
                    CurrencyId = row.CurrencyId,
                    CurrencyCode = row.CurrencyCode,
                    RecordCount = row.RecordCount,
                    TotalAmount = Math.Round(row.TotalAmount, 2, MidpointRounding.AwayFromZero),
                    AverageAmount = row.RecordCount == 0
                        ? null
                        : Math.Round(row.TotalAmount / row.RecordCount, 2, MidpointRounding.AwayFromZero)
                })
                .OrderBy(row => row.CurrencyCode ?? string.Empty, StringComparer.Ordinal)
                .ThenBy(row => row.CurrencyId)
                .ToList();

        private static bool IsWinningStatus(string? statusCode, long? statusId) =>
            statusId == 44 || statusCode is "ACCEPTED" or "ORDERED";

        private static string? ContactName(long? contactId, IReadOnlyDictionary<long, string> contacts) =>
            contactId.HasValue && contacts.TryGetValue(contactId.Value, out var name) ? name : null;

        private static decimal? SingleCurrencyValue(
            IReadOnlyCollection<CustomerCurrencyAggregateDTO> groups,
            Func<CustomerCurrencyAggregateDTO, decimal?> selector) =>
            groups.Count == 1 && groups.Single().CurrencyId.HasValue
                && !string.IsNullOrWhiteSpace(groups.Single().CurrencyCode)
                ? selector(groups.Single())
                : null;

        private static string CurrencyAggregateStatus(IReadOnlyCollection<CustomerCurrencyAggregateDTO> groups)
        {
            if (groups.Count == 0) return "no_data";
            if (groups.Count == 1 && groups.Single().CurrencyId.HasValue
                && !string.IsNullOrWhiteSpace(groups.Single().CurrencyCode)) return "single_currency";
            if (groups.Any(group => !group.CurrencyId.HasValue || string.IsNullOrWhiteSpace(group.CurrencyCode)))
                return groups.Count == 1 ? "currency_missing" : "mixed_currency_with_missing";
            return "mixed_currency";
        }

        private static int MonthsBetween(DateTime from, DateTime to) =>
            (to.Year - from.Year) * 12 + (to.Month - from.Month);

        private long GetBusinessUnitId() =>
            long.TryParse(User.FindFirst("businessUnitId")?.Value, out var id) ? id : 0;
    }

    public sealed class CustomerContextDTO
    {
        public long CustomerId { get; set; }
        public string? CustomerName { get; set; }
        public int TotalQuotes { get; set; }
        public int WonQuotes { get; set; }
        public int LostQuotes { get; set; }

        /// <summary>Won divided by won plus lost; null while no quote is decided.</summary>
        public decimal? WinRatePct { get; set; }

        public int OrdersLast24Months { get; set; }
        public decimal? OrderValueLast24Months { get; set; }
        public string OrderValueStatus { get; set; } = "no_data";
        public List<CustomerCurrencyAggregateDTO> OrderValueByCurrency { get; set; } = new();
        public decimal? AvgQuoteTotal { get; set; }
        public string AvgQuoteTotalStatus { get; set; } = "no_data";
        public List<CustomerCurrencyAggregateDTO> QuoteValueByCurrency { get; set; } = new();

        /// <summary>Null until an immutable quote-time cost snapshot exists.</summary>
        public decimal? AvgMarginPct { get; set; }

        public DateTime? LastQuoteDate { get; set; }
        public List<CustomerQuoteSummaryDTO> RecentQuotes { get; set; } = new();
        public List<CustomerItemPriceDTO> RecentItemPrices { get; set; } = new();
        public List<CustomerRfqSummaryDTO> RecentRfqs { get; set; } = new();
        public List<CustomerOrderSummaryDTO> RecentOrders { get; set; } = new();
        public List<CustomerDemandSummaryDTO> DemandProfile { get; set; } = new();
        public CustomerContextCompletenessDTO Completeness { get; set; } = new();
        public DateTime GeneratedAt { get; set; }
    }

    public sealed class CustomerCurrencyAggregateDTO
    {
        public long? CurrencyId { get; set; }
        public string? CurrencyCode { get; set; }
        public int RecordCount { get; set; }
        public decimal TotalAmount { get; set; }
        public decimal? AverageAmount { get; set; }
    }

    public sealed class CustomerContextCompletenessDTO
    {
        public string QuoteAggregateScope { get; set; } = "all_history";
        public int RecentQuoteLimit { get; set; }
        public bool RecentQuotesTruncated { get; set; }
        public int SoldOrderEvidenceLimit { get; set; }
        public int SoldOrdersEvaluated { get; set; }
        public bool SoldOrderEvidenceTruncated { get; set; }
        public int QuoteItemEvidenceLimit { get; set; }
        public int QuoteItemsEvaluated { get; set; }
        public bool QuoteItemEvidenceTruncated { get; set; }
        public int DemandLookbackMonths { get; set; }
        public DateTime DemandCohortFrom { get; set; }
        public DateTime DemandCohortTo { get; set; }
        public int DemandLineLimit { get; set; }
        public int DemandLinesEvaluated { get; set; }
        public bool DemandLinesTruncated { get; set; }
    }

    public sealed class CustomerQuoteSummaryDTO
    {
        public long QuoteId { get; set; }
        public string QuoteNo { get; set; } = string.Empty;
        public DateTime? QuoteDate { get; set; }
        public decimal? TotalAmount { get; set; }
        public long? CurrencyId { get; set; }
        public string? CurrencyCode { get; set; }
        public long? ContactId { get; set; }
        public string? ContactName { get; set; }
        public string? StatusValue { get; set; }
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
        public long AwardOrderId { get; set; }
        public long? CurrencyId { get; set; }
        public string? CurrencyCode { get; set; }
        public DateTime SoldOn { get; set; }

        /// <summary>Compatibility alias for SoldOn; this is not the quote date.</summary>
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
        public long? ContactId { get; set; }
        public string? ContactName { get; set; }
    }

    public sealed class CustomerOrderSummaryDTO
    {
        public long OrderId { get; set; }
        public string OrderNo { get; set; } = string.Empty;
        public DateTime OrderDate { get; set; }
        public string? Status { get; set; }
        public decimal TotalAmount { get; set; }
        public long? QuoteId { get; set; }
        public long? CurrencyId { get; set; }
        public string? CurrencyCode { get; set; }
        public long? ContactId { get; set; }
        public string? ContactName { get; set; }
    }

    public sealed class CustomerDemandSummaryDTO
    {
        public long? ProductId { get; set; }
        public string? PartNumber { get; set; }
        public string? Description { get; set; }
        public int InquiryCount { get; set; }
        public long RequestedQuantity { get; set; }
        public List<long> SourceRfqIds { get; set; } = new();
        public bool SourceRfqsTruncated { get; set; }
    }
}
