using System.Globalization;
using ERP_RFQ_Automation.Models;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Intelligence.Pricing;

/// <summary>
/// Implementation notes (see PRICING-WIRING.md for the full write-up):
///
/// SIGNALS, blended by priority weight × recency × quantity-similarity:
///   1. priceList       — View_SupplierPriceList (latest supplier list price per product).
///                        The view is mapped in the DbContext but is NOT created by any
///                        Postgres migration, so the query is wrapped defensively: if the
///                        view is absent the signal simply doesn't fire.
///   2. recentQuote     — QuoteItem.UnitPrice from this tenant's ACCEPTED quotes
///                        (StatusId 32 legacy / 44 current), recency-weighted, preferring
///                        comparable quantities.
///   3. supplierQuote   — SupplierQuotedItem rows captured for THIS rfq. RFQ linkage is
///                        encoded by the sourcing tools in QuoteReference as
///                        "rfq={id};item={id};lead={days}" — parsed here. Best (lowest)
///                        supplier cost + margin.
///   4. purchaseHistory — SupplierPurchaseHistory for the product: recency- and
///                        quantity-weighted average cost + margin.
///   5. productMaster   — Product.FinalSalesPrice/SellingPrice (sell side) or
///                        Product.FinalLandedCost/UnitCost + margin (cost side).
///
/// MARGIN — QuoteConfiguration is branding-only (logo/colors/T&amp;Cs; no margin column) and
/// no other tenant margin setting exists, so cost-side signals use a documented constant
/// default of 20% cost-plus (<see cref="DefaultMarginPct"/>).
///
/// CURRENCY — no FX conversion is invented. Each line is priced in its own currency
/// (Rfqitem.CurrencyId/Currency, falling back to the tenant base currency). A signal with
/// an EXPLICIT different currency is excluded; signals with no currency stamp (product
/// master, blank history rows) are assumed to be in the tenant base currency.
///
/// QUANTITY AWARENESS — when history rows carry quantities, each row's weight is
/// multiplied by min(qty, lineQty)/max(qty, lineQty), so quotes/purchases at comparable
/// volumes dominate the blend over wildly different volumes.
///
/// APPLY TARGET — ApplyPricingAsync writes Rfqitem.UnitPrice: RfqRepository.ApproveAsync
/// builds QuoteItems with "UnitPrice = i.UnitPrice ?? 0", so prices applied here flow
/// into the generated quote with zero changes to the existing approve path.
/// </summary>
public sealed class PricingEngine : IPricingEngine
{
    /// <summary>Default cost-plus margin (20%) — no tenant margin configuration exists.</summary>
    public const decimal DefaultMarginPct = 0.20m;

    // Quote.StatusId values meaning "accepted": 32 (legacy, see QuoteRepository stats)
    // and 44 (QuoteService.TransitionStatusAsync "ACCEPTED").
    private static readonly long[] AcceptedQuoteStatusIds = { 32L, 44L };

    private const int LookbackDays = 730;          // ignore signals older than ~24 months
    private const int MaxRowsPerSignal = 500;      // hard cap per batched signal query
    private const double RecencyHalfLifeDays = 180; // weight halves every ~6 months

    // Priority weights (highest-trust signal first, per spec).
    private const double WPriceList = 1.00;
    private const double WRecentQuote = 0.85;
    private const double WSupplierQuote = 0.70;
    private const double WPurchaseHistory = 0.55;
    private const double WProductMasterSell = 0.40;
    private const double WProductMasterCost = 0.35;

    private readonly ErpRfqAutomationContext _db;
    private readonly ILogger<PricingEngine> _logger;

    public PricingEngine(ErpRfqAutomationContext db, ILogger<PricingEngine> logger)
    {
        _db = db;
        _logger = logger;
    }

    // ------------------------------------------------------------------ preview

