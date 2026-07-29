using System.Globalization;
using ERP_RFQ_Automation.Models;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Intelligence.Pricing;

/// <summary>
/// Implementation notes (see PRICING-WIRING.md for the full write-up):
///
/// SHADOW SIGNALS, blended by priority weight × recency × quantity-similarity:
///   1. recentQuote     — QuoteItem.UnitPrice from this tenant's accepted quotes
///                        (StatusId 32 legacy / 44 current), recency-weighted, preferring
///                        comparable quantities.
///
/// Legacy price-list, purchase-history, raw SupplierQuotedItem and unstamped Product master signals are
/// excluded because they do not provide the tenant-qualified canonical evidence and
/// currency contract required for financial authority. No synthetic cost or approval
/// floor is inferred.
///
/// CURRENCY — no FX conversion is invented. Each line is priced in its own currency
/// (Rfqitem.CurrencyId/Currency). A signal with a different or missing explicit currency
/// is excluded. Unknown currency evidence is excluded.
///
/// QUANTITY AWARENESS — when accepted Quote rows carry quantities, each row's weight is
/// multiplied by min(qty, lineQty)/max(qty, lineQty), so quotes at comparable
/// volumes dominate the blend over wildly different volumes.
///
/// Direct application is disabled: predictions never mutate confirmed commercial state.
/// </summary>
public sealed class PricingEngine : IPricingEngine
{
    // Quote.StatusId values meaning "accepted": 32 (legacy, see QuoteRepository stats)
    // and 44 (QuoteService.TransitionStatusAsync "ACCEPTED").
    private static readonly long[] AcceptedQuoteStatusIds = { 32L, 44L };

    private const int LookbackDays = 730;          // ignore signals older than ~24 months
    private const int MaxRowsPerSignal = 500;      // hard cap per batched signal query
    private const double RecencyHalfLifeDays = 180; // weight halves every ~6 months

    // Priority weight for the only evidence source admitted to this shadow policy.
    private const double WRecentQuote = 0.85;

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

        var lineCurrencyIds = items.Where(i => i.CurrencyId.HasValue)
                                   .Select(i => i.CurrencyId!.Value)
                                   .Distinct()
                                   .ToArray();
        var currencyCodeById = lineCurrencyIds.Length == 0
            ? new Dictionary<long, string>()
            : await _db.Currencies.AsNoTracking()
                .Where(c => c.BusinessUnitId == businessUnitId && lineCurrencyIds.Contains(c.Id))
                .ToDictionaryAsync(c => c.Id, c => c.Code, ct);

        // Signal 1 — this tenant's accepted quotes for the same products.
        var acceptedQuoteRowsRaw = productIds.Length == 0
            ? new List<AcceptedQuoteRow>()
            : await _db.QuoteItems.AsNoTracking()
                .Where(qi => qi.ProductId.HasValue && productIds.Contains(qi.ProductId.Value))
                .Where(qi => qi.Quote.BusinessUnitId == businessUnitId
                             && qi.Quote.StatusId.HasValue
                             && qi.Quote.CurrencyId.HasValue
                             && qi.Quote.Currency != null
                             && AcceptedQuoteStatusIds.Contains(qi.Quote.StatusId.Value))
                .Where(qi => (qi.Quote.QuoteDate ?? qi.CreatedDate) >= cutoff)
                .OrderByDescending(qi => qi.Quote.QuoteDate ?? qi.CreatedDate)
                .Take(Math.Min(10_000, Math.Max(MaxRowsPerSignal, productIds.Length * 250)))
                .Select(qi => new AcceptedQuoteRow(
                    qi.ProductId!.Value,
                    qi.UnitPrice,
                    qi.Quantity,
                    qi.Quote.QuoteDate ?? qi.CreatedDate,
                    qi.Quote.Currency!.Code,
                    qi.Quote.QuoteNo))
                .ToListAsync(ct);
        var acceptedQuoteRows = acceptedQuoteRowsRaw.GroupBy(x => x.ProductId)
            .SelectMany(group => group.OrderByDescending(x => x.Date).Take(100)).ToList();

        // Pre-group per product / per line for O(1) lookup while assembling lines.
        var quotesByProduct = acceptedQuoteRows.GroupBy(q => q.ProductId).ToDictionary(g => g.Key, g => g.ToList());

        // ---- per-line blending ----

        var preview = new PricePreview { RfqId = rfq.Id };
        foreach (var item in items)
        {
            var targetCurrency = ResolveLineCurrency(item.CurrencyId, item.CurrencyText, currencyCodeById);

            var lineQty = (decimal)item.Quantity;
            var candidates = new List<Candidate>();

            if (item.ProductId is long pid)
            {
                if (quotesByProduct.TryGetValue(pid, out var aqRows))
                    AddRecentQuoteCandidate(candidates, aqRows, targetCurrency, lineQty, now);
            }

            preview.Lines.Add(BuildLine(
                item.Id,
                item.Description ?? $"Line {item.Id}",
                lineQty,
                targetCurrency,
                candidates));
        }

