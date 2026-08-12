using System.Text.Json;
using ERP_RFQ_Automation.Agent;
using ERP_RFQ_Automation.Agent.Tools;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Procurement;
using ERP_RFQ_Automation.SupplierEvaluation;
using ERP_RFQ_Automation.Tests.Support;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// FR-QTM-03 end to end, on the comparison a human actually awards from.
///
/// <para>Two recommenders existed on this data. The authoritative one — the supplier-quote
/// comparison behind the sourcing workbench — sorted on coverage then landed cost then lead time,
/// with no weights a customer could set. The other, in the agent tool layer, ranked on weights
/// hardcoded at price 0.5 / lead time 0.25 / success rate 0.25. On the same quotes they could name
/// different winners, and nothing on screen said which one the operator was reading.</para>
///
/// <para>These tests pin the unification: one scorer, weights from the tenant's row, and the same
/// winner from both paths. They also pin the two refusals that keep a score from being worse than
/// no score at all — an offer whose weighted criteria are not all captured is not scored and not
/// zeroed, and a candidate set that spans currencies is refused rather than ranked.</para>
/// </summary>
public sealed class SupplierComparisonScoringTests
{
    private const long SecondSupplierId = ProcurementTestData.Supplier + 1;
    private const long ThirdSupplierId = ProcurementTestData.Supplier + 2;
    private const long SecondCurrencyId = ProcurementTestData.Currency + 1;

    [Fact]
    public async Task Changing_the_weight_set_changes_which_supplier_is_recommended()
    {
        using var fixture = new ProcurementScenario();
        await SetCreditDaysAsync(fixture, ProcurementTestData.Supplier, 30);
        await AddSupplierAsync(fixture, SecondSupplierId, "Fast Supplier", creditDays: 30);

        // Cheap and slow against dear and fast, on the same line, covering it equally — so nothing
        // but the weights can decide between them.
        var cheapAndSlow = await QuoteAsync(fixture, ProcurementTestData.Supplier, "cheap", 12m, 10);
        var dearAndFast = await QuoteAsync(fixture, SecondSupplierId, "fast", 20m, 2);

        // "Cheapest wins" — the behaviour the platform had hardcoded before this gate.
        await SetWeightsAsync(fixture, 100, 0, 0, 0, "weights-cheapest");
        var onPrice = await fixture.Execute(service =>
            service.CompareQuotesAsync(fixture.BusinessUnitId, fixture.RfqItemId));

        // "Speed matters" — the same quotes, a different business.
        await SetWeightsAsync(fixture, 40, 50, 0, 10, "weights-speed");
        var onSpeed = await fixture.Execute(service =>
            service.CompareQuotesAsync(fixture.BusinessUnitId, fixture.RfqItemId));

        Assert.Equal(cheapAndSlow, onPrice.RecommendedSupplierQuotedItemId);
        Assert.Equal(dearAndFast, onSpeed.RecommendedSupplierQuotedItemId);

        // And the recommendation the operator reads is the top of the list they read it in.
        Assert.Equal(cheapAndSlow, onPrice.Lines.First().SupplierQuotedItemId);
        Assert.Equal(dearAndFast, onSpeed.Lines.First().SupplierQuotedItemId);
    }