    public async Task<PricePreview> PriceRfqAsync(long rfqId, long businessUnitId, CancellationToken ct)
    {
        // Tenant check + line load in one round trip. The EF global query filter also
        // scopes Rfqs, but the explicit BusinessUnitId predicate keeps this correct even
        // on tenant-less context paths (background/agent execution).
        var rfq = await _db.Rfqs.AsNoTracking()
            .Where(r => r.Id == rfqId && r.BusinessUnitId == businessUnitId)
            .Select(r => new
            {
                r.Id,
                Items = r.Rfqitems
                    .OrderBy(i => i.Id)
                    .Select(i => new
                    {
                        i.Id,
                        i.ProductId,
                        Description = i.ProductShortDescription ?? i.ProductShortName ?? i.ItemText ?? i.CommodityProduct,
                        i.Quantity,
                        i.CurrencyId,
                        CurrencyText = i.Currency,
                        i.UnitPrice
                    })
                    .ToList()
            })
            .FirstOrDefaultAsync(ct);

        if (rfq is null)
            throw new KeyNotFoundException($"RFQ {rfqId} was not found in this business unit.");

        var now = DateTime.UtcNow;
        var cutoff = now.AddDays(-LookbackDays);
        var items = rfq.Items;
        var productIds = items.Where(i => i.ProductId.HasValue)
                              .Select(i => i.ProductId!.Value)
                              .Distinct()
                              .ToArray();

        // ---- batched signal fetches (one query per signal, never per line) ----

        var baseCurrency = await _db.Currencies.AsNoTracking()
            .Where(c => c.BusinessUnitId == businessUnitId && c.IsBaseCurrency == true)
            .Select(c => c.Code)
            .FirstOrDefaultAsync(ct);

        var lineCurrencyIds = items.Where(i => i.CurrencyId.HasValue)
                                   .Select(i => i.CurrencyId!.Value)
                                   .Distinct()
                                   .ToArray();
        var currencyCodeById = lineCurrencyIds.Length == 0
            ? new Dictionary<long, string>()
            : await _db.Currencies.AsNoTracking()
                .Where(c => lineCurrencyIds.Contains(c.Id))
                .ToDictionaryAsync(c => c.Id, c => c.Code, ct);

        // Signal 1 — supplier price list view (may not exist in the database yet).
        var priceListRows = await FetchPriceListRowsAsync(productIds, ct);

        // Signal 2 — this tenant's accepted quotes for the same products.
        var acceptedQuoteRows = productIds.Length == 0
            ? new List<AcceptedQuoteRow>()
            : await _db.QuoteItems.AsNoTracking()
                .Where(qi => qi.ProductId.HasValue && productIds.Contains(qi.ProductId.Value))
                .Where(qi => qi.Quote.BusinessUnitId == businessUnitId
                             && qi.Quote.StatusId.HasValue
                             && AcceptedQuoteStatusIds.Contains(qi.Quote.StatusId.Value))
                .Where(qi => (qi.Quote.QuoteDate ?? qi.CreatedDate) >= cutoff)
                .OrderByDescending(qi => qi.Quote.QuoteDate ?? qi.CreatedDate)
                .Take(MaxRowsPerSignal)
                .Select(qi => new AcceptedQuoteRow(
                    qi.ProductId!.Value,
                    qi.UnitPrice,
                    qi.Quantity,
                    qi.Quote.QuoteDate ?? qi.CreatedDate,
                    qi.Quote.Currency != null ? qi.Quote.Currency.Code : null,
                    qi.Quote.QuoteNo))
                .ToListAsync(ct);

        // Signal 3 — supplier quotes captured for THIS rfq (QuoteReference linkage).
        var refPrefix = $"rfq={rfqId};";
        var supplierQuoteRows = await _db.SupplierQuotedItems.AsNoTracking()
            .Where(sq => sq.IsActive
                         && sq.QuoteReference != null
                         && sq.QuoteReference.StartsWith(refPrefix)
                         && (sq.BusinessUnitId == null || sq.BusinessUnitId == businessUnitId)
                         && sq.UnitPrice.HasValue && sq.UnitPrice > 0)
            .Take(MaxRowsPerSignal)
            .Select(sq => new SupplierQuoteRow(
                sq.QuoteReference!,
                sq.UnitPrice!.Value,
                sq.Quantity,
                sq.QuoteDate,
                sq.Currency != null ? sq.Currency.Code : null,
                sq.Supplier.Name))
            .ToListAsync(ct);

        // Signal 4 — purchase history for the products (tenant-scoped by construction:
        // product ids come from this tenant's RFQ lines; Supplier nav is filter-scoped).
        var purchaseRows = productIds.Length == 0
            ? new List<PurchaseRow>()
            : await _db.SupplierPurchaseHistories.AsNoTracking()
                .Where(ph => productIds.Contains(ph.ProductId) && ph.PurchaseDate >= cutoff && ph.UnitPrice > 0)
                .OrderByDescending(ph => ph.PurchaseDate)
                .Take(MaxRowsPerSignal)
                .Select(ph => new PurchaseRow(
                    ph.ProductId, ph.UnitPrice, ph.Quantity, ph.PurchaseDate, ph.Currency, ph.Supplier.Name))
                .ToListAsync(ct);

        // Signal 5 — product master cost/price fields.
        var productById = productIds.Length == 0
            ? new Dictionary<long, ProductRow>()
            : await _db.Products.AsNoTracking()
                .Where(p => productIds.Contains(p.Id))
                .Select(p => new ProductRow(p.Id, p.ProductName, p.UnitCost, p.SellingPrice, p.FinalLandedCost, p.FinalSalesPrice))
                .ToDictionaryAsync(p => p.Id, ct);

        // Pre-group per product / per line for O(1) lookup while assembling lines.
        var priceListByProduct = priceListRows.GroupBy(v => v.ProductId).ToDictionary(g => g.Key, g => g.ToList());
        var quotesByProduct = acceptedQuoteRows.GroupBy(q => q.ProductId).ToDictionary(g => g.Key, g => g.ToList());
        var purchasesByProduct = purchaseRows.GroupBy(p => p.ProductId).ToDictionary(g => g.Key, g => g.ToList());
        var supplierQuotesByItem = supplierQuoteRows
            .Select(r => (ItemId: ParseQuoteReferenceItemId(r.QuoteReference), Row: r))
            .Where(t => t.ItemId.HasValue)
            .GroupBy(t => t.ItemId!.Value)
            .ToDictionary(g => g.Key, g => g.Select(t => t.Row).ToList());

        // ---- per-line blending ----

        var preview = new PricePreview { RfqId = rfq.Id };
        string? previewCurrency = null;

        foreach (var item in items)
        {
            var targetCurrency = ResolveLineCurrency(item.CurrencyId, item.CurrencyText, currencyCodeById, baseCurrency);
            previewCurrency ??= targetCurrency;

            var lineQty = (decimal)item.Quantity;
            var candidates = new List<Candidate>();

            if (item.ProductId is long pid)
            {
                if (priceListByProduct.TryGetValue(pid, out var plRows))
                    AddPriceListCandidate(candidates, plRows, targetCurrency, baseCurrency, now);
                if (quotesByProduct.TryGetValue(pid, out var aqRows))
                    AddRecentQuoteCandidate(candidates, aqRows, targetCurrency, baseCurrency, lineQty, now);
                if (purchasesByProduct.TryGetValue(pid, out var phRows))
                    AddPurchaseHistoryCandidate(candidates, phRows, targetCurrency, baseCurrency, lineQty, now);
                if (productById.TryGetValue(pid, out var product))
                    AddProductMasterCandidate(candidates, product, targetCurrency, baseCurrency);
            }

            if (supplierQuotesByItem.TryGetValue(item.Id, out var sqRows))
                AddSupplierQuoteCandidate(candidates, sqRows, targetCurrency, baseCurrency, now);

            preview.Lines.Add(BuildLine(
                item.Id,
                item.Description ?? $"Line {item.Id}",
                lineQty,
                candidates));
        }

        preview.Currency = previewCurrency ?? baseCurrency ?? "N/A";
        preview.Totals.RecommendedTotal = Round2(preview.Lines.Sum(l => l.Quantity * l.RecommendedUnitPrice));
        preview.OverallConfidence = preview.Lines.Count == 0
            ? 0m
            : Math.Round(preview.Lines.Average(l => l.Confidence), 2, MidpointRounding.AwayFromZero);

        return preview;
    }

