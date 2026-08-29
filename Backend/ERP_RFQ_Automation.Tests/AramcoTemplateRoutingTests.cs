using ERP_RFQ_Automation.Extraction;
using ERP_RFQ_Automation.Extraction.Templates;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// The template is tried BEFORE the model, and a refusal routes rather than fails.
///
/// <para>This is where the cost saving actually lands. The parser existing changes nothing on
/// its own — an Aramco bid list only stops costing money when the dispatch consults it before
/// planning chunks.</para>
/// </summary>
public sealed class AramcoTemplateRoutingTests
{
    private const string Preamble = """
        MATERIALS E-BIDDING SYSTEM
        Bid Materials List (Low Value Bid)
        2/16/2021 7:31:24 AM
        Vendor Code
        Vendname
        Bidno
        Bid Date
        Bid Close
        2004414
        ALI ZAID AL-QURAISHI&PARTNERS EL
        C001046933
        2/16/2021
        2/28/2021
        Address
        Buyer
        Buyer Tel
        Saudi Arabia
        1G5-Fawzi Alomari
        011-8078850-
        Bid Line
        Item No
        Ship To
        Req Unit
        Req Qty
        Resp Qty
        For Foreign Suppliers, If the delivery type is CIF or DDP, Supplier must attach.
        """;

    [Fact]
    public void An_Aramco_bid_list_is_extracted_with_no_model_call()
    {
        var text = Preamble + "\n" + string.Join("\n",
            "10", "902017274", "3801", "EA", "176", "KEY:SHAFT,SQUARE", "SHAPE:", "SQUARE;",
            "20", "902017276", "3801", "EA", "89", "SEAL,4 MM DIA");

        var outcome = AramcoBidListExtraction.TryExtract(text, "bid.doc", out var rejection);

        Assert.NotNull(outcome);
        Assert.Null(rejection);
        Assert.Equal(ExtractionOutcomeStatus.Ok, outcome!.Status);

        // THE POINT: no provider was consulted, so the ledger must not name one.
        Assert.Null(outcome.AiProviderClass);
        Assert.Equal(ExtractionProcessingPath.DeterministicRules, outcome.ProcessingPath);

        // Nothing is lost by reading it ourselves.
        Assert.Equal(2, outcome.ExtractedItemCount);
        Assert.Equal(outcome.ExpectedItemCount, outcome.ExtractedItemCount);

        var first = outcome.Result!.Items[0];
        Assert.Equal("902017274", first.ItemMaterialCode);
        Assert.Equal("10", first.LineItemNo);
        Assert.Equal(176, first.Quantity);
        Assert.Equal("EA", first.UnitOfMeasure);
        Assert.Equal("3801", first.StorageLocation);
        Assert.Contains("SQUARE;", first.ProductShortDescription);

        // The customer's own bid number becomes the RFQ reference — read, never invented.
        Assert.Equal("C001046933", outcome.Result.Rfqno);
        Assert.Equal("2021-02-28", outcome.Result.BidClosingDate);
    }

    [Fact]
    public void A_document_that_is_not_an_Aramco_bid_list_routes_to_the_model_silently()
    {
        var outcome = AramcoBidListExtraction.TryExtract(
            "Please quote 5 EA of ABC-123.", "email_body.txt", out var rejection);

        Assert.Null(outcome);
        // Not a refusal — it was simply never ours. Nothing to warn about.
        Assert.Null(rejection);
    }

    [Fact]
    public void An_Aramco_document_the_template_cannot_read_routes_to_the_model_LOUDLY()
    {
        // A layout change at the sender, or a defect here. Either is worth knowing about
        // before the bill arrives, so the rejection is surfaced rather than swallowed.
        var text = Preamble + "\n" + string.Join("\n",
            "10", "902017274", "3801", "176", "EA", "KEY:SHAFT");   // unit and quantity swapped

        var outcome = AramcoBidListExtraction.TryExtract(text, "bid.doc", out var rejection);

        Assert.Null(outcome);
        Assert.NotNull(rejection);
        Assert.Contains("neither a unit of measure nor a plant code", rejection);
    }

    [Fact]
    public void A_fractional_quantity_is_preserved_exactly()
    {
        // Truncating 2.5 to 2 would under-quote by 20%; routing it to review would also lose
        // a value the deterministic parser read exactly.
        var text = Preamble + "\n" + string.Join("\n",
            "10", "902017274", "3801", "M", "2.5", "CABLE,ELEC");

        var outcome = AramcoBidListExtraction.TryExtract(text, "bid.doc", out _);

        Assert.NotNull(outcome);
        var line = Assert.Single(outcome!.Result!.Items);
        Assert.Equal(2.5m, line.Quantity);
        Assert.Equal(1.0d, line.QuantityConfidence);
    }

    [Fact]
    public void Deterministic_reads_are_certain_and_say_so()
    {
        var text = Preamble + "\n" + string.Join("\n", "10", "902017274", "3801", "EA", "5", "ITEM");
        var outcome = AramcoBidListExtraction.TryExtract(text, "bid.doc", out _);

        var line = Assert.Single(outcome!.Result!.Items);
        Assert.Equal(1.0d, line.ItemConfidence);
        Assert.Equal(1.0d, outcome.Result.OverallConfidence);
    }
}
