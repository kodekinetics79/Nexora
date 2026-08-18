using ERP_RFQ_Automation.Extraction.Templates;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// The Aramco bid list, read deterministically.
///
/// <para>Every fixture below is the shape of a real customer document — 18 of them were parsed
/// from live evidence storage while this was written, yielding 145 line items at zero token
/// cost. The document that previously consumed an entire tenant's monthly token budget and
/// returned 24 of 1,603 lines is the 73-item case.</para>
///
/// <para>Half of these tests exist to prove the parser REFUSES. That is the point: a model
/// that cannot read a document says so, while a template that maps the quantity column onto
/// the price column produces a quote that looks perfectly correct and is wrong by an order of
/// magnitude.</para>
/// </summary>
public sealed class AramcoBidListParserTests
{
    /// <summary>The masthead and header block every document from this system carries.</summary>
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

    private static string Doc(params string[] body) => Preamble + "\n" + string.Join("\n", body);

    // ---------------------------------------------------------------- reads

    [Fact]
    public void A_full_record_yields_every_column()
    {
        var result = AramcoBidListParser.Parse(Doc(
            "10", "902017274", "3801", "EA", "176",
            "KEY:SHAFT,SQUARE,10 MM X 10 MM LG,X22CRM",
            "SHAPE:", "SQUARE;"));

        Assert.True(result.IsTrustworthy, result.Rejection);
        var line = Assert.Single(result.Lines);
        Assert.Equal("10", line.BidLine);
        Assert.Equal("902017274", line.ItemNo);
        Assert.Equal("3801", line.ShipTo);
        Assert.Equal("EA", line.ReqUnit);
        Assert.Equal(176m, line.ReqQty);
        Assert.StartsWith("KEY:SHAFT", line.Description);
        // The whole specification block belongs to the item, not just its first line.
        Assert.Contains("SQUARE;", line.Description);
    }

    [Fact]
    public void A_record_with_no_ship_to_is_read_not_refused()
    {
        // Real documents leave Ship To blank. A fixed five-value record read the unit as a
        // plant code and refused a perfectly good bid — two of eighteen live documents.
        var result = AramcoBidListParser.Parse(Doc(
            "10", "301269585", "EA", "50000",
            "CONN,ELEC,SLV,NO TENSION,CU BODY,#SC2010"));

        Assert.True(result.IsTrustworthy, result.Rejection);
        var line = Assert.Single(result.Lines);
        Assert.Equal("EA", line.ReqUnit);
        Assert.Equal(50000m, line.ReqQty);
        Assert.Equal(string.Empty, line.ShipTo);
    }

    [Fact]
    public void Consecutive_items_do_not_bleed_into_one_another()
    {
        var result = AramcoBidListParser.Parse(Doc(
            "10", "902017274", "3801", "EA", "176", "KEY:SHAFT", "SHAPE:",
            "20", "902017276", "3801", "EA", "89",  "SEAL,4 MM DIA", "SIZE:",
            "30", "902017278", "3801", "M",  "12",  "CABLE,ELEC"));

        Assert.True(result.IsTrustworthy, result.Rejection);
        Assert.Equal(3, result.Lines.Count);
        Assert.DoesNotContain("SEAL", result.Lines[0].Description);
        Assert.Equal("M", result.Lines[2].ReqUnit);
        Assert.Equal(277m, result.Lines.Sum(l => l.ReqQty));
    }

    [Fact]
    public void The_header_supplies_the_customers_own_bid_reference()
    {
        // This becomes the Lead's RFQ number, so it must come from the document, never invented.
        var result = AramcoBidListParser.Parse(Doc("10", "902017274", "3801", "EA", "1", "ITEM"));

        Assert.Equal("C001046933", result.Bidno);
        Assert.Equal("2004414", result.VendorCode);
        Assert.Equal(new DateOnly(2021, 2, 16), result.BidDate);
        Assert.Equal(new DateOnly(2021, 2, 28), result.BidClose);
    }

