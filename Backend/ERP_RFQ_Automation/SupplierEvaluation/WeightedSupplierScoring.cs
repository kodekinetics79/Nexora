namespace ERP_RFQ_Automation.SupplierEvaluation;

/// <summary>The four criteria FR-QTM-03 names, and nothing else. Not a registry, not extensible.</summary>
public static class SupplierScoreCriteria
{
    public const string Price = "PRICE";
    public const string LeadTime = "LEAD_TIME";
    public const string Warranty = "WARRANTY";
    public const string PaymentTerms = "PAYMENT_TERMS";
}

/// <summary>
/// One scorable supplier offer. Every criterion is nullable because "we do not know" is a real state
/// and the scorer refuses to invent a value for it.
///
/// <para>Price and lead time are lower-is-better; warranty and credit days are higher-is-better.</para>
/// </summary>
public interface IWeightedScoreCandidate
{
    /// <summary>
    /// The single landed-cost figure, consumed as ONE number. It is deliberately not decomposed into
    /// duty/freight/conformity sub-weights.
    /// </summary>
    decimal? Price { get; }

    double? LeadTimeDays { get; }

    /// <summary>Warranty length in months. Null until warranty is captured numerically.</summary>
    double? WarrantyMonths { get; }

    /// <summary>The supplier's credit days — the numeric companion to free-text payment terms.</summary>
    double? CreditDays { get; }

    /// <summary>
    /// The currency <see cref="Price"/> is denominated in, AFTER any conversion the caller performed.
    /// Null means "the caller declares nothing", which is how callers that enforce a single currency
    /// upstream opt out.
    ///
    /// Declared by every candidate or by none: a set where some candidates name a currency and others
    /// do not is not comparable, and <see cref="WeightedSupplierScoring.Score"/> refuses it.
    /// </summary>
    long? PriceCurrencyId => null;
}

/// <summary>
/// A validated weight set: four non-negative integers totalling exactly 100. Constructed from the
/// tenant's <see cref="SupplierComparisonWeights"/> row, never from constants at a call site.
/// </summary>
public sealed class SupplierScoringWeights
{
    private SupplierScoringWeights(int price, int leadTime, int warranty, int paymentTerms)
    {
        Price = price;
        LeadTime = leadTime;
        Warranty = warranty;
        PaymentTerms = paymentTerms;
    }

    public int Price { get; }
    public int LeadTime { get; }
    public int Warranty { get; }
    public int PaymentTerms { get; }

    /// <summary>The ceiling a score is presented against: <c>Weighted 82/100</c>, never a bare 82.</summary>
    public const int MaximumScore = SupplierComparisonWeights.RequiredTotal;

    /// <summary>
    /// Builds the weight set, refusing anything that does not total 100. A set that totals 90 would
    /// still produce a number and that number would be presented out of 100, which is a score no
    /// operator could reconcile against the row it is shown beside.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">A weight is negative, or they do not total 100.</exception>
    public static SupplierScoringWeights Create(int price, int leadTime, int warranty, int paymentTerms)
    {
        if (price < 0 || leadTime < 0 || warranty < 0 || paymentTerms < 0)
            throw new ArgumentOutOfRangeException(nameof(price),
                "Supplier comparison weights cannot be negative.");
        var total = price + leadTime + warranty + paymentTerms;
        if (total != MaximumScore)
            throw new ArgumentOutOfRangeException(nameof(price),
                $"Supplier comparison weights must total {MaximumScore}; these total {total}. "
                + "A score presented as a share of 100 is not one unless they do.");
        return new(price, leadTime, warranty, paymentTerms);
    }

    public static SupplierScoringWeights From(SupplierComparisonWeights row) =>
        Create(row.PriceWeight, row.LeadTimeWeight, row.WarrantyWeight, row.PaymentTermsWeight);

    /// <summary>The defaults a tenant with no saved row runs on.</summary>
    public static SupplierScoringWeights Defaults { get; } = Create(
        SupplierComparisonWeights.DefaultPriceWeight,
        SupplierComparisonWeights.DefaultLeadTimeWeight,
        SupplierComparisonWeights.DefaultWarrantyWeight,
        SupplierComparisonWeights.DefaultPaymentTermsWeight);
}

