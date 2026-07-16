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
    /// </summary>
    public static void ScoreInPlace(IReadOnlyList<IScoreCandidate> bids)
    {
        if (bids.Count == 0) return;

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
}
