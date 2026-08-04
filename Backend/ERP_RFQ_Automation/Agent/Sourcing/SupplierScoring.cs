namespace ERP_RFQ_Automation.Agent.Sourcing;

/// <summary>
/// A single scorable supplier bid. Price and lead time are "lower is better";
/// success rate is "higher is better". Implemented by the per-supplier aggregate in
/// <c>RecommendAwardTool</c> and by the per-line bids in <c>CompareSupplierQuotesTool</c>
/// so both share exactly one scoring formula.
/// </summary>
public interface IScoreCandidate
{
    decimal Price { get; }
    double LeadTime { get; }
    double SuccessRate { get; }
    double Score { get; set; }

    /// <summary>
    /// The currency <see cref="Price"/> is denominated in, AFTER any conversion the caller
    /// performed. Null means "the caller declares nothing", which is how existing callers that
    /// enforce a single currency upstream (CompareSupplierQuotesTool) opt out.
    ///
    /// Declared by every candidate or by none: a set where some candidates name a currency and
    /// others do not is not comparable, and <see cref="SupplierScoring.ScoreInPlace"/> refuses it.
    /// </summary>
    long? PriceCurrencyId => null;
}

/// <summary>
/// Shared multi-criteria scorer for supplier bids. Each criterion is min-max
/// normalized to [0,1] within the candidate set, then combined with fixed weights
/// (price highest, then lead time, then success rate). Factored out of
/// RecommendAwardTool so evaluation logic lives in exactly one place.
/// </summary>
public static class SupplierScoring
{
    public const double WeightPrice = 0.5;
    public const double WeightLeadTime = 0.25;
    public const double WeightSuccessRate = 0.25;

    /// <summary>Weights object for embedding in tool JSON results.</summary>
    public static object Weights => new { price = WeightPrice, leadTime = WeightLeadTime, successRate = WeightSuccessRate };

    /// <summary>
    /// Scores every candidate in place (sets <see cref="IScoreCandidate.Score"/>).
    /// Lower price/lead score higher; higher success scores higher. When a criterion
    /// is constant across the set it contributes its full weight to every candidate.
    ///
    /// Price is min-max normalised across the set at 50% weight, so the numbers compared here
    /// must already be in ONE currency: 900 EUR beating 1,000 USD as a bare decimal is a wrong
    /// award recommendation wearing the authority of an AI answer. Callers convert first (see
    /// RecommendAwardTool) and declare the result via <see cref="IScoreCandidate.PriceCurrencyId"/>;
    /// this method refuses a set that still spans currencies rather than ranking it.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The candidate prices are not all denominated in one declared currency.
    /// </exception>
    public static void ScoreInPlace(IReadOnlyList<IScoreCandidate> bids)
    {
        if (bids.Count == 0) return;
        EnsureOneCurrency(bids);

        var minPrice = bids.Min(b => b.Price);
        var maxPrice = bids.Max(b => b.Price);
        var minLead = bids.Min(b => b.LeadTime);
        var maxLead = bids.Max(b => b.LeadTime);
        var maxSuccess = bids.Max(b => b.SuccessRate);

        foreach (var b in bids)
        {
            var priceScore = maxPrice == minPrice ? 1.0 : 1.0 - (double)((b.Price - minPrice) / (maxPrice - minPrice));
            var leadScore = maxLead == minLead ? 1.0 : 1.0 - ((b.LeadTime - minLead) / (maxLead - minLead));
            var successScore = maxSuccess <= 0 ? 0.0 : b.SuccessRate / maxSuccess;
            b.Score = Math.Round(WeightPrice * priceScore + WeightLeadTime * leadScore + WeightSuccessRate * successScore, 4);
        }
    }

    /// <summary>
    /// Fail-closed comparability gate. Either every candidate declares the same currency, or
    /// none declares one (the legacy contract, kept so callers that enforce a single currency
    /// before they get here compile and behave unchanged). Anything in between is a set whose
    /// prices are not on one scale, and ranking it would produce a confident wrong answer.
    /// </summary>
    private static void EnsureOneCurrency(IReadOnlyList<IScoreCandidate> bids)
    {
        var declared = bids.Select(b => b.PriceCurrencyId).Where(id => id.HasValue)
            .Select(id => id!.Value).Distinct().ToArray();
        if (declared.Length == 0) return;

        if (declared.Length > 1)
            throw new InvalidOperationException(
                $"Supplier bids cannot be ranked: their prices span {declared.Length} currencies " +
                $"(ids {string.Join(", ", declared.OrderBy(id => id))}). Convert every bid into one " +
                "currency using approved exchange rates before scoring.");

        if (bids.Any(b => b.PriceCurrencyId is null))
            throw new InvalidOperationException(
                "Supplier bids cannot be ranked: some bids declare a currency and others carry none, " +
                "so their prices are not on one scale. Resolve the missing currencies before scoring.");
    }
}