/// <summary>
/// One criterion's contribution to one offer's score: the raw value the operator can verify, the
/// weight it carried, and the points it earned. The comparison row shows these so it visibly sums to
/// the total — a recommendation whose arithmetic cannot be checked is a black box.
/// </summary>
/// <param name="Criterion">One of <see cref="SupplierScoreCriteria"/>.</param>
/// <param name="Label">The criterion in plain words, as it appears in an unavailability reason.</param>
/// <param name="RawValue">The offer's value, or null when the offer carries none.</param>
/// <param name="Weight">The tenant's weight for this criterion, 0–100.</param>
/// <param name="PointsEarned">
/// Points out of <paramref name="Weight"/>. Null when the offer has no score at all — never 0,
/// because 0 is indistinguishable from "worst offer in the set" and that is exactly the confusion
/// that turns a missing value into a wrong award.
/// </param>
public sealed record SupplierScoreContribution(
    string Criterion,
    string Label,
    double? RawValue,
    int Weight,
    double? PointsEarned);

/// <summary>
/// One offer's outcome. Either <see cref="Score"/> is a number out of
/// <see cref="SupplierScoringWeights.MaximumScore"/> and <see cref="UnavailableReason"/> is null, or
/// the reverse. Both are never set, and an offer with no score is still fully awardable — the score
/// annotates, the human awards.
/// </summary>
public sealed record SupplierScoreResult(
    double? Score,
    string? UnavailableReason,
    IReadOnlyList<SupplierScoreContribution> Contributions);

/// <summary>
/// The one supplier comparison scorer. Each weighted criterion is min-max normalised to [0,1] within
/// the candidate set and multiplied by its weight, so a complete offer scores out of 100.
///
/// <para>Lifted out of <c>Agent/Sourcing/SupplierScoring.cs</c>, whose weights were constants
/// (price 0.5, lead time 0.25, success rate 0.25) and whose home namespace is BRD-excluded and
/// freezable. Two recommenders existed and could name different winners on the same line: this one,
/// and a hardcoded coverage/landed-cost/lead-time sort in the procurement service. This is the
/// unification.</para>
///
/// <para>Three behaviours are load-bearing and deliberate:</para>
/// <list type="bullet">
/// <item>A mixed-currency candidate set is REFUSED, not ranked. Ranking 900 EUR above 1,000 USD as
/// bare decimals is a wrong award wearing the authority of a recommendation.</item>
/// <item>A missing WEIGHTED criterion produces no score and a reason, never a zero. Zero would sort
/// the offer last as if it were the worst on that criterion, which is a claim nobody made.</item>
/// <item>A criterion weighted 0 is not required, so its absence blocks nothing. That is what makes
/// warranty's default weight of 0 safe while warranty is still free text.</item>
/// <item>A set with only ONE comparable offer is not scored at all. A weighted score is a
/// comparison, and there is nothing here to compare.</item>
/// </list>
///
/// <para>Normalisation is min-max within the candidate set — no z-scores, log scaling or utility
/// curves — and the range is taken over the SCORABLE offers only, because those are the offers being
/// ranked. When a criterion is constant across that set it contributes its full weight to every
/// offer, which is the behaviour the original scorer had.</para>
/// </summary>
public static class WeightedSupplierScoring
{
    /// <summary>
    /// Why a lone offer carries no score. Stated as what the score IS rather than as a failure,
    /// because nothing went wrong: there is simply no comparison to make.
    /// </summary>
    public const string SingleComparableOfferReason =
        "Only one comparable offer — a weighted score ranks offers against each other";

    /// <summary>
    /// Scores every candidate, returning one result per candidate in the SAME ORDER as the input.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The candidate prices are not all denominated in one declared currency.
    /// </exception>
    public static IReadOnlyList<SupplierScoreResult> Score(
        IReadOnlyList<IWeightedScoreCandidate> candidates, SupplierScoringWeights weights)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(weights);
        if (candidates.Count == 0) return [];
        EnsureOneCurrency(candidates);

        var criteria = CriteriaFor(weights);

        // An offer is scorable when it carries a value for every criterion the tenant weighted.
        // Determined BEFORE any normalisation, so the range is taken over the offers actually being
        // ranked and an unrankable offer cannot stretch the scale the others are measured on.
        var missing = new List<string>[candidates.Count];
        for (var i = 0; i < candidates.Count; i++)
            missing[i] = criteria
                .Where(criterion => criterion.Weight > 0 && criterion.Value(candidates[i]) is null)
                .Select(criterion => criterion.Label)
                .ToList();

