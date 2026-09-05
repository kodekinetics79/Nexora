using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Procurement;
using ERP_RFQ_Automation.SupplierEvaluation;
using ERP_RFQ_Automation.SupplierQuotes;
using ERP_RFQ_Automation.Tests.Support;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// FR-QTM-03's fourth criterion, made real.
///
/// <para>Warranty was named as one of four weighted criteria and was the only one with nothing
/// behind it: the supplier quote line carried a free-text <c>Warranty</c> column ("24 months on
/// parts, 12 on labour") while the scorer consumed a numeric <c>WarrantyMonths</c> that no code path
/// ever supplied. The projection onto the scorer returned a literal <c>null</c>. So the warranty
/// weight could never be raised above zero without every offer correctly reporting that it could not
/// be scored — three of four criteria worked, and the fourth was permanently inert.</para>
///
/// <para>The fix is a captured number, not a parsed one. An operator types the months or there is no
/// number; nothing reads the prose. These tests hold that line: the number ranks, its absence
/// refuses to rank rather than scoring zero, longer beats shorter, the supplier's own wording is
/// never touched, and a value nobody could have meant is refused at both capture doors.</para>
/// </summary>
public sealed class SupplierWarrantyMonthsTests
{
    private const long SecondSupplierId = ProcurementTestData.Supplier + 1;
    private const long ThirdSupplierId = ProcurementTestData.Supplier + 2;

    /// <summary>
    /// The control fires on the criterion that used to have no control at all. The same two offers
    /// produce two different winners depending only on whether the tenant weights warranty — which
    /// is impossible while <c>WarrantyMonths</c> is hardcoded null, because then the warranty-weighted
    /// run scores nothing and recommends the same offer as before.
    /// </summary>
    [Fact]
    public async Task Weighting_warranty_changes_which_supplier_is_recommended()
    {
        using var fixture = new ProcurementScenario();
        await AddSupplierAsync(fixture, SecondSupplierId, "Long Warranty Supplier");

        // Cheaper, but a one-year warranty against a five-year one.
        var cheapShortWarranty = await QuoteAsync(fixture, ProcurementTestData.Supplier, "cheap",
            unitPrice: 12m, leadTimeDays: 10, warrantyMonths: 12);
        var dearLongWarranty = await QuoteAsync(fixture, SecondSupplierId, "durable",
            unitPrice: 20m, leadTimeDays: 10, warrantyMonths: 60);

        // Warranty switched off — the shipping default, and the behaviour of every tenant that has
        // not started capturing the number.
        await SetWeightsAsync(fixture, 80, 20, 0, 0, "warranty-off");
        var ignoringWarranty = await fixture.Execute(service =>
            service.CompareQuotesAsync(fixture.BusinessUnitId, fixture.RfqItemId));

        // The same quotes, for a buyer who cares more about the warranty than about the price.
        await SetWeightsAsync(fixture, 30, 10, 60, 0, "warranty-on");
        var weightingWarranty = await fixture.Execute(service =>
            service.CompareQuotesAsync(fixture.BusinessUnitId, fixture.RfqItemId));

        Assert.Equal(cheapShortWarranty, ignoringWarranty.RecommendedSupplierQuotedItemId);
        Assert.Equal(dearLongWarranty, weightingWarranty.RecommendedSupplierQuotedItemId);

        // And the arithmetic is on the row: the long warranty earned the whole warranty weight, the
        // short one earned none of it, and the raw months are the numbers the operator typed.
        var winner = weightingWarranty.Lines.Single(x => x.SupplierQuotedItemId == dearLongWarranty);
        var loser = weightingWarranty.Lines.Single(x => x.SupplierQuotedItemId == cheapShortWarranty);
        Assert.Equal(60, winner.WarrantyMonths);
        Assert.Equal(12, loser.WarrantyMonths);
        Assert.Equal(60d, Warranty(winner).PointsEarned);
        Assert.Equal(0d, Warranty(loser).PointsEarned);
        Assert.Equal(60d, Warranty(winner).RawValue);
        Assert.Equal(12d, Warranty(loser).RawValue);
    }

