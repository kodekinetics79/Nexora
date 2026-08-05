using ERP_RFQ_Automation.DTOs.DocumentIntelligence;
using ERP_RFQ_Automation.Extraction;
using ERP_RFQ_Automation.Services.DocumentIntelligence;
using ERP_RFQ_Automation.Services.Interfaces;
using ERP_RFQ_Automation.Tests.Support;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// The multi-inquiry splitter's contract is "never guess-split": one document becomes N
/// leads ONLY when the grouping evidence is complete and confident; anything ambiguous
/// keeps today's single-lead behavior. These tests drive the pure decision rules and the
/// ChunkedExtractionService integration (LLM + structured paths).
/// </summary>
public class MultiInquirySplitterTests
{
    private static LeadItemData Grouped(string? group, double conf = 0.9, string name = "Item")
        => Ext.Item(0.9, name) with { InquiryGroup = group, InquiryGroupConfidence = conf };

    private static LeadExtractionResult Merged(params LeadItemData[] items)
        => Ext.Result(items.ToList(), 0.9);

    // ---- LLM path: TrySplitByItemGroups ---------------------------------

    [Fact]
    public void TwoCleanGroups_SplitsIntoTwoResults_WithConservation()
    {
        var merged = Merged(
            Grouped("RFQ-A"), Grouped("RFQ-A"),
            Grouped("RFQ-B"), Grouped("RFQ-B"), Grouped("RFQ-B"));

        var split = MultiInquirySplitter.TrySplitByItemGroups(merged);

        Assert.NotNull(split);
        Assert.Equal(2, split!.Count);
        // Per-group conservation: the groups are a strict partition of the items.
        Assert.Equal(2, split[0].Items.Count);
        Assert.Equal(3, split[1].Items.Count);
        Assert.Equal(merged.Items.Count, split.Sum(g => g.Items.Count));
        // Each group carries its own inquiry identifier.
        Assert.Equal("RFQ-A", split[0].Rfqno);
        Assert.Equal("RFQ-B", split[1].Rfqno);
    }

    [Fact]
    public void UnlabeledItem_MakesGroupingAmbiguous_NoSplit()
    {
        var merged = Merged(Grouped("RFQ-A"), Grouped("RFQ-B"), Grouped(null));
        Assert.Null(MultiInquirySplitter.TrySplitByItemGroups(merged));
    }

    [Fact]
    public void LowGroupingConfidence_NoSplit()
    {
        var merged = Merged(Grouped("RFQ-A", 0.4), Grouped("RFQ-B", 0.4));
        Assert.Null(MultiInquirySplitter.TrySplitByItemGroups(merged));
    }

    [Fact]
    public void SingleGroup_IsASingleInquiry_NoSplit()
    {
        var merged = Merged(Grouped("RFQ-A"), Grouped("RFQ-A"));
        Assert.Null(MultiInquirySplitter.TrySplitByItemGroups(merged));
    }

    [Fact]
    public void MoreGroupsThanCap_IsAnExtractionArtifact_NoSplit()
    {
        var items = Enumerable.Range(0, MultiInquirySplitter.MaxGroups + 1)
            .Select(i => Grouped($"RFQ-{i}"))
            .ToArray();
        Assert.Null(MultiInquirySplitter.TrySplitByItemGroups(Merged(items)));
    }

    [Fact]
    public void LabelsMergeCaseInsensitively_AndTrimmed()
    {
        var merged = Merged(Grouped("rfq-a "), Grouped("RFQ-A"), Grouped("RFQ-B"));
        var split = MultiInquirySplitter.TrySplitByItemGroups(merged);

        Assert.NotNull(split);
        Assert.Equal(2, split!.Count);
        Assert.Equal(2, split[0].Items.Count); // "rfq-a" + "RFQ-A" are one inquiry
    }

    [Fact]
    public void SplitGroups_InheritHeader_AndRecomputeConfidence()
    {
        var merged = Ext.Result(new List<LeadItemData>
        {
            Grouped("RFQ-A"), Grouped("RFQ-B")
        }, 0.9) with
        { InquiryType = "service", BuyersName = "Acme" };

        var split = MultiInquirySplitter.TrySplitByItemGroups(merged);

        Assert.NotNull(split);
        Assert.All(split!, g =>
        {
            Assert.Equal("Acme", g.BuyersName);           // shared header inherited
            Assert.Equal("service", g.InquiryType);       // doc-level classification inherited
            Assert.NotNull(g.OverallConfidence);
            Assert.InRange(g.OverallConfidence!.Value, 0, 1);
        });
    }

    // ---- Structured path: ShouldSplitStructured -------------------------

    private static IReadOnlyList<CanonicalRfqDocument> Normalize(params RfqSpreadsheetRow[] rows)
        => new CanonicalRfqNormalizer().NormalizeSpreadsheetRows(rows, businessUnitId: 1).Documents;

    private static RfqSpreadsheetRow Row(int n, string? rfq, string? buyer, string product = "Widget")
        => new()
        {
            RowNumber = n,
            SourceDocumentName = "sheet.xlsx",
            RfqNo = rfq,
            BuyerName = buyer,
            ReceivedDate = "2026-07-14",
            BidClosingDate = "2026-07-30",
            ProductName = product,
            Quantity = "5",
            UnitPrice = "10.00",
            Currency = "USD",
            ManufacturerName = "Maker",
            ManufacturerPartNumber = "MPN-1",
            LeadTimeDays = "10"
        };

