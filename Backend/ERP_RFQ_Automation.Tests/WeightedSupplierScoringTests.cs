using ERP_RFQ_Automation.SupplierEvaluation;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// The governed supplier comparison scorer.
///
/// <para>The register claimed weighted supplier scoring did not exist. It did — with the weights
/// hardcoded at price 0.5 / lead time 0.25 / success rate 0.25 inside <c>Agent/Sourcing</c>, while
/// the recommendation a human actually awarded from was a separate hardcoded sort in the procurement
/// service. Two recommenders, one line, potentially two different winners. These tests hold the
/// unified scorer to the three behaviours that make it safe to put a number in front of an
/// operator.</para>
///
/// <para>They prove rather than assert: each one is written so that removing the behaviour under
/// test makes it fail, not so that it restates the implementation.</para>
/// </summary>
public sealed class WeightedSupplierScoringTests
{
    private sealed record Offer(
        decimal? Price = null,
        double? LeadTimeDays = null,
        double? WarrantyMonths = null,
        double? CreditDays = null,
        long? PriceCurrencyId = null) : IWeightedScoreCandidate;

    /// <summary>
    /// The control fires. Same two offers, two weight sets, two different winners — the cheap slow
    /// supplier wins when price dominates, the expensive fast one wins when speed does. If the
    /// weights were still constants, or were read but not applied, this test cannot pass.
    /// </summary>
    [Fact]
    public void Changing_a_weight_changes_which_supplier_wins()
    {
        var cheapAndSlow = new Offer(Price: 1_000m, LeadTimeDays: 45, CreditDays: 30);
        var dearAndFast = new Offer(Price: 1_240m, LeadTimeDays: 12, CreditDays: 30);
        IReadOnlyList<IWeightedScoreCandidate> offers = [cheapAndSlow, dearAndFast];

        var onPrice = WeightedSupplierScoring.Score(offers, SupplierScoringWeights.Create(70, 20, 0, 10));
        var onSpeed = WeightedSupplierScoring.Score(offers, SupplierScoringWeights.Create(40, 50, 0, 10));

        Assert.True(onPrice[0].Score > onPrice[1].Score,
            $"price-weighted: {onPrice[0].Score} should beat {onPrice[1].Score}");
        Assert.True(onSpeed[1].Score > onSpeed[0].Score,
            $"speed-weighted: {onSpeed[1].Score} should beat {onSpeed[0].Score}");
    }

    /// <summary>
    /// The row an operator reads must add up to the number printed beside it. Points are summed from
    /// the rounded contributions precisely so the arithmetic on screen is exact, not "close".
    /// </summary>
    [Fact]
    public void The_breakdown_sums_to_the_score_and_carries_the_raw_values()
    {
        IReadOnlyList<IWeightedScoreCandidate> offers =
        [
            new Offer(Price: 1_000m, LeadTimeDays: 45, CreditDays: 30),
            new Offer(Price: 1_240m, LeadTimeDays: 12, CreditDays: 60),
            new Offer(Price: 1_100m, LeadTimeDays: 30, CreditDays: 45)
        ];

        var scored = WeightedSupplierScoring.Score(offers, SupplierScoringWeights.Create(70, 20, 0, 10));

        foreach (var result in scored)
        {
            Assert.Null(result.UnavailableReason);
            // To the two decimal places both the score and the contributions are rendered at: the
            // operator's arithmetic has to work on the numbers actually printed.
            Assert.Equal(result.Score!.Value, result.Contributions.Sum(c => c.PointsEarned ?? 0), 2);
            Assert.InRange(result.Score!.Value, 0, SupplierScoringWeights.MaximumScore);
        }

        // The cheapest offer earns the whole price weight; the dearest earns none of it. Both are
        // facts an operator can check against the landed cost in the same row.
        var price = scored.Select(r => r.Contributions.Single(c => c.Criterion == SupplierScoreCriteria.Price));
        Assert.Equal(70d, price.First().PointsEarned);
        Assert.Equal(0d, price.Skip(1).First().PointsEarned);
        Assert.Equal(1_000d, scored[0].Contributions
            .Single(c => c.Criterion == SupplierScoreCriteria.Price).RawValue);
        Assert.Equal(30d, scored[0].Contributions
            .Single(c => c.Criterion == SupplierScoreCriteria.PaymentTerms).RawValue);
    }