    /// <summary>
    /// Direction. Longer warranty is better, and getting that backwards would be a confident wrong
    /// recommendation rather than a visible bug — the losing row would still show a plausible score.
    /// Everything but the warranty is held identical, so nothing else can decide the order.
    /// </summary>
    [Fact]
    public async Task With_all_else_equal_the_longer_warranty_ranks_above_the_shorter()
    {
        using var fixture = new ProcurementScenario();
        await AddSupplierAsync(fixture, SecondSupplierId, "Longer Warranty Supplier");

        var shorter = await QuoteAsync(fixture, ProcurementTestData.Supplier, "short",
            unitPrice: 12m, leadTimeDays: 10, warrantyMonths: 12);
        var longer = await QuoteAsync(fixture, SecondSupplierId, "long",
            unitPrice: 12m, leadTimeDays: 10, warrantyMonths: 36);
        await SetWeightsAsync(fixture, 40, 40, 20, 0, "warranty-direction");

        var comparison = await fixture.Execute(service =>
            service.CompareQuotesAsync(fixture.BusinessUnitId, fixture.RfqItemId));

        var lines = comparison.Lines.ToArray();
        Assert.Equal(longer, lines[0].SupplierQuotedItemId);
        Assert.Equal(shorter, lines[1].SupplierQuotedItemId);
        Assert.Equal(longer, comparison.RecommendedSupplierQuotedItemId);
        Assert.True(lines[0].WeightedScore > lines[1].WeightedScore,
            $"36 months scored {lines[0].WeightedScore}, 12 months scored {lines[1].WeightedScore}");
    }

    /// <summary>
    /// Ruling R-F on the new criterion. A line whose warranty was never captured is not scored zero
    /// — zero is indistinguishable from "shortest warranty in the set", and this offer is the
    /// cheapest and the fastest, so a zero would have quietly cost it the award it should still be
    /// able to win on a human's judgement.
    /// </summary>
    [Fact]
    public async Task An_offer_with_no_warranty_number_is_not_scored_and_says_which_criterion_is_missing()
    {
        using var fixture = new ProcurementScenario();
        await AddSupplierAsync(fixture, SecondSupplierId, "Second Supplier");
        await AddSupplierAsync(fixture, ThirdSupplierId, "Uncaptured Warranty Supplier");
        await SetWeightsAsync(fixture, 40, 40, 20, 0, "warranty-weighted");

        var scored = await QuoteAsync(fixture, ProcurementTestData.Supplier, "scored",
            unitPrice: 12m, leadTimeDays: 5, warrantyMonths: 24);
        await QuoteAsync(fixture, SecondSupplierId, "also-scored",
            unitPrice: 15m, leadTimeDays: 9, warrantyMonths: 12);
        // No warranty period recorded. Deliberately the cheapest and the fastest offer on the line.
        var uncaptured = await QuoteAsync(fixture, ThirdSupplierId, "uncaptured",
            unitPrice: 8m, leadTimeDays: 3, warrantyMonths: null);

        var comparison = await fixture.Execute(service =>
            service.CompareQuotesAsync(fixture.BusinessUnitId, fixture.RfqItemId));

        var lines = comparison.Lines.ToArray();
        var missing = lines.Single(x => x.SupplierQuotedItemId == uncaptured);

        Assert.Null(missing.WeightedScore);
        Assert.Null(missing.WarrantyMonths);
        Assert.NotNull(missing.ScoreUnavailableReason);
        Assert.Contains("Cannot score", missing.ScoreUnavailableReason!, StringComparison.Ordinal);
        Assert.Contains("warranty", missing.ScoreUnavailableReason!, StringComparison.Ordinal);
        // No criterion earned points, so no zero can be mistaken for a measurement.
        Assert.DoesNotContain(missing.ScoreBreakdown!, contribution => contribution.PointsEarned.HasValue);

        // Fully awardable, and sorted below the offers that could be ranked rather than mixed in
        // among them as though it had lost on the merits.
        Assert.True(missing.Eligible);
        Assert.Empty(missing.Blockers);
        Assert.Equal(uncaptured, lines[^1].SupplierQuotedItemId);
        Assert.All(lines[..^1], line => Assert.NotNull(line.WeightedScore));
        Assert.Equal(scored, comparison.RecommendedSupplierQuotedItemId);
    }