    [Fact]
    public void TwoIdentifiedRfqGroups_ShouldSplit()
    {
        var docs = Normalize(Row(2, "RFQ-1", "Acme"), Row(3, "RFQ-2", "Acme"));
        Assert.Equal(2, docs.Count);
        Assert.True(MultiInquirySplitter.ShouldSplitStructured(docs));
    }

    [Fact]
    public void SingleGroup_ShouldNotSplit()
    {
        var docs = Normalize(Row(2, "RFQ-1", "Acme"), Row(3, "RFQ-1", "Acme"));
        Assert.False(MultiInquirySplitter.ShouldSplitStructured(docs));
    }

    [Fact]
    public void GroupWithoutRfqOrBuyer_IsAmbiguousFragment_ShouldNotSplit()
    {
        // Row 4 has neither RFQ number nor buyer -> its "group" is a bare row-number
        // fallback; splitting would fabricate an inquiry out of an unowned fragment.
        var docs = Normalize(Row(2, "RFQ-1", "Acme"), Row(3, "RFQ-2", "Acme"), Row(4, null, null));
        Assert.True(docs.Count >= 3);
        Assert.False(MultiInquirySplitter.ShouldSplitStructured(docs));
    }

    // ---- ChunkedExtractionService integration ---------------------------

    private static ChunkedExtractionService NewService(StubLlm llm)
        => new(llm, new CanonicalRfqNormalizer(), new NoopLogger<ChunkedExtractionService>());

    private static DocumentExtractionInput Doc(int rows)
        => new()
        {
            BusinessUnitId = 1,
            HeaderText = "buyer: Acme",
            LineItemRegions = Enumerable.Range(0, rows).Select(i => $"row {i}").ToList()
        };

    [Fact]
    public async Task Unstructured_CleanExtractionWithGroups_ProducesSplitResults()
    {
        var items = new List<LeadItemData> { Grouped("RFQ-A"), Grouped("RFQ-A"), Grouped("RFQ-B") };
        var llm = new StubLlm(Ext.Result(items, 0.9));

        var outcome = await NewService(llm).ExtractUnstructuredAsync(Doc(3));

        Assert.Equal(ExtractionOutcomeStatus.Ok, outcome.Status);
        Assert.NotNull(outcome.SplitResults);
        Assert.Equal(2, outcome.SplitResults!.Count);
        Assert.Equal(3, outcome.SplitResults.Sum(g => g.Items.Count)); // conservation
        Assert.NotNull(outcome.Result); // merged view stays populated
    }

    [Fact]
    public async Task Unstructured_NeedsReview_NeverGuessSplits()
    {
        // A TRUE review signal — incomplete OCR, content is known to be missing — must keep
        // the document one flagged lead: grouped labels must NOT split it. (Fewer items
        // than parsed text LINES is deliberately no longer such a signal: lines are not
        // items, and that false "Item count mismatch" used to force NeedsReview on every
        // unstructured document, which silently disabled this splitter entirely.)
        var items = new List<LeadItemData> { Grouped("RFQ-A"), Grouped("RFQ-B") };
        var llm = new StubLlm(Ext.Result(items, 0.9));
        var input = new DocumentExtractionInput
        {
            BusinessUnitId = 1,
            HeaderText = "buyer: Acme",
            LineItemRegions = Enumerable.Range(0, 3).Select(i => $"row {i}").ToList(),
            OcrStatus = ExtractionOcrStatus.Partial,
            OcrTruncated = true
        };

        var outcome = await NewService(llm).ExtractUnstructuredAsync(input);

        Assert.Equal(ExtractionOutcomeStatus.NeedsReview, outcome.Status);
        Assert.Contains("OCR was incomplete", outcome.ReviewReason);
        Assert.Null(outcome.SplitResults);
    }

    [Fact]
    public async Task Structured_TwoValidRfqGroups_SplitsWithPerGroupHeaders()
    {
        var llm = new StubLlm(); // structured path must not touch the LLM
        var input = new DocumentExtractionInput
        {
            BusinessUnitId = 1,
            SourceDocumentName = "multi.xlsx",
            IsStructured = true,
            StructuredRows = new[] { Row(2, "RFQ-1", "Acme"), Row(3, "RFQ-2", "Globex") }
        };

        var outcome = await NewService(llm).ExtractAsync(input);

        Assert.Equal(0, llm.CallCount);
        Assert.Equal(ExtractionOutcomeStatus.Ok, outcome.Status);
        Assert.NotNull(outcome.SplitResults);
        Assert.Equal(2, outcome.SplitResults!.Count);
        Assert.Equal(new[] { "RFQ-1", "RFQ-2" }, outcome.SplitResults.Select(r => r.Rfqno).ToArray());
        Assert.All(outcome.SplitResults, r => Assert.Single(r.Items));
        Assert.Equal(2, outcome.ExtractedItemCount);
    }

    [Fact]
    public async Task Structured_AmbiguousGroups_KeepsSingleLeadNeedsReview()
    {
        var llm = new StubLlm();
        var input = new DocumentExtractionInput
        {
            BusinessUnitId = 1,
            SourceDocumentName = "multi.xlsx",
            IsStructured = true,
            // Third row has no RFQ/buyer identity -> ambiguous fragment -> no split.
            StructuredRows = new[] { Row(2, "RFQ-1", "Acme"), Row(3, "RFQ-2", "Globex"), Row(4, null, null) }
        };

        var outcome = await NewService(llm).ExtractAsync(input);

        Assert.Equal(ExtractionOutcomeStatus.NeedsReview, outcome.Status);
        Assert.Null(outcome.SplitResults);
        Assert.Contains("distinct RFQ groups", outcome.ReviewReason);
    }
}
