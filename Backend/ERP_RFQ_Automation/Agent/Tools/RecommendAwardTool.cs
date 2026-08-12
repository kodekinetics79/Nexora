using System.Text.Json;
using ERP_RFQ_Automation.Agent.Guardrails;
using ERP_RFQ_Automation.Fx;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.SupplierEvaluation;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Agent.Tools;

/// <summary>
/// Advisory (read-only) multi-criteria award recommendation for an RFQ. Compares
/// the suppliers quoted against the RFQ's line items on total price, average lead
/// time and payment terms, then recommends the best-scoring supplier.
/// Purely advisory — records nothing — so it is NOT a mutation and skips the
/// guardrail. Acting on the recommendation happens via create_order_from_quote /
/// dispatch_rfq_to_supplier, which ARE guarded.
///
/// Scoring contract
/// ----------------
/// The weights are the tenant's, read from <c>SupplierComparisonWeights</c>, not constants in this
/// file. This tool and the supplier-quote comparison a human awards from now score through the same
/// <see cref="WeightedSupplierScoring"/> with the same weight set, so there is one recommendation
/// instead of two that could disagree on the same data.
///
/// Historical success rate is NO LONGER scored. It is an operator-typed spreadsheet column, never a
/// measured outcome, and weighting it presented a typed number as performance evidence. It is still
/// reported beside each supplier as the display-only value it always was.
///
/// FX contract
/// -----------
/// Price carries the largest default weight and is min-max normalised across the candidate set, so
/// the figures compared must be in one currency. They previously were not: bid totals were summed
/// straight off <c>Rfqitem.UnitPrice</c> with the per-line <c>CurrencyId</c> ignored, so on a
/// mixed-currency RFQ a supplier quoting 900 EUR outranked one quoting 1,000 USD purely because
/// 900 &lt; 1000. That is a wrong commercial decision presented as an AI recommendation.
///
/// Bid totals are now converted into the business unit's base currency with approved,
/// effective-dated rates before scoring. When a bid carries no currency, or no approved rate
/// joins it to the base currency, the tool REFUSES to rank and says exactly why — the same
/// fail-closed stance <c>CompareSupplierQuotesTool</c> already takes, and the one
/// <c>IFxConversionService</c> takes on every path.
/// </summary>
public sealed class RecommendAwardTool : IAgentTool
{
    private readonly ErpRfqAutomationContext _db;
    public RecommendAwardTool(ErpRfqAutomationContext db) => _db = db;

    public string Name => AgentToolNames.RecommendAward;
    public string Description =>
        "Recommend which supplier to award an RFQ to, using the tenant's configured weighted comparison of " +
        "total price, lead time, warranty and payment terms. Advisory only — does not place any order.";
    public string InputJsonSchema =>
        "{\"type\":\"object\",\"properties\":{\"rfqId\":{\"type\":\"integer\"}},\"required\":[\"rfqId\"]}";
    public bool IsMutation => false;