        var totals = preview.Lines.Where(x => !string.IsNullOrWhiteSpace(x.Currency) && x.RecommendedUnitPrice > 0)
            .GroupBy(x => x.Currency!, StringComparer.OrdinalIgnoreCase)
            .Select(group => new CurrencyPriceTotal(group.Key,
                Round2(group.Sum(line => line.Quantity * line.RecommendedUnitPrice)), group.Count()))
            .OrderBy(x => x.Currency, StringComparer.OrdinalIgnoreCase).ToList();
        preview.Totals.ByCurrency = totals;
        preview.Totals.PricedLineCount = totals.Sum(x => x.LineCount);
        preview.Totals.UnpricedLineCount = preview.Lines.Count - preview.Totals.PricedLineCount;
        preview.Currency = totals.Count == 1 ? totals[0].Currency : null;
        preview.Totals.RecommendedTotal = totals.Count == 1 && preview.Totals.UnpricedLineCount == 0
            ? totals[0].RecommendedTotal
            : null;
        preview.OverallConfidence = preview.Lines.Count == 0
            ? 0m
            : Math.Round(preview.Lines.Average(l => l.Confidence), 2, MidpointRounding.AwayFromZero);

        return preview;
    }

    // ------------------------------------------------------------------ apply

    public async Task<ApplyPricingResult> ApplyPricingAsync(long rfqId, long businessUnitId, ApplyPricingRequest req, CancellationToken ct)
    {
        await Task.CompletedTask;
        throw new InvalidOperationException(
            "Shadow pricing cannot mutate RFQ prices. Use the governed Supplier award to Customer Quote pricing workflow.");
    }

    // ------------------------------------------------------------------ signal candidates

    private static void AddRecentQuoteCandidate(List<Candidate> candidates, List<AcceptedQuoteRow> rows,
        string? targetCurrency, decimal lineQty, DateTime now)
    {
        // Sell-side signal: no margin applied. Prefer recent AND comparable-quantity quotes.
        var scored = rows
            .Where(r => r.UnitPrice > 0 && CurrencyOk(r.CurrencyCode, targetCurrency))
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
            $"{best.CurrencyCode} for qty {Fmt(best.Quantity)}; " +
            $"{scored.Count} accepted quote(s) on record.",
            CostBasis: null,
            Date: best.Date,
            RefText: best.QuoteNo));
    }

    // ------------------------------------------------------------------ blending

    private static PriceLine BuildLine(long rfqItemId, string description, decimal quantity,
        string? currency, List<Candidate> candidates)
    {
        var line = new PriceLine
        {
            RfqItemId = rfqItemId,
            Description = description,
            Quantity = quantity,
            Currency = currency
        };

        if (candidates.Count == 0)
        {
            line.Rationale = "No governed pricing evidence is available for this line; review it in the Customer Quote workflow.";
            line.Confidence = 0m;
            line.NeedsAttention = true;
            return line;
        }

        var ordered = candidates.OrderByDescending(c => c.Weight).ToList();
        var dominant = ordered[0];

        var totalWeight = ordered.Sum(c => c.Weight);
        var recommended = Round4(ordered.Aggregate(0m, (acc, c) => acc + c.Price * (decimal)(c.Weight / totalWeight)));

        decimal? floor = null;
        decimal? marginPct = null;

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
        line.Rationale = BuildRationale(dominant, ordered.Count);
        line.Signals = ordered.Select(c => new PriceSignal
        {
            Source = c.Source,
            Label = c.Label,
            Value = Round4(c.Price),
            Detail = c.Detail
        }).ToList();

        return line;
    }

    private static string BuildRationale(Candidate dominant, int signalCount)
    {
        var corroboration = signalCount > 1 ? $", corroborated by {signalCount - 1} other signal(s)" : "";
        return $"Based on your last accepted quote for this product ({FmtMonth(dominant.Date)})" +
            corroboration + " (shadow advice; no authoritative cost floor).";
    }

    // ------------------------------------------------------------------ helpers

    private static string? ResolveLineCurrency(long? currencyId, string? currencyText,
        IReadOnlyDictionary<long, string> codeById)
    {
        if (currencyId.HasValue && codeById.TryGetValue(currencyId.Value, out var code) && !string.IsNullOrWhiteSpace(code))
            return code.Trim();
        if (!string.IsNullOrWhiteSpace(currencyText))
            return currencyText.Trim();
        return null;
    }

    /// <summary>
    /// Same-currency policy: both the signal and target line require explicit matching
    /// currency evidence. No base-currency inference is allowed.
    /// </summary>
    private static bool CurrencyOk(string? signalCurrency, string? targetCurrency)
    {
        if (string.IsNullOrWhiteSpace(targetCurrency)) return false;
        if (string.IsNullOrWhiteSpace(signalCurrency)) return false;
        return string.Equals(signalCurrency.Trim(), targetCurrency.Trim(), StringComparison.OrdinalIgnoreCase);
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

    private static decimal Round4(decimal v) => Math.Round(v, 4, MidpointRounding.AwayFromZero);
    private static decimal Round2(decimal v) => Math.Round(v, 2, MidpointRounding.AwayFromZero);

    private static string Fmt(decimal v) => v.ToString("0.##", CultureInfo.InvariantCulture);
    private static string FmtMonth(DateTime? d) =>
        d.HasValue ? d.Value.ToString("MMM yyyy", CultureInfo.InvariantCulture) : "date unknown";

    // ------------------------------------------------------------------ internal rows

    private sealed record Candidate(
        string Source, string Label, decimal Price, double Weight, string Detail,
        decimal? CostBasis, DateTime? Date, string? RefText);

    private sealed record AcceptedQuoteRow(long ProductId, decimal UnitPrice, decimal Quantity, DateTime? Date, string CurrencyCode, string QuoteNo);
}

/// <summary>Stable signal source identifiers (wire contract values).</summary>
public static class PriceSignalSources
{
    public const string RecentQuote = "recentQuote";
}