    [Fact]
    public void The_unit_is_transcribed_verbatim_never_standardised()
    {
        // Canonicalisation is deterministic and happens later, where it can be corrected and
        // replayed. Rewriting the sender's wording here destroys the evidence a reviewer checks.
        var result = AramcoBidListParser.Parse(Doc("10", "902017274", "3801", "ST", "10", "KIT"));
        Assert.Equal("ST", Assert.Single(result.Lines).ReqUnit);
    }

    // ---------------------------------------------------------------- refuses

    [Fact]
    public void A_document_without_the_masthead_is_not_ours()
    {
        var result = AramcoBidListParser.Parse("Please quote 5 EA of ABC-123.");
        Assert.False(result.IsTrustworthy);
        Assert.Contains("masthead", result.Rejection);
    }

    [Fact]
    public void A_missing_column_header_block_is_refused()
    {
        var result = AramcoBidListParser.Parse(
            "MATERIALS E-BIDDING SYSTEM\n10\n902017274\n3801\nEA\n176\nITEM");
        Assert.False(result.IsTrustworthy);
        Assert.Contains("six-column header", result.Rejection);
    }

    [Fact]
    public void A_reordered_column_is_refused_rather_than_guessed()
    {
        // THE CENTRAL SAFETY PROPERTY. If the sheet ever swaps quantity and unit, the parser
        // must stop — not quote 176 of something measured in "176".
        var result = AramcoBidListParser.Parse(Doc(
            "10", "902017274", "3801", "176", "EA", "KEY:SHAFT"));

        Assert.False(result.IsTrustworthy);
        Assert.Contains("neither a unit of measure nor a plant code", result.Rejection);
    }

    [Fact]
    public void A_missing_quantity_is_refused()
    {
        var result = AramcoBidListParser.Parse(Doc(
            "10", "902017274", "3801", "EA", "KEY:SHAFT,SQUARE"));
        Assert.False(result.IsTrustworthy);
        Assert.Contains("quantity", result.Rejection);
    }

    [Fact]
    public void A_zero_quantity_is_refused()
    {
        // Zero is a real demand for no units. It cannot be quoted and must not be silently kept.
        var result = AramcoBidListParser.Parse(Doc("10", "902017274", "3801", "EA", "0", "KEY"));
        Assert.False(result.IsTrustworthy);
        Assert.Contains("cannot be quoted", result.Rejection);
    }

    [Fact]
    public void An_item_with_no_description_is_refused()
    {
        var result = AramcoBidListParser.Parse(Doc("10", "902017274", "3801", "EA", "176"));
        Assert.False(result.IsTrustworthy);
        Assert.Contains("no description", result.Rejection);
    }

    [Fact]
    public void A_skipped_line_item_is_caught_by_the_completeness_check()
    {
        // The cross-check that matters: material numbers cannot be miscounted, so a parse that
        // produced fewer records than the document holds codes has silently dropped a line —
        // and an under-quoted bid is worse than an expensive one.
        var doc = Doc(
            "10", "902017274", "3801", "EA", "176", "KEY:SHAFT",
            // a stray ninth-digit code inside the specification, with no record around it
            "SUPERSEDED BY", "902017999");

        var result = AramcoBidListParser.Parse(doc);

        Assert.False(result.IsTrustworthy);
        Assert.Contains("material number", result.Rejection);
    }

    [Fact]
    public void The_vendor_code_is_not_mistaken_for_a_line_item()
    {
        // The masthead's seven-digit vendor code sits above the items. A looser material-number
        // pattern counted it, and the completeness check then refused every real document.
        var result = AramcoBidListParser.Parse(Doc("10", "902017274", "3801", "EA", "176", "KEY"));
        Assert.True(result.IsTrustworthy, result.Rejection);
        Assert.Single(result.Lines);
    }

    [Fact]
    public void Recognition_is_cheap_and_specific()
    {
        Assert.True(AramcoBidListParser.Recognises("… MATERIALS E-BIDDING SYSTEM …"));
        Assert.False(AramcoBidListParser.Recognises("Please quote the attached"));
        Assert.False(AramcoBidListParser.Recognises(null));
        Assert.False(AramcoBidListParser.Recognises("   "));
    }
}