    public async Task<AgentToolResult> ExecuteAsync(JsonElement input, AgentToolContext ctx, CancellationToken ct)
    {
        var rfqId = input.GetInt64OrNull("rfqId");
        if (rfqId is null) return AgentToolResult.Fail("rfqId is required.");

        // Explicit tenant lineage, matching CompareSupplierQuotesTool. Every IFxConversionService
        // call below takes this business unit id explicitly — that is the compensating control
        // that keeps conversion safe from an unscoped caller.
        var businessUnitId = ctx.BusinessUnitId;
        var rfqExists = await _db.Set<Rfq>().AsNoTracking()
            .AnyAsync(r => r.Id == rfqId.Value && r.BusinessUnitId == businessUnitId, ct);
        if (!rfqExists) return AgentToolResult.Fail($"RFQ {rfqId} not found.");

        // Line items that carry a supplier + price form the candidate bids.
        var items = await _db.Set<Rfqitem>().AsNoTracking()
            .Where(i => i.Rfqid == rfqId.Value && i.SupplierId != null && i.UnitPrice != null)
            .Select(i => new
            {
                i.SupplierId,
                SupplierName = i.Supplier != null ? i.Supplier.Name : null,
                SuccessRate = i.Supplier != null ? i.Supplier.SuccessRate : null,
                CreditDays = i.Supplier != null ? i.Supplier.CreditDays : null,
                i.UnitPrice,
                i.Quantity,
                i.LeadTime,
                i.CurrencyId
            })
            .ToListAsync(ct);

        if (items.Count == 0)
            return AgentToolResult.Fail($"No priced supplier bids found for RFQ {rfqId}. Dispatch the RFQ to suppliers first.");

        // Fail closed on a bid line with no currency: a price that does not know its own
        // denomination cannot take 50% of the weight in a ranking.
        var unpricedCurrencyLines = items.Count(i => i.CurrencyId is null);
        if (unpricedCurrencyLines > 0)
            return AgentToolResult.Fail(
                $"{unpricedCurrencyLines} of {items.Count} priced bid line(s) on RFQ {rfqId} carry no currency. " +
                "Award ranking weights price at 50%, so every bid must declare its currency before the " +
                "suppliers can be compared.");

        var fx = new FxConversionService(_db);
        var asOf = DateTime.UtcNow;
        var distinctCurrencies = items.Select(i => i.CurrencyId!.Value).Distinct().ToArray();

        // Single currency: no conversion is needed and none is invented. Mixed: everything is
        // brought onto the business unit's base currency first.
        long scoringCurrencyId;
        if (distinctCurrencies.Length == 1)
        {
            scoringCurrencyId = distinctCurrencies[0];
        }
        else
        {
            var baseCurrencyId = await fx.ResolveBaseCurrencyIdAsync(businessUnitId, ct);
            if (baseCurrencyId is null)
                return AgentToolResult.Fail(
                    $"RFQ {rfqId} has supplier bids in {distinctCurrencies.Length} currencies and this business unit " +
                    "has no single active base currency to compare them in. Set exactly one base currency, or " +
                    "normalise the bids, before an award can be recommended.");
            scoringCurrencyId = baseCurrencyId.Value;
        }

        var scoringCurrencyCode = await _db.Set<Currency>().AsNoTracking()
            .Where(c => c.BusinessUnitId == businessUnitId && c.Id == scoringCurrencyId)
            .Select(c => c.Code).FirstOrDefaultAsync(ct);

        var grouped = items
            .GroupBy(i => new { i.SupplierId, i.SupplierName, i.SuccessRate, i.CreditDays })
            .OrderBy(g => g.Key.SupplierId)
            .ToList();

        var bySupplier = new List<SupplierBid>(grouped.Count);
        foreach (var group in grouped)
        {
            // TotalAsync groups this supplier's lines by their own currency, applies one approved
            // rate per currency, rounds once after the rate, and fails closed as a whole rather
            // than quietly dropping the legs it could not convert.
            var total = await fx.TotalAsync(businessUnitId,
                group.Select(x => new FxAmount((x.UnitPrice ?? 0m) * x.Quantity, x.CurrencyId)).ToArray(),
                asOf, scoringCurrencyId, ct);

            if (!total.Converted || total.Total is null)
                return AgentToolResult.Fail(
                    $"Supplier {group.Key.SupplierName ?? $"#{group.Key.SupplierId}"} cannot be compared with the " +
                    $"others on RFQ {rfqId}: {total.UnavailableReason} " +
                    "No award is recommended while any bid is uncomparable.");

            bySupplier.Add(new SupplierBid
            {
                SupplierId = group.Key.SupplierId!.Value,
                SupplierName = group.Key.SupplierName ?? $"Supplier {group.Key.SupplierId}",
                TotalPrice = total.Total.Value,
                TotalPriceCurrencyId = scoringCurrencyId,
                // Null, not zero — the same distinction the CreditDays line below draws, and it was
                // missed here. `DefaultIfEmpty(0).Average()` gave a supplier whose lead time nobody
                // captured an average of 0 days, which min-max normalisation reads as the FASTEST
                // offer in the set and awards the entire lead-time weight to. A supplier that never
                // stated a delivery date was winning on delivery.
                AvgLeadTime = group.Where(x => x.LeadTime.HasValue)
                    .Select(x => (double?)x.LeadTime!.Value)
                    .DefaultIfEmpty(null)
                    .Average(),
                SuccessRate = (double)(group.Key.SuccessRate ?? 0m),
                // Null, not zero: a supplier whose credit days nobody captured has not told us it
                // takes no credit, and the scorer must be able to tell those two apart.
                CreditDays = group.Key.CreditDays,
                LineCount = group.Count()
            });
        }

        // The tenant's weight set, shared with the supplier-quote comparison a human awards from, so
        // both name the same winner. Every bid now declares the SAME currency, so the scorer's
        // comparability gate passes by construction.
        var weights = await new SupplierComparisonWeightsService(_db).ResolveAsync(businessUnitId, ct);
        var scored = WeightedSupplierScoring.Score(bySupplier, weights);
        for (var i = 0; i < bySupplier.Count; i++)
        {
            bySupplier[i].Score = scored[i].Score;
            bySupplier[i].ScoreUnavailableReason = scored[i].UnavailableReason;
            bySupplier[i].Contributions = scored[i].Contributions;
        }

        var ranked = bySupplier
            .OrderByDescending(b => b.Score.HasValue).ThenByDescending(b => b.Score ?? 0d)
            .ThenBy(b => b.TotalPrice).ThenBy(b => b.SupplierId).ToList();
        var winner = ranked[0].Score.HasValue ? ranked[0] : null;
        var currencyLabel = scoringCurrencyCode ?? $"currency #{scoringCurrencyId}";
        var conversionNote = distinctCurrencies.Length == 1
            ? $"All bids are quoted in {currencyLabel}; no conversion was applied."
            : $"Bids spanned {distinctCurrencies.Length} currencies and were converted into {currencyLabel} " +
              "using approved, effective-dated exchange rates before ranking.";
        var weightsNote =
            $"price {weights.Price}, lead time {weights.LeadTime}, warranty {weights.Warranty}, " +
            $"payment terms {weights.PaymentTerms}, out of {SupplierScoringWeights.MaximumScore}";

        // No score, no recommendation. A criterion the tenant weighted is not captured for any
        // supplier here, and inventing a value for it — or quietly treating it as zero, which sorts
        // the supplier last as if it had lost on the numbers — would be a confident wrong answer.
        // The bids are unaffected: they remain valid, and a human can still award any of them.
        if (winner is null)
            return AgentToolResult.Fail(
                $"No award is recommended for RFQ {rfqId}: no supplier could be scored against this business " +
                $"unit's comparison weights ({weightsNote}). {ranked[0].ScoreUnavailableReason} " +
                "Capture the missing values, or move the weight off the criterion that is not captured.");

        return AgentToolResult.Ok(new
        {
            rfqId = rfqId.Value,
            recommendedSupplierId = winner.SupplierId,
            recommendedSupplierName = winner.SupplierName,
            comparisonCurrencyId = scoringCurrencyId,
            comparisonCurrencyCode = scoringCurrencyCode,
            conversionNote,
            rationale =
                $"Best weighted score {winner.Score:0.##} out of {SupplierScoringWeights.MaximumScore} " +
                $"({weightsNote}). Total price {winner.TotalPrice:0.##} {currencyLabel}, avg lead time " +
                $"{(winner.AvgLeadTime is null ? "not captured" : $"{winner.AvgLeadTime:0.#} days")}, credit days " +
                $"{(winner.CreditDays is null ? "not captured" : winner.CreditDays.ToString())}. " +
                conversionNote,
            weights = new
            {
                price = weights.Price,
                leadTime = weights.LeadTime,
                warranty = weights.Warranty,
                paymentTerms = weights.PaymentTerms,
                outOf = SupplierScoringWeights.MaximumScore
            },
            comparison = ranked.Select(b => new
            {
                b.SupplierId,
                b.SupplierName,
                b.TotalPrice,
                totalPriceCurrencyCode = scoringCurrencyCode,
                b.AvgLeadTime,
                b.CreditDays,
                // Reported, never scored: an operator typed it, nothing measured it.
                b.SuccessRate,
                b.LineCount,
                b.Score,
                scoreOutOf = SupplierScoringWeights.MaximumScore,
                scoreUnavailableReason = b.ScoreUnavailableReason,
                scoreBreakdown = b.Contributions
            })
        });
    }

    private sealed class SupplierBid : IWeightedScoreCandidate
    {
        public long SupplierId { get; set; }
        public string SupplierName { get; set; } = string.Empty;

        /// <summary>Already converted into <see cref="TotalPriceCurrencyId"/>.</summary>
        public decimal TotalPrice { get; set; }
        public long TotalPriceCurrencyId { get; set; }
        /// <summary>Null when no line in the group stated a lead time. See the projection comment.</summary>
        public double? AvgLeadTime { get; set; }
        public int? CreditDays { get; set; }

        /// <summary>Reported to the operator, deliberately not scored. See the class remarks.</summary>
        public double SuccessRate { get; set; }

        public int LineCount { get; set; }
        public double? Score { get; set; }
        public string? ScoreUnavailableReason { get; set; }
        public IReadOnlyList<SupplierScoreContribution> Contributions { get; set; } = [];

        // Projection onto the governed scorer. Warranty is not captured numerically anywhere in the
        // RFQ line, so it is offered as "unknown" rather than as a number nobody stated.
        decimal? IWeightedScoreCandidate.Price => TotalPrice;
        double? IWeightedScoreCandidate.LeadTimeDays => AvgLeadTime;  // already nullable: null => unscorable
        double? IWeightedScoreCandidate.WarrantyMonths => null;
        double? IWeightedScoreCandidate.CreditDays => CreditDays;
        long? IWeightedScoreCandidate.PriceCurrencyId => TotalPriceCurrencyId;
    }
}
