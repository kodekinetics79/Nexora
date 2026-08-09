using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.OrderToCash;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// FR-COM-02. "Match PO lines to the originating RFQ and quotation using item code, manufacturer
/// and part number." The rule is deterministic, so it is asserted directly rather than through a
/// tolerance: a given pair of documents always yields the same proposal, the same key and the same
/// confidence, and an exception is always an exception.
/// </summary>
public sealed class CustomerPurchaseOrderLineMatcherTests
{
    private const long QuoteLineOne = 1001;
    private const long QuoteLineTwo = 1002;

    [Fact]
    public void ExactPartNumber_ProposesTheOneQuoteLineThatCarriesIt()
    {
        var quoteLines = new[]
        {
            QuoteLine(QuoteLineOne, "Ball valve", "MAT-100", "Emerson", "E-VLV-1"),
            QuoteLine(QuoteLineTwo, "Gate valve", "MAT-200", "Emerson", "E-VLV-2"),
        };

        // The buyer punctuates and cases their part number differently; normalisation absorbs it.
        var proposal = Assert.Single(CustomerPurchaseOrderLineMatcher.Propose(
            [PoLine("10", "Whatever the buyer calls it", null, null, "e vlv/2")], quoteLines));

        Assert.Equal(QuoteLineMatchStatuses.Proposed, proposal.Status);
        Assert.Equal(QuoteLineTwo, proposal.ProposedQuoteItemId);
        Assert.Equal(QuoteLineMatchKeys.ManufacturerPartNumber, proposal.MatchedKey);
        Assert.Equal(QuoteLineMatchConfidence.Exact, proposal.Confidence);
        Assert.Contains("part number", proposal.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ManufacturerAndItemCode_MatchWhenThePurchaseOrderCarriesNoPartNumber()
    {
        var quoteLines = new[]
        {
            QuoteLine(QuoteLineOne, "Ball valve", "MAT-100", "Emerson", "E-VLV-1"),
            // Same item code, different manufacturer: only the pair of keys separates the two.
            QuoteLine(QuoteLineTwo, "Ball valve", "MAT-100", "Velan", "V-VLV-9"),
        };

        var proposal = Assert.Single(CustomerPurchaseOrderLineMatcher.Propose(
            [PoLine("10", "Ball valve", "mat 100", "velan", null)], quoteLines));

        Assert.Equal(QuoteLineMatchStatuses.Proposed, proposal.Status);
        Assert.Equal(QuoteLineTwo, proposal.ProposedQuoteItemId);
        Assert.Equal(QuoteLineMatchKeys.ManufacturerAndItemCode, proposal.MatchedKey);
        Assert.Equal(QuoteLineMatchConfidence.High, proposal.Confidence);
    }

    [Fact]
    public void ItemCodeAlone_MatchesOnlyWhenNoManufacturerContradictsIt()
    {
        var quoteLines = new[] { QuoteLine(QuoteLineOne, "Ball valve", "MAT-100", "Emerson", null) };

        var agreeing = Assert.Single(CustomerPurchaseOrderLineMatcher.Propose(
            [PoLine("10", "Ball valve", "MAT-100", null, null)], quoteLines));
        var contradicting = Assert.Single(CustomerPurchaseOrderLineMatcher.Propose(
            [PoLine("10", "Ball valve", "MAT-100", "Velan", null)], quoteLines));

        Assert.Equal(QuoteLineMatchStatuses.Proposed, agreeing.Status);
        Assert.Equal(QuoteLineMatchKeys.ItemCode, agreeing.MatchedKey);
        Assert.Equal(QuoteLineMatchConfidence.Medium, agreeing.Confidence);
        Assert.Equal(QuoteLineMatchStatuses.ReviewRequired, contradicting.Status);
        Assert.Null(contradicting.ProposedQuoteItemId);
    }

    [Fact]
    public void AmbiguousLine_ProposesNothingAndSurfacesEveryTiedCandidate()
    {
        var quoteLines = new[]
        {
            QuoteLine(QuoteLineOne, "Ball valve 2in", "MAT-100", "Emerson", "E-VLV-1"),
            QuoteLine(QuoteLineTwo, "Ball valve 3in", "MAT-200", "Emerson", "E-VLV-1"),
        };

        var proposal = Assert.Single(CustomerPurchaseOrderLineMatcher.Propose(
            [PoLine("10", "Ball valve", null, "Emerson", "E-VLV-1")], quoteLines));

        Assert.Equal(QuoteLineMatchStatuses.Ambiguous, proposal.Status);
        Assert.Null(proposal.ProposedQuoteItemId);
        Assert.Equal(QuoteLineMatchConfidence.None, proposal.Confidence);
        Assert.Equal(new[] { QuoteLineOne, QuoteLineTwo }, proposal.Candidates.Select(x => x.QuoteItemId).ToArray());
        Assert.All(proposal.Candidates, candidate =>
            Assert.Equal(QuoteLineMatchKeys.ManufacturerPartNumber, candidate.MatchedKey));
    }

    [Fact]
    public void NoKeyMatches_ProposesNothingAndStillOffersTheQuoteLinesForReview()
    {
        var quoteLines = new[] { QuoteLine(QuoteLineOne, "Ball valve", "MAT-100", "Emerson", "E-VLV-1") };

        var unrelated = Assert.Single(CustomerPurchaseOrderLineMatcher.Propose(
            [PoLine("10", "Hydraulic hose", "MAT-999", "Parker", "P-HOSE-3")], quoteLines));
        var silent = Assert.Single(CustomerPurchaseOrderLineMatcher.Propose(
            [PoLine("10", "Hydraulic hose", null, null, null)], quoteLines));

        Assert.Equal(QuoteLineMatchStatuses.ReviewRequired, unrelated.Status);
        Assert.Null(unrelated.ProposedQuoteItemId);
        Assert.Equal(QuoteLineOne, Assert.Single(unrelated.Candidates).QuoteItemId);
        Assert.Equal(QuoteLineMatchStatuses.ReviewRequired, silent.Status);
        Assert.Contains("no item code, manufacturer or part number", silent.Reason,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void StrongerKeyWins_AndAWeakerKeyNeverOverrulesIt()
    {
        var quoteLines = new[]
        {
            // Carries the buyer's part number but a different item code.
            QuoteLine(QuoteLineOne, "Ball valve", "MAT-999", "Emerson", "E-VLV-1"),
            // Carries the buyer's item code but no part number.
            QuoteLine(QuoteLineTwo, "Ball valve", "MAT-100", "Emerson", null),
        };

        var proposal = Assert.Single(CustomerPurchaseOrderLineMatcher.Propose(
            [PoLine("10", "Ball valve", "MAT-100", "Emerson", "E-VLV-1")], quoteLines));

        Assert.Equal(QuoteLineOne, proposal.ProposedQuoteItemId);
        Assert.Equal(QuoteLineMatchKeys.ManufacturerPartNumber, proposal.MatchedKey);
    }

    [Fact]
    public void OneQuoteLineProposedForTwoPurchaseOrderLines_TellsTheReviewerAboutTheSplit()
    {
        var quoteLines = new[] { QuoteLine(QuoteLineOne, "Ball valve", "MAT-100", "Emerson", "E-VLV-1") };

        var proposals = CustomerPurchaseOrderLineMatcher.Propose(
            [PoLine("10", "Ball valve", null, null, "E-VLV-1"), PoLine("20", "Ball valve", null, null, "E-VLV-1")],
            quoteLines);

        Assert.All(proposals, proposal =>
        {
            Assert.Equal(QuoteLineMatchStatuses.Proposed, proposal.Status);
            Assert.Equal(QuoteLineOne, proposal.ProposedQuoteItemId);
            Assert.Contains("more than one PO line", proposal.Reason, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public async Task ProposeQuoteLineMatches_UsesTheStoredRfqKeysAndNeverLeavesTheTenant()
    {
        using var fixture = new CustomerAwardTestFixture(twoQuoteLines: true);
        var (first, second) = await SeedIdentityKeysAsync(fixture);

        var proposal = await fixture.Service.ProposeQuoteLineMatchesAsync(fixture.BusinessUnitId,
            new(fixture.QuoteId, 0, [new("10", "Buyer wording", null, null, "mfr/two")]));
        var line = Assert.Single(proposal.Lines);

        Assert.Equal(second, line.ProposedQuoteItemId);
        Assert.Equal(QuoteLineMatchStatuses.Proposed, line.Status);
        Assert.Equal(1, proposal.ProposedCount);
        Assert.NotEqual(first, line.ProposedQuoteItemId);

        // Another tenant asking for the same quotation must not see it at all.
        using var otherContext = fixture.Database.ContextFor(fixture.BusinessUnitId + 1);
        var otherService = new CustomerAwardApplicationService(otherContext);
        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            otherService.ProposeQuoteLineMatchesAsync(fixture.BusinessUnitId + 1,
                new(fixture.QuoteId, 0, [new("10", "Buyer wording", null, null, "mfr/two")])));
    }

    [Fact]
    public async Task ProposeQuoteLineMatches_RefusesAnotherCustomersQuotation()
    {
        using var fixture = new CustomerAwardTestFixture();
        await SeedIdentityKeysAsync(fixture);
        var quoteCustomerId = await fixture.Context.Quotes.Where(x => x.Id == fixture.QuoteId)
            .Select(x => x.CustomerId).SingleAsync();

        var error = await Assert.ThrowsAsync<ArgumentException>(() =>
            fixture.Service.ProposeQuoteLineMatchesAsync(fixture.BusinessUnitId,
                new(fixture.QuoteId, quoteCustomerId!.Value + 1,
                    [new("10", "Buyer wording", null, null, "MFR-ONE")])));

        Assert.Contains("different customer", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ProposePurchaseOrderMatches_ReadsTheKeysCapturedFromTheBuyersDocument()
    {
        using var fixture = new CustomerAwardTestFixture(twoQuoteLines: true);
        var (_, second) = await SeedIdentityKeysAsync(fixture);
        var command = fixture.PurchaseOrderCommand("PO-KEYS", 4m) with
        {
            Lines = [fixture.PurchaseOrderCommand("unused", 4m).Lines.Single() with
            {
                ProductId = null,
                ManufacturerPartNumber = "MFR-TWO",
                ManufacturerName = "Fixture Manufacturing",
                CustomerItemCode = "CODE-2"
            }]
        };
        var purchaseOrder = await fixture.Service.CreatePurchaseOrderAsync(fixture.BusinessUnitId,
            "po-keys", "corr-po-keys", command, "tests");

        fixture.Context.ChangeTracker.Clear();
        var proposal = await fixture.Service.ProposePurchaseOrderMatchesAsync(fixture.BusinessUnitId,
            purchaseOrder.Id, fixture.QuoteId);
        var line = Assert.Single(proposal.Lines);

        Assert.Equal("MFR-TWO", purchaseOrder.Lines.Single().ManufacturerPartNumber);
        Assert.Equal(purchaseOrder.Lines.Single().Id, line.CustomerPurchaseOrderLineId);
        Assert.Equal(second, line.ProposedQuoteItemId);
        Assert.Equal(QuoteLineMatchKeys.ManufacturerPartNumber, line.MatchedKey);
    }

    /// <summary>
    /// FR-COM-04. The regression guard for the capture path: a customer PO line's price, quantity
    /// and part identity must be whatever the buyer sent, never a copy of our own quote line. When
    /// they are copied the discrepancy engine compares the system against itself and can only ever
    /// report agreement, so this asserts that a genuinely different buyer document is preserved
    /// verbatim and is reported as the discrepancy it is.
    /// </summary>
    [Fact]
    public async Task BuyerLineValues_ArePersistedVerbatimAndReportedAsDiscrepancies()
    {
        using var fixture = new CustomerAwardTestFixture();
        await SeedIdentityKeysAsync(fixture);
        var quoteLine = await fixture.Context.QuoteItems.AsNoTracking()
            .SingleAsync(x => x.Id == fixture.QuoteItemId);
        var command = fixture.PurchaseOrderCommand("PO-BUYER-VALUES", 4m) with
        {
            Lines = [fixture.PurchaseOrderCommand("unused", 4m).Lines.Single() with
            {
                ProductId = null,
                Description = "Buyer's own wording for the part",
                UnitPrice = 87.25m,
                LineAmount = 349m,
                ManufacturerPartNumber = "SOMETHING-ELSE",
                CustomerItemCode = "BUYER-CODE-1"
            }]
        };

        var purchaseOrder = await fixture.Service.CreatePurchaseOrderAsync(fixture.BusinessUnitId,
            "po-buyer-values", "corr-po-buyer-values", command, "tests");
        var award = await fixture.Service.CreateAwardAsync(fixture.BusinessUnitId, "award-buyer-values",
            "corr-award-buyer-values", new(purchaseOrder.Id, fixture.QuoteId, 0, purchaseOrder.Version, 1,
                [new(purchaseOrder.Lines.Single().Id, fixture.QuoteItemId, 4m)]), "tests");
        await fixture.Service.ConfirmAwardAsync(fixture.BusinessUnitId, award.Id, "confirm-buyer-values",
            "corr-confirm-buyer-values", new(award.Version), "tests");

        fixture.Context.ChangeTracker.Clear();
        var stored = await fixture.Context.CustomerPurchaseOrderLines.AsNoTracking()
            .SingleAsync(x => x.CustomerPurchaseOrderId == purchaseOrder.Id);
        var match = await fixture.Service.GetPurchaseOrderMatchAsync(fixture.BusinessUnitId, purchaseOrder.Id);
        var line = Assert.Single(match.Lines);

        Assert.NotEqual(quoteLine.UnitPrice, stored.UnitPrice!.Value);
        Assert.Equal(87.25m, stored.UnitPrice);
        Assert.Equal("Buyer's own wording for the part", stored.Description);
        Assert.Equal("SOMETHING-ELSE", stored.ManufacturerPartNumber);
        Assert.Equal("BUYER-CODE-1", stored.CustomerItemCode);
        Assert.Contains("PRICE_DISCREPANCY", line.Differences);
        // Reachable only because the part identity is the buyer's rather than a copy of ours.
        Assert.Contains("PART_DISCREPANCY", line.Differences);
        Assert.Equal("DISCREPANCY", line.MatchStatus);
        Assert.Equal("SOMETHING-ELSE", line.ManufacturerPartNumber);
    }

    /// <summary>
    /// Gives the fixture's quote lines the FR-COM-02 keys by hanging RFQ lines off them, which is
    /// where the quotation's own item code, manufacturer and part number live.
    /// </summary>
    private static async Task<(long First, long Second)> SeedIdentityKeysAsync(CustomerAwardTestFixture fixture)
    {
        var rfqId = await fixture.Context.Rfqs.Select(x => x.Id).SingleAsync();
        var quote = await fixture.Context.Quotes.Include(x => x.QuoteItems)
            .SingleAsync(x => x.Id == fixture.QuoteId);
        var ordered = quote.QuoteItems.OrderBy(x => x.Id).ToList();
        var suffixes = new[] { "ONE", "TWO", "THREE" };
        var nextRfqItemId = 890_100L;

        for (var index = 0; index < ordered.Count; index++)
        {
            var rfqItem = new Rfqitem
            {
                Id = nextRfqItemId++,
                Rfqid = rfqId,
                Quantity = (int)ordered[index].Quantity,
                ItemMaterialCode = $"CODE-{index + 1}",
                ManufacturerName = "Fixture Manufacturing",
                ManufacturerPartNumber = $"MFR-{suffixes[index]}",
                CreatedBy = "tests",
                CreatedDate = DateTime.UtcNow
            };
            fixture.Context.Rfqitems.Add(rfqItem);
            ordered[index].RfqitemId = rfqItem.Id;
        }

        await fixture.Context.SaveChangesAsync();
        fixture.Context.ChangeTracker.Clear();
        return (ordered[0].Id, ordered.Count > 1 ? ordered[1].Id : ordered[0].Id);
    }

    private static PurchaseOrderLineKeys PoLine(string reference, string? description, string? itemCode,
        string? manufacturer, string? partNumber)
        => new(reference, description, itemCode, manufacturer, partNumber);

    private static QuoteLineKeys QuoteLine(long id, string description, string? itemCode,
        string? manufacturer, string? partNumber)
        => new(id, description, itemCode, manufacturer, partNumber, [], 10m, 10m, 100m);
}