    [Fact]
    public async Task Every_scored_line_shows_the_arithmetic_that_produced_its_score()
    {
        using var fixture = new ProcurementScenario();
        await SetCreditDaysAsync(fixture, ProcurementTestData.Supplier, 30);
        await AddSupplierAsync(fixture, SecondSupplierId, "Fast Supplier", creditDays: 60);
        await QuoteAsync(fixture, ProcurementTestData.Supplier, "cheap", 12m, 10);
        await QuoteAsync(fixture, SecondSupplierId, "fast", 20m, 2);
        await SetWeightsAsync(fixture, 40, 50, 0, 10, "weights-speed");

        var comparison = await fixture.Execute(service =>
            service.CompareQuotesAsync(fixture.BusinessUnitId, fixture.RfqItemId));

        Assert.All(comparison.Lines, line =>
        {
            Assert.NotNull(line.WeightedScore);
            Assert.Null(line.ScoreUnavailableReason);
            Assert.NotNull(line.ScoreBreakdown);
            // The row visibly sums to the number beside it: a recommendation whose arithmetic the
            // operator cannot check is a black box, however good the arithmetic is.
            Assert.Equal(line.WeightedScore!.Value,
                line.ScoreBreakdown!.Sum(contribution => contribution.PointsEarned ?? 0d), 2);
            Assert.InRange(line.WeightedScore.Value, 0, SupplierScoringWeights.MaximumScore);
            // Four criteria, each carrying the weight the tenant set — no fifth criterion, and the
            // switched-off one is still reported rather than hidden.
            Assert.Equal(4, line.ScoreBreakdown.Count);
            Assert.Equal(SupplierScoringWeights.MaximumScore,
                line.ScoreBreakdown.Sum(contribution => contribution.Weight));
        });

        // The facts the line used to drop, now carried on it: no new query fetched them.
        var winner = comparison.Lines.Single(x =>
            x.SupplierQuotedItemId == comparison.RecommendedSupplierQuotedItemId);
        Assert.Equal("Fast Supplier", winner.SupplierName);
        Assert.Equal(60, winner.CreditDays);
        Assert.Equal(60d, winner.ScoreBreakdown!
            .Single(x => x.Criterion == SupplierScoreCriteria.PaymentTerms).RawValue);
    }

    [Fact]
    public async Task An_offer_that_cannot_be_scored_stays_awardable_and_sorts_below_the_scored_ones()
    {
        using var fixture = new ProcurementScenario();
        // Payment terms must carry weight for a missing credit-days value to be fatal. The DEFAULT
        // set weights it at 0 — deliberately, because credit days is introduced by this gate and is
        // null on every supplier that already exists — so this test states the weighting it needs
        // rather than inheriting one. An earlier version relied on the default being 10 and stopped
        // exercising its own property the day that default was corrected.
        await SetWeightsAsync(fixture, 70, 20, 0, 10, "weights-payment-terms");
        await SetCreditDaysAsync(fixture, ProcurementTestData.Supplier, 30);
        await AddSupplierAsync(fixture, SecondSupplierId, "Second Supplier", creditDays: 60);
        // No credit days captured: the tenant weights payment terms at 10, so this offer has no
        // value for a weighted criterion. It is deliberately CHEAPER and FASTER than the others,
        // so a scorer that quietly treated the gap as zero would be caught by the winner changing.
        await AddSupplierAsync(fixture, ThirdSupplierId, "Unclassified Supplier", creditDays: null);
        var scorable = await QuoteAsync(fixture, ProcurementTestData.Supplier, "scored", 12m, 5);
        await QuoteAsync(fixture, SecondSupplierId, "also-scored", 15m, 9);
        var unscorable = await QuoteAsync(fixture, ThirdSupplierId, "unscored", 8m, 3);

        var comparison = await fixture.Execute(service =>
            service.CompareQuotesAsync(fixture.BusinessUnitId, fixture.RfqItemId));

        var lines = comparison.Lines.ToArray();
        var missing = lines.Single(x => x.SupplierQuotedItemId == unscorable);

        // No score, and no zero either — the two are not the same claim.
        Assert.Null(missing.WeightedScore);
        Assert.NotNull(missing.ScoreUnavailableReason);
        Assert.Contains("payment terms", missing.ScoreUnavailableReason!, StringComparison.Ordinal);
        Assert.DoesNotContain(missing.ScoreBreakdown!,
            contribution => contribution.PointsEarned.HasValue);

        // Still fully awardable. The score annotates; eligibility is decided elsewhere and was not
        // touched.
        Assert.True(missing.Eligible);
        Assert.Empty(missing.Blockers);

        // Below BOTH scored offers, not mixed in among the ranked ones as though it had lost — even
        // though it is the cheapest and the fastest quote on the line.
        Assert.Equal(scorable, lines[0].SupplierQuotedItemId);
        Assert.Equal(unscorable, lines[^1].SupplierQuotedItemId);
        Assert.All(lines[..^1], line => Assert.NotNull(line.WeightedScore));
        Assert.Equal(scorable, comparison.RecommendedSupplierQuotedItemId);
    }

