using ERP_RFQ_Automation.Services.DocumentIntelligence;

namespace ERP_RFQ_Automation.Tests.DocumentIntelligence;

/// <summary>
/// The floor that keeps non-bids out of Leads, decided from cell content rather than headers.
/// Every fixture here is written as the RENDERED sheet text the spreadsheet readers produce, so
/// these cases hold for xlsx, xls and csv without three sets of fixtures.
/// </summary>
public class SpreadsheetBidEvidenceTests
{
    private static string Sheet(params string[] rows) => "[Worksheet: Sheet1]\n" + string.Join("\n", rows) + "\n";

    [Fact]
    public void AMaterialCrossReference_IsNotQuotable()
    {
        // THE PRODUCTION DOCUMENT, to shape and scale. Four columns, 2,241 rows: the seller's item
        // number, the buyer's item number, a group description and a group code. Every column is
        // populated and the sheet reads perfectly — there is simply nothing in it to quote.
        //
        // Note what makes this hard: THREE of the four columns are numeric. A naive "does a
        // numeric column exist?" floor would call this a bid. The ten-digit identifiers and the
        // five-digit group codes are separated from a quantity by magnitude, not by type.
        var rows = new List<string> { "ASMO Item#\tARAMCO Item #\tMSG Descreption\tMSG #" };
        for (var i = 0; i < 40; i++)
            rows.Add($"200000{8634 + i}\t100072{1728 + i}\tSpares, UPS (061100)\t61100");

        var evidence = SpreadsheetBidEvidence.Assess(Sheet(rows.ToArray()));

        Assert.True(evidence.ShouldRefuseAsNonBid);
        Assert.False(evidence.HasQuantityShapedColumn);
        Assert.False(evidence.HasPriceShapedColumn);
        Assert.False(evidence.HasDateColumn);
        Assert.False(evidence.HasUnitColumn);
    }

    [Fact]
    public void AnOrdinaryEnquiryWithQuantities_IsQuotable()
    {
        var evidence = SpreadsheetBidEvidence.Assess(Sheet(
            "Item\tDescription\tQty",
            "902017274\tKEY:SHAFT,SQUARE,10 MM\t176",
            "902017275\tVALVE,GATE,2 IN\t4",
            "902017276\tGASKET,SPIRAL WOUND\t12"));

        Assert.False(evidence.ShouldRefuseAsNonBid);
        Assert.True(evidence.HasQuantityShapedColumn);
    }

    [Fact]
    public void HeadersInAnotherLanguage_StillReadAsQuotable()
    {
        // The point of reading content rather than headers: no alias list will ever contain
        // these spellings, and it does not matter.
        var evidence = SpreadsheetBidEvidence.Assess(Sheet(
            "الصنف\tالوصف\tالكمية",
            "902017274\tصمام\t176",
            "902017275\tحشية\t4",
            "902017276\tمسمار\t12"));

        Assert.False(evidence.ShouldRefuseAsNonBid);
        Assert.True(evidence.HasQuantityShapedColumn);
    }

    [Fact]
    public void NoHeaderRowAtAll_StillReadsAsQuotable()
    {
        var evidence = SpreadsheetBidEvidence.Assess(Sheet(
            "902017274\tKEY:SHAFT,SQUARE\t176\tEA",
            "902017275\tVALVE,GATE,2 IN\t4\tEA",
            "902017276\tGASKET,SPIRAL WOUND\t12\tEA"));

        Assert.False(evidence.ShouldRefuseAsNonBid);
    }

    [Fact]
    public void ABulkEnquiryWithHugeQuantities_IsStillQuotable()
    {
        // FALSE-REJECTION GUARD, and the reason four signals are required rather than one.
        // Cable ordered by the metre defeats the quantity magnitude test on its own — the median
        // is far above any plausible count — but the unit column carries the document through.
        // Wrongly rejecting a real enquiry costs an order; this is the case that would do it.
        var evidence = SpreadsheetBidEvidence.Assess(Sheet(
            "Material\tDescription\tQuantity\tUnit",
            "902017274\tCABLE,XLPE,11KV\t250000\tM",
            "902017275\tCABLE,XLPE,33KV\t180000\tM",
            "902017276\tCABLE,LV,4C\t420000\tM"));

        Assert.False(evidence.ShouldRefuseAsNonBid);
        Assert.True(evidence.HasUnitColumn);
    }

    [Fact]
    public void AnEnquiryStatingOnlyADeliveryDate_IsStillQuotable()
    {
        var evidence = SpreadsheetBidEvidence.Assess(Sheet(
            "Material\tDescription\tRequired By",
            "902017274\tKEY:SHAFT,SQUARE\t2026-03-01",
            "902017275\tVALVE,GATE,2 IN\t2026-03-14",
            "902017276\tGASKET,SPIRAL WOUND\t2026-04-02"));

        Assert.False(evidence.ShouldRefuseAsNonBid);
        Assert.True(evidence.HasDateColumn);
    }

    [Fact]
    public void APriceListWithoutQuantities_IsQuotable()
    {
        // A price list is not an enquiry either, but it is NOT this check's job to say so: money
        // on the page is commercial shape, and the floor only refuses documents with no
        // commercial shape at all. Narrower judgements belong downstream, with more context.
        var evidence = SpreadsheetBidEvidence.Assess(Sheet(
            "Part\tDescription\tUnit Price",
            "902017274\tKEY:SHAFT,SQUARE\t12.50",
            "902017275\tVALVE,GATE,2 IN\t340.00",
            "902017276\tGASKET,SPIRAL WOUND\t8.75"));

        Assert.False(evidence.ShouldRefuseAsNonBid);
        Assert.True(evidence.HasPriceShapedColumn);
    }

    [Fact]
    public void ASheetOfIdentifiersAlone_IsNotQuotable()
    {
        var rows = new List<string> { "Old Code\tNew Code" };
        for (var i = 0; i < 10; i++)
            rows.Add($"200000{8634 + i}\t100072{1728 + i}");

        var evidence = SpreadsheetBidEvidence.Assess(Sheet(rows.ToArray()));

        Assert.True(evidence.ShouldRefuseAsNonBid);
    }

    [Fact]
    public void AnEmptySheet_IsNotRefusedHere()
        // Nothing to judge, so this floor says nothing. An empty workbook is already a permanent
        // parse failure upstream ("contains no cell content"), and two components claiming the
        // same refusal for different reasons is how a document ends up with the wrong one.
        => Assert.False(SpreadsheetBidEvidence.Assess(string.Empty).ShouldRefuseAsNonBid);

    [Fact]
    public void AShortEnquiryWithTooFewRowsToJudge_IsNeverRefused()
    {
        // THE FALSE-REJECTION THIS GUARD EXISTS FOR. A genuine two-line enquiry cannot support a
        // column profile — one value per column proves nothing — and refusing it as a non-bid
        // would throw away real business on no evidence. It costs a single chunk to read
        // properly, so there is nothing to save by guessing.
        var evidence = SpreadsheetBidEvidence.Assess(Sheet(
            "Section\tNarrative\tOwner",
            "MAT-88001\tBall valve DN50 PN16 stainless\tJubail Plant"));

        Assert.False(evidence.ShouldRefuseAsNonBid);
        Assert.False(evidence.HasEnoughRowsToJudge);
    }
}