    /// <summary>
    /// Warranty at weight zero — the shipping default — asks nothing of the data. Existing lines
    /// carry no warranty number at all, so if the criterion were required regardless of its weight
    /// this gate would ship a comparison that refuses to rank anything on day one.
    /// </summary>
    [Fact]
    public async Task An_uncaptured_warranty_blocks_nothing_while_the_weight_is_zero()
    {
        using var fixture = new ProcurementScenario();
        await AddSupplierAsync(fixture, SecondSupplierId, "Second Supplier");
        await QuoteAsync(fixture, ProcurementTestData.Supplier, "cheap",
            unitPrice: 12m, leadTimeDays: 10, warrantyMonths: null);
        await QuoteAsync(fixture, SecondSupplierId, "fast",
            unitPrice: 20m, leadTimeDays: 2, warrantyMonths: null);

        Assert.Equal(0, SupplierScoringWeights.Defaults.Warranty);

        var comparison = await fixture.Execute(service =>
            service.CompareQuotesAsync(fixture.BusinessUnitId, fixture.RfqItemId));

        Assert.NotNull(comparison.RecommendedSupplierQuotedItemId);
        Assert.All(comparison.Lines, line =>
        {
            Assert.NotNull(line.WeightedScore);
            Assert.Null(line.ScoreUnavailableReason);
            // The criterion is still reported, at the weight the tenant set, so the row shows the
            // whole weight set rather than hiding the one that is switched off.
            Assert.Equal(0, Warranty(line).Weight);
            Assert.Null(Warranty(line).RawValue);
        });
    }

    /// <summary>
    /// The number is captured beside the wording, and the wording is left exactly as the supplier
    /// wrote it. A warranty clause carries conditions no single integer can hold; destroying or
    /// rewriting it to fit the score would trade a true statement for a partial one.
    /// </summary>
    [Fact]
    public async Task The_typed_number_is_persisted_and_the_suppliers_own_wording_survives_untouched()
    {
        const string wording = "24 months on parts, 12 on labour, excluding consumables";
        var store = new CapturingStore();

        await new SupplierQuoteInboxService(store).CaptureAsync(
            Command() with { Lines = [Line(warranty: wording, warrantyMonths: 24)] });

        var line = Assert.Single(Assert.Single(store.Revisions).Lines);
        Assert.Equal(24, line.WarrantyMonths);
        Assert.Equal(wording, line.Warranty);
    }