    [Fact]
    public async Task A_mixed_currency_comparison_is_still_refused_and_never_scored()
    {
        using var fixture = new ProcurementScenario();
        await SetCreditDaysAsync(fixture, ProcurementTestData.Supplier, 30);
        await AddSupplierAsync(fixture, SecondSupplierId, "Euro Supplier", creditDays: 30);
        await AddCurrencyAsync(fixture);
        await QuoteAsync(fixture, ProcurementTestData.Supplier, "local", 12m, 5);
        await QuoteAsync(fixture, SecondSupplierId, "foreign", 9m, 5, SecondCurrencyId);

        var comparison = await fixture.Execute(service =>
            service.CompareQuotesAsync(fixture.BusinessUnitId, fixture.RfqItemId));

        // A cheaper bare decimal in another currency is not a cheaper offer, and a score would have
        // dressed that inversion up as a recommendation.
        Assert.Null(comparison.RecommendedSupplierQuotedItemId);
        Assert.All(comparison.Lines, line =>
        {
            Assert.False(line.Eligible);
            Assert.Contains("currency not comparable without approved FX evidence", line.Blockers);
            Assert.Null(line.WeightedScore);
        });
    }

    /// <summary>
    /// The single-supplier RFQ — the most common comparison there is. One offer is not scored,
    /// because a weighted score ranks offers against each other and there is nothing here to rank it
    /// against. Before this, min-max normalisation over a set of one gave it the full weight of every
    /// criterion and the workbench printed "Weighted 100/100" for an offer that was never compared.
    ///
    /// <para>Everything else about the offer is unchanged: eligible, no blockers, still the
    /// recommendation a human can award, and the verifiable landed-cost fact still on the row.</para>
    /// </summary>
    [Fact]
    public async Task A_lone_offer_is_not_scored_but_stays_awardable_and_recommended()
    {
        using var fixture = new ProcurementScenario();
        await SetCreditDaysAsync(fixture, ProcurementTestData.Supplier, 30);
        var onlyQuote = await QuoteAsync(fixture, ProcurementTestData.Supplier, "sole", 12m, 10);

        var comparison = await fixture.Execute(service =>
            service.CompareQuotesAsync(fixture.BusinessUnitId, fixture.RfqItemId));

        var line = Assert.Single(comparison.Lines);
        Assert.Equal(onlyQuote, line.SupplierQuotedItemId);

        // No score, and emphatically not a perfect one.
        Assert.Null(line.WeightedScore);
        Assert.NotNull(line.ScoreUnavailableReason);
        Assert.Contains("one comparable offer", line.ScoreUnavailableReason!,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(line.ScoreBreakdown!, contribution => contribution.PointsEarned.HasValue);

        // Fully awardable, and still the recommendation: the score annotates, the human awards.
        Assert.True(line.Eligible);
        Assert.Empty(line.Blockers);
        Assert.Equal(onlyQuote, comparison.RecommendedSupplierQuotedItemId);

        // The verifiable fact the chip is drawn from is untouched — an unverifiable score was never
        // allowed to replace it, and now there is no score at all.
        Assert.NotNull(line.LandedUnitCost);
    }

    /// <summary>
    /// A mixed-currency comparison already refused to rank, and that refusal stands — but it said so
    /// only in the blocker list, while the score cell on every row was simply empty with nothing to
    /// explain it. Scoring is skipped precisely BECAUSE the set spans currencies, so the skip has to
    /// carry the reason itself.
    /// </summary>
    [Fact]
    public async Task A_mixed_currency_comparison_states_why_no_line_carries_a_score()
    {
        using var fixture = new ProcurementScenario();
        await SetCreditDaysAsync(fixture, ProcurementTestData.Supplier, 30);
        await AddSupplierAsync(fixture, SecondSupplierId, "Euro Supplier", creditDays: 30);
        await AddCurrencyAsync(fixture);
        await QuoteAsync(fixture, ProcurementTestData.Supplier, "local", 12m, 5);
        await QuoteAsync(fixture, SecondSupplierId, "foreign", 9m, 5, SecondCurrencyId);

        var comparison = await fixture.Execute(service =>
            service.CompareQuotesAsync(fixture.BusinessUnitId, fixture.RfqItemId));

        Assert.NotEmpty(comparison.Lines);
        Assert.All(comparison.Lines, line =>
        {
            Assert.Null(line.WeightedScore);
            // Every row says why, in plain words that name the actual cause.
            Assert.False(string.IsNullOrWhiteSpace(line.ScoreUnavailableReason),
                $"line {line.SupplierQuotedItemId} shows an empty score with no explanation");
            Assert.Contains("currency", line.ScoreUnavailableReason!, StringComparison.OrdinalIgnoreCase);
            // The fail-closed refusal itself is unchanged — this explains it, it does not relax it.
            Assert.False(line.Eligible);
            Assert.Contains("currency not comparable without approved FX evidence", line.Blockers);
        });
        Assert.Null(comparison.RecommendedSupplierQuotedItemId);
    }

    [Fact]
    public async Task The_agent_tool_and_the_governed_comparison_name_the_same_winner()
    {
        using var fixture = new ProcurementScenario();
        await SetCreditDaysAsync(fixture, ProcurementTestData.Supplier, 30);
        await AddSupplierAsync(fixture, SecondSupplierId, "Fast Supplier", creditDays: 30);
        await QuoteAsync(fixture, ProcurementTestData.Supplier, "cheap", 12m, 10);
        var dearAndFast = await QuoteAsync(fixture, SecondSupplierId, "fast", 20m, 2);
        // Weighted towards delivery, so the winner is NOT the cheapest offer. Before the two paths
        // were unified this is exactly where they diverged: the workbench sorted on landed cost and
        // named the cheap supplier, the agent tool ranked on its own constants.
        await SetWeightsAsync(fixture, 40, 50, 0, 10, "weights-speed");

        var comparison = await fixture.Execute(service =>
            service.CompareQuotesAsync(fixture.BusinessUnitId, fixture.RfqItemId));
        var governedWinner = comparison.Lines
            .Single(x => x.SupplierQuotedItemId == comparison.RecommendedSupplierQuotedItemId).SupplierId;

        await using var context = fixture.Context();
        var toolResult = await new CompareSupplierQuotesTool(context).ExecuteAsync(
            AgentSeed.Json($"{{\"rfqId\":{fixture.RfqId}}}"),
            new AgentToolContext { BusinessUnitId = fixture.BusinessUnitId, UserId = 42, UserName = "qa" },
            default);

        Assert.True(toolResult.Success, toolResult.Error);
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(toolResult.Data));
        var root = document.RootElement;
        Assert.Equal(SecondSupplierId, governedWinner);
        Assert.Equal(governedWinner, root.GetProperty("recommendedSupplierId").GetInt64());
        Assert.Equal(governedWinner, Assert.Single(root.GetProperty("lines").EnumerateArray())
            .GetProperty("bestSupplierId").GetInt64());

        // Both are running the tenant's weight set, not two sets of constants.
        Assert.Equal(40, root.GetProperty("weights").GetProperty("price").GetInt32());
        Assert.Equal(50, root.GetProperty("weights").GetProperty("leadTime").GetInt32());

        // The award the tool advises is the one the workbench shows as best.
        Assert.Equal(dearAndFast, comparison.RecommendedSupplierQuotedItemId);
    }