    // ------------------------------------------------------------------ apply

    public async Task<ApplyPricingResult> ApplyPricingAsync(long rfqId, long businessUnitId, ApplyPricingRequest req, CancellationToken ct)
    {
        if (req is null || req.Lines is null || req.Lines.Count == 0)
            throw new ArgumentException("At least one pricing line is required.");

        // Tenant check first — same explicit BU predicate the approve flow uses (SEC-07).
        var rfq = await _db.Rfqs
            .Include(r => r.Rfqitems)
            .FirstOrDefaultAsync(r => r.Id == rfqId && r.BusinessUnitId == businessUnitId, ct);

        if (rfq is null)
            throw new KeyNotFoundException($"RFQ {rfqId} was not found in this business unit.");

        var itemsById = rfq.Rfqitems.ToDictionary(i => i.Id);
        var seen = new HashSet<long>();

        foreach (var line in req.Lines)
        {
            if (!seen.Add(line.RfqItemId))
                throw new ArgumentException($"Duplicate rfqItemId {line.RfqItemId} in request.");
            if (!itemsById.ContainsKey(line.RfqItemId))
                throw new ArgumentException($"RFQ item {line.RfqItemId} does not belong to RFQ {rfqId}.");
            if (line.UnitPrice <= 0)
                throw new ArgumentException($"Unit price for RFQ item {line.RfqItemId} must be greater than zero.");
        }

        var actor = string.IsNullOrWhiteSpace(req.AppliedBy) ? "PricingIntelligence" : req.AppliedBy!;
        var stamp = DateTime.UtcNow;

        foreach (var line in req.Lines)
        {
            // Rfqitem.UnitPrice is the exact source RfqRepository.ApproveAsync reads
            // ("UnitPrice = i.UnitPrice ?? 0") when it generates the Quote.
            var item = itemsById[line.RfqItemId];
            item.UnitPrice = line.UnitPrice;
            item.ModifiedBy = actor;
            item.ModifiedDate = stamp;
        }

        await _db.SaveChangesAsync(ct);

        return new ApplyPricingResult
        {
            Applied = req.Lines.Count,
            Total = Round2(rfq.Rfqitems.Sum(i => i.Quantity * (i.UnitPrice ?? 0m)))
        };
    }