        var scorable = Enumerable.Range(0, candidates.Count).Where(i => missing[i].Count == 0).ToArray();
        // A weighted score is a comparison, so one comparable offer produces none. With a single
        // scorable candidate the min and the max of every criterion are the same number, that
        // criterion contributes its full weight, and the offer totals a flawless 100 out of 100
        // having been measured against nothing — on the single-supplier RFQ, which is the most
        // common case there is. It gets a plain reason instead, stays fully awardable, and the
        // verifiable lowest-landed-cost fact beside it is untouched.
        //
        // This is NOT the constant-criterion case below: when several offers are being compared and
        // simply tie on a criterion, awarding all of them its full weight is correct and neutral.
        var comparable = scorable.Length > 1;
        var ranges = criteria.ToDictionary(
            criterion => criterion.Key,
            criterion => RangeOf(criterion, candidates, scorable));

        var results = new SupplierScoreResult[candidates.Count];
        for (var i = 0; i < candidates.Count; i++)
        {
            var candidate = candidates[i];
            var scored = missing[i].Count == 0 && comparable;
            var contributions = new List<SupplierScoreContribution>(criteria.Count);
            var total = 0d;

            foreach (var criterion in criteria)
            {
                var raw = criterion.Value(candidate);
                double? points = null;
                if (scored && criterion.Weight > 0)
                {
                    points = Math.Round(criterion.Weight * Normalise(criterion, raw!.Value, ranges[criterion.Key]),
                        2, MidpointRounding.AwayFromZero);
                    total += points.Value;
                }
                else if (scored)
                {
                    // Weighted 0: it earns nothing and it required nothing. Reported so the row shows
                    // the whole weight set rather than silently hiding a criterion the tenant switched off.
                    points = 0d;
                }

                contributions.Add(new(criterion.Key, criterion.Label, raw, criterion.Weight, points));
            }

            results[i] = scored
                // Summed from the ROUNDED contributions, so the row the operator reads adds up to the
                // number beside it exactly rather than to within a rounding step of it.
                ? new(Math.Round(total, 2, MidpointRounding.AwayFromZero), null, contributions)
                // The missing-criterion reason takes precedence: it is the more specific fact, and
                // it is the one the operator can act on by capturing the value.
                : new(null, missing[i].Count > 0
                    ? $"Cannot score — {string.Join(", ", missing[i])} missing"
                    : SingleComparableOfferReason, contributions);
        }

        return results;
    }

    private sealed record Criterion(
        string Key, string Label, int Weight, bool LowerIsBetter, Func<IWeightedScoreCandidate, double?> Value);

    private static IReadOnlyList<Criterion> CriteriaFor(SupplierScoringWeights weights) =>
    [
        new(SupplierScoreCriteria.Price, "price", weights.Price, true, c => (double?)c.Price),
        new(SupplierScoreCriteria.LeadTime, "lead time", weights.LeadTime, true, c => c.LeadTimeDays),
        new(SupplierScoreCriteria.Warranty, "warranty", weights.Warranty, false, c => c.WarrantyMonths),
        new(SupplierScoreCriteria.PaymentTerms, "payment terms", weights.PaymentTerms, false, c => c.CreditDays)
    ];

    private static (double Min, double Max) RangeOf(
        Criterion criterion, IReadOnlyList<IWeightedScoreCandidate> candidates, IReadOnlyList<int> scorable)
    {
        var values = scorable.Select(i => criterion.Value(candidates[i])).Where(v => v.HasValue)
            .Select(v => v!.Value).ToArray();
        return values.Length == 0 ? (0d, 0d) : (values.Min(), values.Max());
    }

    private static double Normalise(Criterion criterion, double value, (double Min, double Max) range)
    {
        // A criterion every offer agrees on separates nothing, so it contributes its full weight to
        // all of them rather than arbitrarily awarding it to one.
        if (range.Max == range.Min) return 1.0;
        var position = (value - range.Min) / (range.Max - range.Min);
        return criterion.LowerIsBetter ? 1.0 - position : position;
    }

    /// <summary>
    /// Fail-closed comparability gate, kept verbatim from <c>Agent/Sourcing/SupplierScoring.cs</c>.
    /// Either every candidate declares the same currency, or none declares one (the legacy contract,
    /// kept so callers that enforce a single currency before they get here behave unchanged).
    /// Anything in between is a set whose prices are not on one scale, and ranking it would produce a
    /// confident wrong answer.
    /// </summary>
    private static void EnsureOneCurrency(IReadOnlyList<IWeightedScoreCandidate> bids)
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