    [Fact]
    public async Task The_agent_tool_declines_to_name_a_winner_it_cannot_score()
    {
        using var fixture = new ProcurementScenario();
        // Credit days captured for nobody, on a tenant that weights payment terms at 10. The weights
        // are stated here rather than inherited: the DEFAULT payment-terms weight is 0, precisely so
        // the feature is usable on the day it ships, and a test that leans on a default it does not
        // control silently stops testing anything when that default is corrected.
        await SetWeightsAsync(fixture, 70, 20, 0, 10, "weights-payment-terms");
        await AddSupplierAsync(fixture, SecondSupplierId, "Unclassified Supplier", creditDays: null);
        await QuoteAsync(fixture, ProcurementTestData.Supplier, "cheap", 12m, 10);
        await QuoteAsync(fixture, SecondSupplierId, "fast", 20m, 2);

        await using var context = fixture.Context();
        var toolResult = await new CompareSupplierQuotesTool(context).ExecuteAsync(
            AgentSeed.Json($"{{\"rfqId\":{fixture.RfqId}}}"),
            new AgentToolContext { BusinessUnitId = fixture.BusinessUnitId, UserId = 42, UserName = "qa" },
            default);

        Assert.True(toolResult.Success, toolResult.Error);
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(toolResult.Data));
        var root = document.RootElement;