    // ------------------------------------------------------------------ signal candidates

    private async Task<List<PriceListRow>> FetchPriceListRowsAsync(long[] productIds, CancellationToken ct)
    {
        if (productIds.Length == 0) return new();
        try
        {
            return await _db.ViewSupplierPriceLists.AsNoTracking()
                .Where(v => productIds.Contains(v.ProductId))
                .Take(MaxRowsPerSignal)
                .Select(v => new PriceListRow(v.ProductId, v.LatestPrice, v.Currency, v.LastPurchasedDate, v.SupplierName))
                .ToListAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // View_SupplierPriceList is mapped in the EF model but is not created by any
            // Postgres migration — on databases without it the signal must simply not fire.
            _logger.LogDebug(ex, "Supplier price list view unavailable; priceList signal skipped.");
            return new();
        }
    }

    private static void AddPriceListCandidate(List<Candidate> candidates, List<PriceListRow> rows,
        string? targetCurrency, string? baseCurrency, DateTime now)
    {
        var usable = rows.Where(r => r.LatestPrice > 0 && CurrencyOk(r.Currency, targetCurrency, baseCurrency))
                         .OrderByDescending(r => r.LastPurchasedDate)
                         .ToList();
        if (usable.Count == 0) return;

        var best = usable[0];
        var cost = best.LatestPrice;
        var price = ApplyMargin(cost);
        candidates.Add(new Candidate(
            PriceSignalSources.PriceList,
            "Supplier price list",
            price,
            WPriceList * RecencyWeight(best.LastPurchasedDate, now),
            $"Latest list price {Fmt(cost)} {best.Currency ?? baseCurrency ?? ""} from {best.SupplierName} " +
            $"({FmtMonth(best.LastPurchasedDate)}) plus {FmtMargin(DefaultMarginPct)} margin.",
            CostBasis: cost,
            Date: best.LastPurchasedDate,
            RefText: best.SupplierName));
    }

    private static void AddRecentQuoteCandidate(List<Candidate> candidates, List<AcceptedQuoteRow> rows,
        string? targetCurrency, string? baseCurrency, decimal lineQty, DateTime now)
    {
        // Sell-side signal: no margin applied. Prefer recent AND comparable-quantity quotes.
        var scored = rows
            .Where(r => r.UnitPrice > 0 && CurrencyOk(r.CurrencyCode, targetCurrency, baseCurrency))
            .Select(r => (Row: r, Eff: RecencyWeight(r.Date, now) * QtySimilarity(r.Quantity, lineQty)))
            .OrderByDescending(t => t.Eff)
            .ToList();
        if (scored.Count == 0) return;

        var (best, eff) = scored[0];
        candidates.Add(new Candidate(
            PriceSignalSources.RecentQuote,
            "Recent accepted quote",
            best.UnitPrice,
            WRecentQuote * eff,
            $"Accepted quote {best.QuoteNo} ({FmtMonth(best.Date)}) at {Fmt(best.UnitPrice)} " +
            $"{best.CurrencyCode ?? baseCurrency ?? ""} for qty {Fmt(best.Quantity)}; " +
            $"{scored.Count} accepted quote(s) on record.",
            CostBasis: null,
            Date: best.Date,
            RefText: best.QuoteNo));
    }