    /// <summary>
    /// A missing WEIGHTED criterion yields no score and a reason — never a zero. A zero would sort
    /// the offer last as if it were the worst on that criterion, which is a claim nobody made, and
    /// the offer would then look rejected rather than un-assessed.
    /// </summary>
    [Fact]
    public void A_missing_weighted_criterion_yields_no_score_and_no_zero()
    {
        IReadOnlyList<IWeightedScoreCandidate> offers =
        [
            // Two complete offers, so the set is a real comparison and the ONE thing under test is
            // what happens to the third — an offer whose weighted credit days nobody captured.
            new Offer(Price: 1_000m, LeadTimeDays: 45, CreditDays: 30),
            new Offer(Price: 900m, LeadTimeDays: 40, CreditDays: null),
            new Offer(Price: 1_100m, LeadTimeDays: 30, CreditDays: 60)
        ];

        var scored = WeightedSupplierScoring.Score(offers, SupplierScoringWeights.Create(70, 20, 0, 10));

        Assert.NotNull(scored[0].Score);
        Assert.NotNull(scored[2].Score);
        Assert.Null(scored[1].Score);
        Assert.NotEqual(0d, scored[1].Score ?? -1d);
        Assert.Equal("Cannot score — payment terms missing", scored[1].UnavailableReason);
        // Not one contribution on the unscored offer carries points, so nothing downstream can sum
        // them into an accidental total.
        Assert.All(scored[1].Contributions, c => Assert.Null(c.PointsEarned));
        // …and the raw values it DOES have are still reported, so the row is not blank.
        Assert.Equal(900d, scored[1].Contributions
            .Single(c => c.Criterion == SupplierScoreCriteria.Price).RawValue);
    }

    /// <summary>
    /// Every criterion missing at once still produces one readable sentence rather than four.
    /// </summary>
    [Fact]
    public void Several_missing_criteria_are_named_in_one_reason()
    {
        IReadOnlyList<IWeightedScoreCandidate> offers =
            [new Offer(Price: 1_000m, LeadTimeDays: 5, CreditDays: 30), new Offer()];

        var scored = WeightedSupplierScoring.Score(offers, SupplierScoringWeights.Create(70, 20, 0, 10));

        Assert.Equal("Cannot score — price, lead time, payment terms missing", scored[1].UnavailableReason);
    }

    /// <summary>
    /// A criterion the tenant weighted 0 is not required, so its absence blocks nothing. This is what
    /// makes warranty's default weight of 0 safe while warranty is still free text on the quote —
    /// otherwise every offer in the product would be unscorable on the day the feature shipped.
    /// </summary>
    [Fact]
    public void A_criterion_weighted_zero_is_not_required()
    {
        IReadOnlyList<IWeightedScoreCandidate> offers =
        [
            new Offer(Price: 1_000m, LeadTimeDays: 45, WarrantyMonths: null, CreditDays: 30),
            new Offer(Price: 1_200m, LeadTimeDays: 12, WarrantyMonths: null, CreditDays: 30)
        ];

        var scored = WeightedSupplierScoring.Score(offers, SupplierScoringWeights.Create(70, 20, 0, 10));

        Assert.All(scored, r => Assert.NotNull(r.Score));
        Assert.All(scored, r => Assert.Null(r.UnavailableReason));
        // Reported with zero weight and zero points so the settings the tenant chose stay visible in
        // the row rather than a criterion silently vanishing from the explanation.
        var warranty = scored[0].Contributions.Single(c => c.Criterion == SupplierScoreCriteria.Warranty);
        Assert.Equal(0, warranty.Weight);
        Assert.Equal(0d, warranty.PointsEarned);
        Assert.Null(warranty.RawValue);
    }

    /// <summary>
    /// The same criterion missing becomes fatal the moment the tenant weights it. Same data, same
    /// scorer, one number changed in Setup.
    /// </summary>
    [Fact]
    public void Weighting_a_criterion_makes_its_absence_fatal()
    {
        IReadOnlyList<IWeightedScoreCandidate> offers =
        [
            new Offer(Price: 1_000m, LeadTimeDays: 45, CreditDays: 30),
            new Offer(Price: 1_200m, LeadTimeDays: 12, CreditDays: 30)
        ];

        Assert.All(WeightedSupplierScoring.Score(offers, SupplierScoringWeights.Create(70, 20, 0, 10)),
            r => Assert.NotNull(r.Score));
        Assert.All(WeightedSupplierScoring.Score(offers, SupplierScoringWeights.Create(60, 20, 10, 10)),
            r => Assert.Equal("Cannot score — warranty missing", r.UnavailableReason));
    }