        // No winner rather than an arbitrary one, and the quotes are still reported: they are valid
        // offers a human may award, they simply cannot be ranked on criteria nobody captured.
        Assert.Equal(JsonValueKind.Null, root.GetProperty("recommendedSupplierId").ValueKind);
        Assert.Contains("could be scored", root.GetProperty("rationale").GetString()!, StringComparison.Ordinal);
        var line = Assert.Single(root.GetProperty("lines").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, line.GetProperty("bestSupplierId").ValueKind);
        Assert.Contains("payment terms", line.GetProperty("bestUnavailableReason").GetString()!,
            StringComparison.Ordinal);
        Assert.Equal(2, line.GetProperty("bids").EnumerateArray().Count());
    }

    private static async Task SetCreditDaysAsync(ProcurementScenario fixture, long supplierId, int? creditDays)
    {
        await using var context = fixture.Context();
        var supplier = context.Suppliers.Single(x => x.Id == supplierId);
        supplier.CreditDays = creditDays;
        await context.SaveChangesAsync();
    }

    /// <summary>
    /// A second award-eligible supplier on the seeded tenant. Its governance columns mirror the
    /// fixture's own supplier so that eligibility is identical and the comparison turns on the
    /// commercial numbers alone.
    /// </summary>
    private static async Task AddSupplierAsync(
        ProcurementScenario fixture, long supplierId, string name, int? creditDays)
    {
        await using var context = fixture.Context();
        var supplier = AgentSeed.Supplier(context, supplierId, fixture.BusinessUnitId, name,
            $"supplier-{supplierId}@example.test");
        supplier.GovernanceStatus = SupplierGovernanceStatuses.Approved;
        supplier.VerificationStatus = SupplierVerificationStatuses.Verified;
        supplier.ComplianceStatus = SupplierComplianceStatuses.Cleared;
        supplier.RiskStatus = SupplierRiskStatuses.Low;
        supplier.ReadinessStatus = SupplierReadinessStatuses.Ready;
        supplier.ConcurrencyToken = Guid.NewGuid();
        supplier.CreditDays = creditDays;
        await context.SaveChangesAsync();
    }

    private static async Task AddCurrencyAsync(ProcurementScenario fixture)
    {
        await using var context = fixture.Context();
        context.Currencies.Add(new Currency
        {
            Id = SecondCurrencyId,
            BusinessUnitId = fixture.BusinessUnitId,
            Code = "QEU",
            CurrencyName = "QA Foreign Currency",
            ExchangeRate = 1m,
            IsActive = true,
            CreatedBy = "qa",
            CreatedOn = DateTime.UtcNow
        });
        await context.SaveChangesAsync();
    }

    private static async Task SetWeightsAsync(ProcurementScenario fixture,
        int price, int leadTime, int warranty, int paymentTerms, string key)
    {
        await using var context = fixture.Context();
        await new SupplierComparisonWeightsService(context).UpdateAsync(
            fixture.BusinessUnitId, 42, "qa", key,
            new UpdateSupplierComparisonWeightsCommand(price, leadTime, warranty, paymentTerms,
                "Gate 2 comparison weighting"));
    }

    private static async Task<long> QuoteAsync(ProcurementScenario fixture, long supplierId, string key,
        decimal unitPrice, int leadTimeDays, long? currencyId = null)
    {
        var solicitation = await fixture.Execute(service => service.CreateSolicitationAsync(
            fixture.Solicitation($"{key}-sol") with { SupplierId = supplierId }));
        await fixture.MarkSolicitationSentAsync(solicitation.Id);
        var quote = await fixture.Execute(service => service.CaptureSupplierQuoteAsync(
            fixture.Quote(solicitation.Id, $"{key}-quote") with
            {
                Lines = [fixture.QuoteLine() with
                {
                    UnitPrice = unitPrice,
                    LeadTimeDays = leadTimeDays,
                    CurrencyId = currencyId ?? ProcurementTestData.Currency
                }]
            }));
        return Assert.Single(quote.LineIds);
    }
}