    private static void AddSupplierQuoteCandidate(List<Candidate> candidates, List<SupplierQuoteRow> rows,
        string? targetCurrency, string? baseCurrency, DateTime now)
    {
        var usable = rows.Where(r => CurrencyOk(r.CurrencyCode, targetCurrency, baseCurrency))
                         .OrderBy(r => r.UnitPrice)
                         .ToList();
        if (usable.Count == 0) return;

        var best = usable[0]; // best (lowest) supplier cost for this exact RFQ line
        var price = ApplyMargin(best.UnitPrice);
        candidates.Add(new Candidate(
            PriceSignalSources.SupplierQuote,
            "Supplier quote for this RFQ",
            price,
            WSupplierQuote * RecencyWeight(best.QuoteDate ?? now, now),
            $"Best of {usable.Count} supplier quote(s) captured for this RFQ: {Fmt(best.UnitPrice)} " +
            $"{best.CurrencyCode ?? baseCurrency ?? ""} from {best.SupplierName} plus {FmtMargin(DefaultMarginPct)} margin.",
            CostBasis: best.UnitPrice,
            Date: best.QuoteDate,
            RefText: best.SupplierName));
    }

    private static void AddPurchaseHistoryCandidate(List<Candidate> candidates, List<PurchaseRow> rows,
        string? targetCurrency, string? baseCurrency, decimal lineQty, DateTime now)
    {
        var usable = rows.Where(r => CurrencyOk(r.Currency, targetCurrency, baseCurrency)).ToList();
        if (usable.Count == 0) return;

        // Recency- and quantity-weighted average cost across the purchase history.
        double weightSum = 0;
        decimal weightedCost = 0;
        foreach (var r in usable)
        {
            var w = RecencyWeight(r.PurchaseDate, now) * QtySimilarity(r.Quantity, lineQty);
            weightSum += w;
            weightedCost += r.UnitPrice * (decimal)w;
        }
        if (weightSum <= 0) return;

        var avgCost = weightedCost / (decimal)weightSum;
        var latest = usable.OrderByDescending(r => r.PurchaseDate).First();
        var price = ApplyMargin(avgCost);

        candidates.Add(new Candidate(
            PriceSignalSources.PurchaseHistory,
            "Purchase history",
            price,
            WPurchaseHistory * RecencyWeight(latest.PurchaseDate, now),
            $"Recency/quantity-weighted avg cost {Fmt(avgCost)} {latest.Currency ?? baseCurrency ?? ""} across " +
            $"{usable.Count} purchase(s) (last {FmtMonth(latest.PurchaseDate)} from {latest.SupplierName}) " +
            $"plus {FmtMargin(DefaultMarginPct)} margin.",
            CostBasis: Round4(avgCost),
            Date: latest.PurchaseDate,
            RefText: latest.SupplierName));
    }

    private static void AddProductMasterCandidate(List<Candidate> candidates, ProductRow product,
        string? targetCurrency, string? baseCurrency)
    {
        // Product master has no currency column — values are assumed to be in the tenant
        // base currency, so the signal only fires when that is compatible with the line.
        if (!CurrencyOk(null, targetCurrency, baseCurrency)) return;

        var sell = FirstPositive(product.FinalSalesPrice, product.SellingPrice);
        var cost = FirstPositive(product.FinalLandedCost, product.UnitCost);

        if (sell.HasValue)
        {
            candidates.Add(new Candidate(
                PriceSignalSources.ProductMaster,
                "Product master selling price",
                sell.Value,
                WProductMasterSell,
                $"Master selling price {Fmt(sell.Value)} for {product.ProductName ?? "product"}" +
                (cost.HasValue ? $" (master cost {Fmt(cost.Value)})." : "."),
                CostBasis: cost,
                Date: null,
                RefText: product.ProductName));
        }
        else if (cost.HasValue)
        {
            candidates.Add(new Candidate(
                PriceSignalSources.ProductMaster,
                "Product master cost + margin",
                ApplyMargin(cost.Value),
                WProductMasterCost,
                $"Master unit cost {Fmt(cost.Value)} for {product.ProductName ?? "product"} " +
                $"plus {FmtMargin(DefaultMarginPct)} margin.",
                CostBasis: cost,
                Date: null,
                RefText: product.ProductName));
        }
    }