    /// <summary>
    /// A mixed-currency set is REFUSED, not ranked. Ranking 900 EUR above 1,000 USD as bare decimals
    /// is a wrong award wearing the authority of a recommendation. This gate is lifted verbatim from
    /// the scorer it replaces and must keep failing closed.
    /// </summary>
    [Fact]
    public void A_mixed_currency_candidate_set_is_refused_rather_than_ranked()
    {
        IReadOnlyList<IWeightedScoreCandidate> offers =
        [
            new Offer(Price: 900m, LeadTimeDays: 10, CreditDays: 30, PriceCurrencyId: 2),
            new Offer(Price: 1_000m, LeadTimeDays: 20, CreditDays: 30, PriceCurrencyId: 7)
        ];

        var refusal = Assert.Throws<InvalidOperationException>(
            () => WeightedSupplierScoring.Score(offers, SupplierScoringWeights.Defaults));
        Assert.Contains("span 2 currencies", refusal.Message);
    }

    /// <summary>
    /// Half-declared is the same failure by another route: the prices are still not on one scale.
    /// </summary>
    [Fact]
    public void A_set_where_only_some_offers_declare_a_currency_is_refused()
    {
        IReadOnlyList<IWeightedScoreCandidate> offers =
        [
            new Offer(Price: 900m, LeadTimeDays: 10, CreditDays: 30, PriceCurrencyId: 2),
            new Offer(Price: 1_000m, LeadTimeDays: 20, CreditDays: 30)
        ];

        var refusal = Assert.Throws<InvalidOperationException>(
            () => WeightedSupplierScoring.Score(offers, SupplierScoringWeights.Defaults));
        Assert.Contains("not on one scale", refusal.Message);
    }

    /// <summary>
    /// One declared currency across the set, or none at all, is comparable and is ranked. The gate
    /// must fail closed without also refusing the cases the callers legitimately produce.
    /// </summary>
    [Fact]
    public void One_declared_currency_across_the_set_is_ranked_normally()
    {
        IReadOnlyList<IWeightedScoreCandidate> offers =
        [
            new Offer(Price: 900m, LeadTimeDays: 10, CreditDays: 30, PriceCurrencyId: 2),
            new Offer(Price: 1_000m, LeadTimeDays: 20, CreditDays: 30, PriceCurrencyId: 2)
        ];

        var scored = WeightedSupplierScoring.Score(offers, SupplierScoringWeights.Defaults);

        Assert.True(scored[0].Score > scored[1].Score);
    }

    /// <summary>
    /// The default weight set reproduces sane behaviour on ordinary data: the cheapest-and-fastest
    /// offer takes the full 100, the worst on every criterion takes 0, and nothing lands outside
    /// the ceiling the score is presented against.
    /// </summary>
    [Fact]
    public void The_default_weights_produce_a_sane_ranking()
    {
        IReadOnlyList<IWeightedScoreCandidate> offers =
        [
            new Offer(Price: 800m, LeadTimeDays: 7, CreditDays: 60),
            new Offer(Price: 1_400m, LeadTimeDays: 60, CreditDays: 0),
            new Offer(Price: 1_000m, LeadTimeDays: 21, CreditDays: 30)
        ];

        var scored = WeightedSupplierScoring.Score(offers, SupplierScoringWeights.Defaults);

        Assert.Equal(100d, scored[0].Score);
        Assert.Equal(0d, scored[1].Score);
        Assert.InRange(scored[2].Score!.Value, 0.01, 99.99);
        Assert.Equal(80, SupplierScoringWeights.Defaults.Price);
        // Warranty AND payment terms are both zero by default, and that is what makes the feature
        // usable on the day it ships: both are scored from data that does not exist yet on any
        // supplier, and a weighted criterion with no value is never scored as zero — it refuses to
        // score at all. A non-zero default on either would put "Cannot score" on every row.
        Assert.Equal(0, SupplierScoringWeights.Defaults.Warranty);
        Assert.Equal(0, SupplierScoringWeights.Defaults.PaymentTerms);
    }

