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
/// COST FLOOR — separate from the blend and not a signal at all. PriceLine.FloorUnitPrice is the
/// awarded supplier's landed unit cost for that RFQ line (CustomerQuoteSourcingDecision), carried
/// with its own currency in PriceLine.FloorCurrency. It is READ, never derived: it is the identical
/// figure the governed customer price was built from, so the price the customer sees and the floor
/// it is checked against can never drift apart. A line with no awarded sourcing decision has NO
/// floor and stays null — never 0, which would assert that any price is acceptable.
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

        // THE COST FLOOR. Not a signal and not a blend: the awarded supplier's landed unit cost,
        // recorded on CustomerQuoteSourcingDecision by the governed award-to-quote pricing bridge.
        // It is the same number the customer price was derived from (landed / (1 - margin)), which
        // is exactly why it is the honest floor — the two cannot drift apart.
        //
        // MOST RECENT decision per RFQ line wins. Re-pricing a line appends a new decision row
        // (the table is append-only, keyed by idempotency key), so the latest row is the sourcing
        // that is actually in force. A line with NO decision has NO floor and stays null — see
        // PriceLine.FloorUnitPrice: null is a visible gap, zero would be a lie.
        var sourcingDecisions = await _db.CustomerQuoteSourcingDecisions.AsNoTracking()
            .Where(d => d.BusinessUnitId == businessUnitId && d.RfqId == rfqId)
            .Select(d => new { d.Id, d.RfqItemId, d.SupplierLandedUnitCost, d.CurrencyId, d.CreatedOn })
            .ToListAsync(ct);
        var floorByRfqItem = sourcingDecisions
            .GroupBy(d => d.RfqItemId)
            .ToDictionary(g => g.Key,
                g => g.OrderByDescending(d => d.CreatedOn).ThenByDescending(d => d.Id).First());

        var lineCurrencyIds = items.Where(i => i.CurrencyId.HasValue)
                                   .Select(i => i.CurrencyId!.Value)
                                   .Concat(floorByRfqItem.Values.Select(d => d.CurrencyId))
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

            decimal? floor = null;
            string? floorCurrency = null;
            string? floorBasis = null;
            if (floorByRfqItem.TryGetValue(item.Id, out var decision))
            {
                floor = decision.SupplierLandedUnitCost;
                // The floor's currency is the AWARD's currency, not the RFQ line's. When the code
                // cannot be named the floor is still published — dropping it would fail OPEN — and
                // the guard refuses the comparison instead (BelowFloorGuard, fail-closed branch).
                floorCurrency = currencyCodeById.TryGetValue(decision.CurrencyId, out var fc)
                    && !string.IsNullOrWhiteSpace(fc) ? fc.Trim() : null;
                floorBasis = floorCurrency is null
                    ? $"Awarded supplier landed unit cost {Fmt(floor.Value)} from sourcing decision " +
                      $"{decision.Id} ({FmtMonth(decision.CreatedOn)}); its currency could not be identified, " +
                      "so no price can be checked against it until an approved exchange rate exists."
                    : $"Cost floor {Fmt(floor.Value)} {floorCurrency} — the awarded supplier's landed " +
                      $"unit cost from sourcing decision {decision.Id} ({FmtMonth(decision.CreatedOn)}).";
            }

            preview.Lines.Add(BuildLine(
                item.Id,
                item.Description ?? $"Line {item.Id}",
                lineQty,
                targetCurrency,
                candidates,
                floor,
                floorCurrency,
                floorBasis));
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
            RefText: best.QuoteNo,
            CurrencyCode: best.CurrencyCode));
    }

    // ------------------------------------------------------------------ blending

    private static PriceLine BuildLine(long rfqItemId, string description, decimal quantity,
        string? currency, List<Candidate> candidates,
        decimal? floor, string? floorCurrency, string? floorBasis)
    {
        var line = new PriceLine
        {
            RfqItemId = rfqItemId,
            Description = description,
            Quantity = quantity,
            Currency = currency,
            // The floor is set BEFORE every early return below, deliberately. It comes from the
            // award, not from the sell-side blend, so a line with an awarded cost and no accepted
            // quote history still has a floor — and that is precisely the first-time item the send
            // gate used to wave through with nothing to compare against.
            FloorUnitPrice = floor,
            FloorCurrency = floorCurrency,
            FloorBasis = floorBasis
        };

        // The floor is stated on EVERY line, present or absent. A silent omission is the blank
        // that reads like a loading state; "no cost floor is established" is the visible gap.
        var floorSentence = floorBasis ?? NoFloorEstablished;

        if (candidates.Count == 0)
        {
            line.Rationale = "No governed pricing evidence is available for this line; review it in the " +
                             "Customer Quote workflow. " + floorSentence;
            line.Confidence = 0m;
            line.NeedsAttention = true;
            return line;
        }

        // Fail closed: only candidates denominated in this line's currency may be blended. A
        // signal in another currency is dropped rather than converted here, because a shadow
        // pricing advisory must rest on like-for-like evidence, not on an FX assumption.
        candidates = candidates.Where(c => CurrencyOk(c.CurrencyCode, currency)).ToList();
        if (candidates.Count == 0)
        {
            line.Rationale = "The only pricing evidence for this line is in another currency, so none of " +
                             "it was used. " + floorSentence;
            line.Confidence = 0m;
            line.NeedsAttention = true;
            return line;
        }

        var ordered = candidates.OrderByDescending(c => c.Weight).ToList();
        var dominant = ordered[0];

        var totalWeight = ordered.Sum(c => c.Weight);
        var recommended = Round4(ordered.Aggregate(0m, (acc, c) => acc + c.Price * (decimal)(c.Weight / totalWeight)));

        // Margin is only meaningful when both sides are the same money. This advisory does not
        // convert (see the CURRENCY note at the top of the file); the send gate does, because that
        // is where refusing costs nothing and guessing costs a deal.
        decimal? marginPct = floor is > 0m && recommended > 0m && CurrencyOk(floorCurrency, currency)
            ? Math.Round((recommended - floor.Value) / recommended * 100m, 2, MidpointRounding.AwayFromZero)
            : null;

        // Confidence: dominant effective weight (already priority × recency × qty),
        // plus a small bonus per corroborating signal, plus an agreement bonus when the
        // candidates cluster tightly. Clamped to [0.05, 0.98] — never absolute certainty.
        var prices = ordered.Select(c => c.Price).ToList();
        var spread = recommended > 0 ? (double)((prices.Max() - prices.Min()) / recommended) : 0.0;
        var agreement = ordered.Count > 1 ? Math.Max(0.0, 0.15 * (1.0 - Math.Min(1.0, spread / 0.25))) : 0.0;
        var confidence = Math.Clamp(dominant.Weight + 0.05 * (ordered.Count - 1) + agreement, 0.05, 0.98);

        line.RecommendedUnitPrice = recommended;
        line.MarginPct = marginPct;
        line.Confidence = Math.Round((decimal)confidence, 2, MidpointRounding.AwayFromZero);
        line.NeedsAttention = line.Confidence < 0.5m;
        line.Rationale = BuildRationale(dominant, ordered.Count, floorSentence);
        line.Signals = ordered.Select(c => new PriceSignal
        {
            Source = c.Source,
            Label = c.Label,
            Value = Round4(c.Price),
            Detail = c.Detail
        }).ToList();

        return line;
    }

    /// <summary>
    /// Said in full whenever a line has no awarded sourcing decision. The absence is stated in
    /// words on purpose: a blank floor reads as "not loaded yet", and a zero would read as
    /// "any price is acceptable".
    /// </summary>
    private const string NoFloorEstablished =
        "No cost floor is established for this line: no supplier award has been recorded against it, " +
        "so there is nothing to check a price against.";

    private static string BuildRationale(Candidate dominant, int signalCount, string floorSentence)
    {
        var corroboration = signalCount > 1 ? $", corroborated by {signalCount - 1} other signal(s)" : "";
        return $"Based on your last accepted quote for this product ({FmtMonth(dominant.Date)})" +
            corroboration + " (shadow advice). " + floorSentence;
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

    /// <summary>
    /// A price signal admitted into the blend. CurrencyCode is REQUIRED: the weighted blend at
    /// BuildLine averages Price across candidates, so a candidate that does not carry its own
    /// currency could silently be averaged against a different denomination the moment a second
    /// signal source is admitted. Today only recentQuote exists and it is already currency-
    /// filtered, so this is an invariant made structural rather than a live bug fixed.
    /// </summary>
    private sealed record Candidate(
        string Source, string Label, decimal Price, double Weight, string Detail,
        decimal? CostBasis, DateTime? Date, string? RefText, string? CurrencyCode);

    private sealed record AcceptedQuoteRow(long ProductId, decimal UnitPrice, decimal Quantity, DateTime? Date, string CurrencyCode, string QuoteNo);
}

/// <summary>Stable signal source identifiers (wire contract values).</summary>
public static class PriceSignalSources
{
    public const string RecentQuote = "recentQuote";
}