    // ------------------------------------------------------------------ blending

    private static PriceLine BuildLine(long rfqItemId, string description, decimal quantity, List<Candidate> candidates)
    {
        var line = new PriceLine
        {
            RfqItemId = rfqItemId,
            Description = description,
            Quantity = quantity
        };

        if (candidates.Count == 0)
        {
            line.Rationale = "No pricing history found for this line — set a price manually.";
            line.Confidence = 0m;
            line.NeedsAttention = true;
            return line;
        }

        var ordered = candidates.OrderByDescending(c => c.Weight).ToList();
        var dominant = ordered[0];

        var totalWeight = ordered.Sum(c => c.Weight);
        var recommended = Round4(ordered.Aggregate(0m, (acc, c) => acc + c.Price * (decimal)(c.Weight / totalWeight)));

        // Floor = cost basis (no margin) of the most trusted cost-side signal; when only
        // sell-side signals exist, back the default margin out of the recommendation and
        // flag it as derived (never invents a cost we don't have — it is documented math).
        var costSignal = ordered.FirstOrDefault(c => c.CostBasis.HasValue && c.CostBasis.Value > 0);
        var floorDerived = costSignal is null;
        var floor = Round4(costSignal?.CostBasis ?? recommended / (1 + DefaultMarginPct));

        var marginPct = floor > 0 ? Math.Round((recommended - floor) / floor, 4, MidpointRounding.AwayFromZero) : 0m;

        // Confidence: dominant effective weight (already priority × recency × qty),
        // plus a small bonus per corroborating signal, plus an agreement bonus when the
        // candidates cluster tightly. Clamped to [0.05, 0.98] — never absolute certainty.
        var prices = ordered.Select(c => c.Price).ToList();
        var spread = recommended > 0 ? (double)((prices.Max() - prices.Min()) / recommended) : 0.0;
        var agreement = ordered.Count > 1 ? Math.Max(0.0, 0.15 * (1.0 - Math.Min(1.0, spread / 0.25))) : 0.0;
        var confidence = Math.Clamp(dominant.Weight + 0.05 * (ordered.Count - 1) + agreement, 0.05, 0.98);

        line.RecommendedUnitPrice = recommended;
        line.FloorUnitPrice = floor;
        line.MarginPct = marginPct;
        line.Confidence = Math.Round((decimal)confidence, 2, MidpointRounding.AwayFromZero);
        line.NeedsAttention = line.Confidence < 0.5m;
        line.Rationale = BuildRationale(dominant, ordered.Count, floorDerived);
        line.Signals = ordered.Select(c => new PriceSignal
        {
            Source = c.Source,
            Label = c.Label,
            Value = Round4(c.Price),
            Detail = c.Detail
        }).ToList();

        return line;
    }