    /// <summary>
    /// "Cheapest wins" (100/0/0/0) is the preset that reproduces today's behaviour exactly, and it
    /// must genuinely rank on landed cost alone — a supplier three weeks faster must not creep ahead.
    /// </summary>
    [Fact]
    public void The_cheapest_wins_preset_ranks_on_landed_cost_alone()
    {
        IReadOnlyList<IWeightedScoreCandidate> offers =
        [
            new Offer(Price: 1_000m, LeadTimeDays: 60, CreditDays: 0),
            new Offer(Price: 1_001m, LeadTimeDays: 1, CreditDays: 90)
        ];

        var scored = WeightedSupplierScoring.Score(offers, SupplierScoringWeights.Create(100, 0, 0, 0));

        Assert.Equal(100d, scored[0].Score);
        Assert.Equal(0d, scored[1].Score);
    }

    /// <summary>
    /// A criterion every offer agrees on separates nothing, so it contributes its full weight to all
    /// of them rather than being awarded arbitrarily to one. Inherited deliberately from the scorer
    /// this replaces.
    /// </summary>
    [Fact]
    public void A_criterion_that_is_constant_across_the_set_contributes_its_full_weight()
    {
        IReadOnlyList<IWeightedScoreCandidate> offers =
        [
            new Offer(Price: 1_000m, LeadTimeDays: 14, CreditDays: 30),
            new Offer(Price: 1_000m, LeadTimeDays: 14, CreditDays: 30)
        ];

        var scored = WeightedSupplierScoring.Score(offers, SupplierScoringWeights.Defaults);

        Assert.All(scored, r => Assert.Equal(100d, r.Score));
    }

    /// <summary>
    /// The unscorable offer must not stretch the scale the ranked offers are measured on. Its
    /// absurdly low price is not a comparison anyone can make, so it takes no part in the range.
    ///
    /// <para>The offer is made unscorable by a MISSING LEAD TIME, which carries weight 20 in the
    /// defaults. An earlier version of this test used a missing <c>CreditDays</c>, which stopped
    /// making the offer unscorable the moment the default payment-terms weight went to zero — and
    /// the test then failed loudly rather than passing vacuously, which is the only reason the
    /// change was caught. A test that proves a property must keep exercising it when the
    /// configuration around it moves.</para>
    /// </summary>
    [Fact]
    public void An_unscorable_offer_does_not_distort_the_range_the_others_are_scored_on()
    {
        IReadOnlyList<IWeightedScoreCandidate> comparable =
        [
            new Offer(Price: 1_000m, LeadTimeDays: 20, CreditDays: 30),
            new Offer(Price: 1_200m, LeadTimeDays: 10, CreditDays: 30)
        ];
        IReadOnlyList<IWeightedScoreCandidate> withAnOutlier =
        [
            new Offer(Price: 1_000m, LeadTimeDays: 20, CreditDays: 30),
            new Offer(Price: 1_200m, LeadTimeDays: 10, CreditDays: 30),
            // Priced at a hundredth of the others and unscorable: no lead time, which the default
            // weight set scores at 20. If this row entered the price range, the two real offers
            // would be squeezed from 80 and 0 to roughly 13 and 0 — a ranking distorted by a row
            // that is not even being ranked.
            new Offer(Price: 10m, LeadTimeDays: null, CreditDays: 30)
        ];

        var baseline = WeightedSupplierScoring.Score(comparable, SupplierScoringWeights.Defaults);
        var withOutlier = WeightedSupplierScoring.Score(withAnOutlier, SupplierScoringWeights.Defaults);

        Assert.Equal(baseline[0].Score, withOutlier[0].Score);
        Assert.Equal(baseline[1].Score, withOutlier[1].Score);
        Assert.Null(withOutlier[2].Score);
        Assert.NotNull(withOutlier[2].UnavailableReason);
    }