    /// <summary>
    /// Not captured stays expressible. Null is the state of every line that predates the column and
    /// of every quote that never named a period, and it must survive capture as null rather than
    /// being defaulted to zero months — which would assert that the supplier offered no warranty.
    /// </summary>
    [Fact]
    public async Task A_quote_that_names_no_warranty_period_is_captured_as_uncaptured()
    {
        var store = new CapturingStore();

        await new SupplierQuoteInboxService(store).CaptureAsync(
            Command() with { Lines = [Line(warranty: null, warrantyMonths: null)] });

        var line = Assert.Single(Assert.Single(store.Revisions).Lines);
        Assert.Null(line.WarrantyMonths);
        Assert.Null(line.Warranty);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(SupplierQuoteWarranty.MaximumMonths + 1)]
    public async Task A_warranty_length_nobody_could_have_meant_is_refused_at_capture(int months)
    {
        var store = new CapturingStore();

        var error = await Assert.ThrowsAsync<SupplierQuoteValidationException>(() =>
            new SupplierQuoteInboxService(store).CaptureAsync(
                Command() with { Lines = [Line(warranty: "as per contract", warrantyMonths: months)] }));

        // Named, not folded into a generic "line contains invalid values": the operator has to know
        // which field to fix.
        Assert.Contains("warranty months", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(store.Revisions);
    }

    /// <summary>
    /// The buyer workbench is the other door onto the same canonical line, and it refuses the same
    /// values — before anything is persisted, and in its own vocabulary rather than by surfacing a
    /// line number the caller never chose.
    /// </summary>
    [Theory]
    [InlineData(-6)]
    [InlineData(SupplierQuoteWarranty.MaximumMonths + 1)]
    public async Task The_workbench_refuses_a_warranty_length_nobody_could_have_meant(int months)
    {
        using var fixture = new ProcurementScenario();
        var solicitation = await fixture.Execute(service =>
            service.CreateSolicitationAsync(fixture.Solicitation("bad-warranty-sol")));
        await fixture.MarkSolicitationSentAsync(solicitation.Id);

        var error = await Assert.ThrowsAsync<ProcurementValidationException>(() =>
            fixture.Execute(service => service.CaptureSupplierQuoteAsync(
                fixture.Quote(solicitation.Id, "bad-warranty") with
                {
                    Lines = [fixture.QuoteLine() with { WarrantyMonths = months }]
                })));

        Assert.Contains("warranty months", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static SupplierScoreContribution Warranty(QuoteComparisonLine line) =>
        line.ScoreBreakdown!.Single(x => x.Criterion == SupplierScoreCriteria.Warranty);

    private static async Task SetWeightsAsync(ProcurementScenario fixture,
        int price, int leadTime, int warranty, int paymentTerms, string key)
    {
        await using var context = fixture.Context();
        await new SupplierComparisonWeightsService(context).UpdateAsync(
            fixture.BusinessUnitId, 42, "qa", key,
            new UpdateSupplierComparisonWeightsCommand(price, leadTime, warranty, paymentTerms,
                "Warranty comparison weighting"));
    }

    /// <summary>
    /// A second award-eligible supplier whose governance mirrors the fixture's own, so eligibility is
    /// identical across the set and the comparison turns on the commercial numbers alone.
    /// </summary>
    private static async Task AddSupplierAsync(ProcurementScenario fixture, long supplierId, string name)
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
        await context.SaveChangesAsync();
    }

    private static async Task<long> QuoteAsync(ProcurementScenario fixture, long supplierId, string key,
        decimal unitPrice, int leadTimeDays, int? warrantyMonths)
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
                    WarrantyMonths = warrantyMonths
                }]
            }));
        return Assert.Single(quote.LineIds);
    }

    private static ERP_RFQ_Automation.SupplierQuotes.CaptureSupplierQuoteCommand Command() => new(
        7, 31, 41, 51, "NX-2026-0001", "SUP-Q-900", 1,
        SupplierQuoteCaptureChannels.Manual, null, "warranty-capture", new string('A', 64),
        61, DateTime.UtcNow.AddDays(30), "FCA", 20, 5, 9, 3, 2, "NET 30", null,
        [Line(null, null)], [], "warranty-capture-1", "buyer@example.com", "corr-warranty");

    private static ERP_RFQ_Automation.SupplierQuotes.CaptureSupplierQuoteLine Line(
        string? warranty, int? warrantyMonths) => new(
        1, 71, 81, "PN-900", "Maker", "SP-900", "Industrial component", 10, 8,
        "EA", 12.50m, 5, 4, "PARTIAL", "US", warranty, false, null, [], warrantyMonths);

    /// <summary>
    /// The smallest store that lets <see cref="SupplierQuoteInboxService.CaptureAsync"/> run: it
    /// keeps what was persisted so the test can read the columns back, and refuses everything the
    /// capture path does not use rather than pretending to implement it.
    /// </summary>
    private sealed class CapturingStore : ISupplierQuoteStore
    {
        public long? ScopedTenantId => 7;
        public List<SupplierQuoteRevision> Revisions { get; } = [];

        public Task<SupplierQuoteAnchor?> ResolveAnchorAsync(long businessUnitId, long supplierId,
            long solicitationId, long sourcingCaseId, CancellationToken cancellationToken) =>
            Task.FromResult<SupplierQuoteAnchor?>(new SupplierQuoteAnchor(31, 41, 51, 91,
                "NX-2026-0001", new Dictionary<long, long> { [71] = 81 }));

        public Task<SupplierQuoteRevision?> FindRevisionByIdempotencyKeyAsync(long businessUnitId,
            string idempotencyKey, CancellationToken cancellationToken) =>
            Task.FromResult<SupplierQuoteRevision?>(null);

        public Task<SupplierQuote?> FindCanonicalAsync(long businessUnitId, long supplierId,
            string supplierQuoteReference, CancellationToken cancellationToken) =>
            Task.FromResult<SupplierQuote?>(null);

        public Task<SupplierQuoteCaptureResult> PersistRevisionAsync(SupplierQuote quote,
            SupplierQuoteRevision revision, bool isNewQuote, CancellationToken cancellationToken)
        {
            Revisions.Add(revision);
            return Task.FromResult(new SupplierQuoteCaptureResult(1, 2, revision.RevisionNumber,
                quote.InboxStatus, 0, false));
        }

        public Task<IReadOnlyCollection<SupplierQuoteInboxRow>> SearchInboxAsync(long businessUnitId,
            string? status, int limit, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<SupplierQuoteDetail?> GetDetailAsync(long businessUnitId, long supplierQuoteId,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task ReviewAsync(ReviewSupplierQuoteFieldCommand command,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