    private static string BuildRationale(Candidate dominant, int signalCount, bool floorDerived)
    {
        var corroboration = signalCount > 1 ? $", corroborated by {signalCount - 1} other signal(s)" : "";
        var margin = FmtMargin(DefaultMarginPct);

        var core = dominant.Source switch
        {
            PriceSignalSources.PriceList =>
                $"Based on the latest supplier price list entry for this product ({FmtMonth(dominant.Date)}) plus {margin} margin",
            PriceSignalSources.RecentQuote =>
                $"Based on your last accepted quote for this product ({FmtMonth(dominant.Date)})",
            PriceSignalSources.SupplierQuote =>
                $"Based on the best supplier quote captured for this RFQ ({dominant.RefText}) plus {margin} margin",
            PriceSignalSources.PurchaseHistory =>
                $"Based on recent purchase history for this product (last purchase {FmtMonth(dominant.Date)}) plus {margin} margin",
            _ => dominant.CostBasis.HasValue && dominant.Label.Contains("cost", StringComparison.OrdinalIgnoreCase)
                ? $"Based on the product master unit cost plus {margin} margin; no recent market signals were found"
                : "Based on the product master selling price; no recent market signals were found"
        };

        return core + corroboration + (floorDerived ? " (floor derived from the recommendation, no cost signal available)." : ".");
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>Parses the sourcing-tool linkage "rfq={id};item={id};lead={days}".</summary>
    internal static long? ParseQuoteReferenceItemId(string? reference)
    {
        if (string.IsNullOrWhiteSpace(reference)) return null;
        foreach (var part in reference.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = part.Split('=', 2);
            if (kv.Length == 2
                && kv[0].Trim().Equals("item", StringComparison.OrdinalIgnoreCase)
                && long.TryParse(kv[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
                return id;
        }
        return null;
    }

    private static string? ResolveLineCurrency(long? currencyId, string? currencyText,
        IReadOnlyDictionary<long, string> codeById, string? baseCurrency)
    {
        if (currencyId.HasValue && codeById.TryGetValue(currencyId.Value, out var code) && !string.IsNullOrWhiteSpace(code))
            return code.Trim();
        if (!string.IsNullOrWhiteSpace(currencyText))
            return currencyText.Trim();
        return baseCurrency;
    }

    /// <summary>
    /// Same-currency policy: a signal with an explicit currency must match the line's
    /// currency; signals without a currency stamp are assumed to be in the tenant base
    /// currency and are accepted when the line is (or falls back to) that base currency.
    /// </summary>
    private static bool CurrencyOk(string? signalCurrency, string? targetCurrency, string? baseCurrency)
    {
        if (string.IsNullOrWhiteSpace(targetCurrency)) return true;
        var effectiveSignal = string.IsNullOrWhiteSpace(signalCurrency) ? baseCurrency : signalCurrency;
        if (string.IsNullOrWhiteSpace(effectiveSignal)) return true; // nothing to compare — accept, don't convert
        return string.Equals(effectiveSignal.Trim(), targetCurrency.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private static double RecencyWeight(DateTime? date, DateTime now)
    {
        if (!date.HasValue) return 0.6; // unknown date: middling trust
        var days = Math.Max(0.0, (now - date.Value).TotalDays);
        return Math.Clamp(Math.Pow(0.5, days / RecencyHalfLifeDays), 0.05, 1.0);
    }

    private static double QtySimilarity(decimal signalQty, decimal lineQty)
    {
        if (signalQty <= 0 || lineQty <= 0) return 1.0; // no quantity info: neutral
        var min = (double)Math.Min(signalQty, lineQty);
        var max = (double)Math.Max(signalQty, lineQty);
        return Math.Clamp(min / max, 0.1, 1.0);
    }

    private static decimal ApplyMargin(decimal cost) => Round4(cost * (1 + DefaultMarginPct));

    private static decimal? FirstPositive(params decimal?[] values) =>
        values.FirstOrDefault(v => v.HasValue && v.Value > 0);

    private static decimal Round4(decimal v) => Math.Round(v, 4, MidpointRounding.AwayFromZero);
    private static decimal Round2(decimal v) => Math.Round(v, 2, MidpointRounding.AwayFromZero);

    private static string Fmt(decimal v) => v.ToString("0.##", CultureInfo.InvariantCulture);
    private static string FmtMargin(decimal pct) => $"{pct * 100:0}%";
    private static string FmtMonth(DateTime? d) =>
        d.HasValue ? d.Value.ToString("MMM yyyy", CultureInfo.InvariantCulture) : "date unknown";

    // ------------------------------------------------------------------ internal rows

    private sealed record Candidate(
        string Source, string Label, decimal Price, double Weight, string Detail,
        decimal? CostBasis, DateTime? Date, string? RefText);

    private sealed record PriceListRow(long ProductId, decimal LatestPrice, string? Currency, DateTime LastPurchasedDate, string SupplierName);
    private sealed record AcceptedQuoteRow(long ProductId, decimal UnitPrice, decimal Quantity, DateTime? Date, string? CurrencyCode, string QuoteNo);
    private sealed record SupplierQuoteRow(string QuoteReference, decimal UnitPrice, decimal Quantity, DateTime? QuoteDate, string? CurrencyCode, string SupplierName);
    private sealed record PurchaseRow(long ProductId, decimal UnitPrice, decimal Quantity, DateTime PurchaseDate, string? Currency, string SupplierName);
    private sealed record ProductRow(long Id, string? ProductName, decimal? UnitCost, decimal? SellingPrice, decimal? FinalLandedCost, decimal? FinalSalesPrice);
}

/// <summary>Stable signal source identifiers (wire contract values).</summary>
public static class PriceSignalSources
{
    public const string PriceList = "priceList";
    public const string RecentQuote = "recentQuote";
    public const string SupplierQuote = "supplierQuote";
    public const string PurchaseHistory = "purchaseHistory";
    public const string ProductMaster = "productMaster";
}