    /// <summary>
    /// A weight set that does not total 100 cannot be constructed at all. The scorer is the last
    /// line: even if a row reached it past the service and the CHECK constraint, it refuses rather
    /// than emitting a number that would be printed as a share of 100 and is not one.
    /// </summary>
    [Theory]
    [InlineData(70, 20, 0, 0)]
    [InlineData(70, 20, 10, 10)]
    [InlineData(0, 0, 0, 0)]
    public void A_weight_set_that_does_not_total_one_hundred_is_refused(
        int price, int leadTime, int warranty, int paymentTerms)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => SupplierScoringWeights.Create(price, leadTime, warranty, paymentTerms));
    }

    [Fact]
    public void A_negative_weight_is_refused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => SupplierScoringWeights.Create(110, -10, 0, 0));
    }

    [Fact]
    public void An_empty_candidate_set_scores_nothing_rather_than_throwing()
    {
        Assert.Empty(WeightedSupplierScoring.Score([], SupplierScoringWeights.Defaults));
    }

    /// <summary>
    /// A lone offer is not scored at all. Min-max normalisation over a set of one makes the min and
    /// the max the same number on every criterion, so the offer earns the full weight of each and
    /// totals a flawless "Weighted 100/100" having been compared to nothing — on the single-supplier
    /// RFQ, which is the most common case there is. It gets a reason instead.
    ///
    /// <para>Nothing else about the offer changes: the raw values are still reported, no contribution
    /// carries points that could be summed into an accidental total, and eligibility is decided
    /// elsewhere and is untouched.</para>
    /// </summary>
    [Fact]
    public void A_single_comparable_offer_is_not_scored_and_says_why()
    {
        IReadOnlyList<IWeightedScoreCandidate> onlyOffer =
            [new Offer(Price: 1_240m, LeadTimeDays: 21, CreditDays: 30)];

        var scored = WeightedSupplierScoring.Score(onlyOffer, SupplierScoringWeights.Defaults);

        var result = Assert.Single(scored);
        Assert.Null(result.Score);
        Assert.Equal(WeightedSupplierScoring.SingleComparableOfferReason, result.UnavailableReason);
        Assert.Contains("one comparable offer", result.UnavailableReason!, StringComparison.OrdinalIgnoreCase);
        Assert.All(result.Contributions, c => Assert.Null(c.PointsEarned));
        // The row is not blank: the facts the operator can verify are still on it.
        Assert.Equal(1_240d, result.Contributions
            .Single(c => c.Criterion == SupplierScoreCriteria.Price).RawValue);
        Assert.Equal(21d, result.Contributions
            .Single(c => c.Criterion == SupplierScoreCriteria.LeadTime).RawValue);
    }

    /// <summary>
    /// The same refusal when the set is larger but only ONE offer in it is actually comparable: the
    /// range is taken over the scorable offers, and a range of one is a range of nothing. The
    /// unscorable offer keeps its own, more specific, missing-criterion reason.
    /// </summary>
    [Fact]
    public void One_scorable_offer_among_several_is_still_not_scored()
    {
        IReadOnlyList<IWeightedScoreCandidate> offers =
        [
            new Offer(Price: 1_000m, LeadTimeDays: 20, CreditDays: 30),
            new Offer(Price: 900m, LeadTimeDays: null, CreditDays: 30)
        ];

        var scored = WeightedSupplierScoring.Score(offers, SupplierScoringWeights.Defaults);

        Assert.Null(scored[0].Score);
        Assert.Equal(WeightedSupplierScoring.SingleComparableOfferReason, scored[0].UnavailableReason);
        Assert.Equal("Cannot score — lead time missing", scored[1].UnavailableReason);
    }

    /// <summary>
    /// THE GUARD AGAINST OVER-FIXING. Two offers that tie on a criterion still both earn its full
    /// weight — that is the constant-criterion rule, and it is correct and neutral because they are
    /// genuinely being compared and genuinely agree. Only a set with ONE comparable offer refuses.
    /// </summary>
    [Fact]
    public void Two_offers_tying_on_a_criterion_still_both_earn_its_full_weight()
    {
        IReadOnlyList<IWeightedScoreCandidate> offers =
        [
            // Identical lead time — the tied criterion — with prices that differ, so the set is a
            // real comparison and only the lead-time weight is at stake.
            new Offer(Price: 1_000m, LeadTimeDays: 14, CreditDays: 30),
            new Offer(Price: 1_200m, LeadTimeDays: 14, CreditDays: 30)
        ];

        var scored = WeightedSupplierScoring.Score(offers, SupplierScoringWeights.Defaults);

        Assert.All(scored, r =>
        {
            Assert.Null(r.UnavailableReason);
            Assert.Equal((double)SupplierScoringWeights.Defaults.LeadTime,
                r.Contributions.Single(c => c.Criterion == SupplierScoreCriteria.LeadTime).PointsEarned);
        });
        // And the criterion they do NOT tie on still separates them.
        Assert.True(scored[0].Score > scored[1].Score);
    }
}
