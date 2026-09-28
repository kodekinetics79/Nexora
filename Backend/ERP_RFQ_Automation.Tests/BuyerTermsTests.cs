using ERP_RFQ_Automation.Extraction.Templates;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// The buyer's commercial terms, read from the rows an event print states above its line items.
/// The wording is the real shape of an ASMO/Ariba RFP print, not one invented to suit the reader.
/// </summary>
public sealed class BuyerTermsTests
{
    private static IReadOnlyList<IReadOnlyList<string?>> Table(params string?[][] rows) => rows;

    private static readonly IReadOnlyList<IReadOnlyList<IReadOnlyList<string?>>> EventPrint = new[]
    {
        Table(new[] { "Overview" }, new[] { "Owner", "Saad Almuteb" }, new[] { "Event Type", "RFP" }, new[] { "Currency", "US Dollar" }),
        Table(new[] { "Timing Rules" }, new[] { "Publish time", "9/2/2026 2:15 PM" }, new[] { "Due date", "10/8/2026 3:00 PM" },
            new[] { "Allow bidding overtime", "No" }),
        Table(new[] { "Currency Rules" }, new[] { "Allow participants to select bidding currency", "Yes" }),
        Table(new[] { "Exchange Rates" }, new[] { "From Currency", "To Currency", "Rate" },
            new[] { "Saudi Riyal", "US Dollar", "0.26" }, new[] { "US Dollar", "Saudi Riyal", "3.75" }),
        Table(
            new[] { "Content" },
            new[] { "Name", "Alternative", "Value" },
            new[] { "1 INTRODUCTION", "", "" },
            new[] { "This Request for Proposal (RFP) is issued to invite Vendor to submit an electronic bid, in accordance with the instructions and conditions contained in this RFP, for a Purchase Order (Order)/ Purchase Agreement (PA) for supply of Goods as detailed in this RFP under.  Vendor shall quote for supply and delivery of Goods to ASMO or ASMO customer's delivery point in Saudi Arabia.  Operation and governance of any Orders that might result from this RFP are given below.  Vendor shall ensure that the prices set out in its Proposal to Buyer do not include VAT amounts. All prices must be exclusive of VAT. Vendor shall separately identify in its Proposal any VAT amounts to be assessed on top of the quoted prices.", "", "" },
            new[] { "2 IKTVA Values", "", "" },
            new[] { "4.3 ASMO - RFP Clause - VENDOR WAREHOUSING PA.pdf ASMO - RFP Clause - VENDOR WAREHOUSING PA.pdf", "", "" },
            new[] { "4.4 ASMO - Terms and Conditions.pdf ASMO - Terms and Conditions.pdf", "", "" },
            new[] { "4.5 1- Required Incoterms - The preferred Incoterms for this bidding process are AMC/SAC. (submit alternative bid for (VDD/VTC). 2- Required Currency - Quotations shall be submitted in either USD or SAR. 3- Alternative Offers - Bidders may submit alternative offers for each item, where applicable. 4- Quantity Disclaimer - The quantities stated in the RFQ are hypothetical estimatesand do not necessarily reflect actual consumption or future demand. 6- contract duration – Option 1: offer for two-year agreement. Option 2: An alternative offer for a three-year agreement. *The preferred option will be selected upon finalization of the award.", "", "" },
            new[] { "5.1  BID VALIDITY  What is the last date for bid validity?  Ensure bid validity of at least sixty (60) days from the bid closing date, unless otherwise requested.", "", "" },
            new[] { "5.2  PACKING AND LABELING  Are you complying with Saudi Aramco's packing labeling and marking requirements as detailed in the bidding instructions, or do you wish to deliver using your own packing standards?", "", "" },
            new[] { "6.1  Technical Documents  Please attach all relevant technical documents of the items quoted.   DO NOT ATTACH COMMERCIAL DETAILS   YOUR BID WILL BE SUBJECT TO DISQUALIFICATION", "", "" },
            new[] { "6.2  Part Number Revision/Obsolescence  Is Vendor's quotation for all items matching with the product, part or model number that Saudi Aramco has requested?", "", "" },
            new[] { "8 MODULE ADAPT ESD 4CH. RELAY OUTPUT", "", "" },
            new[] { "Quantity", "", "1 each" }),
    };

    private static Dictionary<string, BuyerTerms.Term> Read(string? fileName = "RFP - 6000000028 - Switchgear Package 2026 - 1 of 3 (1).docx")
        => BuyerTerms.Read(EventPrint, fileName).ToDictionary(t => t.Key);

    [Fact]
    public void An_event_print_states_the_terms_a_quote_must_meet()
    {
        var terms = Read();

        Assert.Equal("AMC/SAC; also price VDD/VTC as an alternative", terms["delivery_terms"].Value);
        Assert.Equal("ASMO or ASMO customer's delivery point in Saudi Arabia", terms["deliver_to"].Value);
        Assert.Equal("2-year agreement; also offer 3-year as an alternative", terms["agreement"].Value);
        Assert.Equal("USD or SAR", terms["quote_currency"].Value);
        Assert.Equal("1 USD = 3.75 SAR · 1 SAR = 0.26 USD", terms["exchange_rate"].Value);
        Assert.Equal("At least 60 days after closing", terms["validity"].Value);
        Assert.Equal("Prices without VAT; show VAT separately", terms["vat"].Value);
        Assert.Equal("Allowed for each item", terms["alternatives"].Value);
        Assert.Equal("Estimates only, not a commitment", terms["quantities"].Value);
        Assert.Equal("ASMO - RFP Clause - VENDOR WAREHOUSING PA; ASMO - Terms and Conditions", terms["documents"].Value);
        Assert.Equal("Technical documents without prices, or the bid is disqualified", terms["technical_offer"].Value);
        Assert.Equal("Confirm each part number, or state the substitute", terms["part_numbers"].Value);
        Assert.Equal("Saudi Aramco packing, labelling and marking, or state your own", terms["packing"].Value);
        Assert.Equal("IKTVA values requested", terms["local_content"].Value);
    }

    [Fact]
    public void The_closing_keeps_the_time_the_portal_shuts()
    {
        var closes = Read()["closes"];
        Assert.Equal("8 Oct 2026, 3:00 PM, no extension", closes.Value);
        Assert.Equal("Due date: 10/8/2026 3:00 PM; Allow bidding overtime: No", closes.Quote);
    }

    [Fact]
    public void A_split_package_is_named_from_the_file()
    {
        Assert.Equal("Part 1 of 3", Read()["package"].Value);
        Assert.False(Read("RFP 6000000028.docx").ContainsKey("package"));
    }

    [Fact]
    public void Every_term_carries_the_buyers_own_sentence()
    {
        var terms = Read();
        Assert.Contains("preferred Incoterms for this bidding process are AMC/SAC", terms["delivery_terms"].Quote);
        Assert.Contains("two-year agreement", terms["agreement"].Quote);
        Assert.Contains("at least sixty (60) days", terms["validity"].Quote);
        Assert.All(terms.Values, term => Assert.False(string.IsNullOrWhiteSpace(term.Quote)));
    }

    [Fact]
    public void Terms_come_in_the_order_that_decides_the_bid()
    {
        var keys = BuyerTerms.Read(EventPrint, "x - 1 of 3.docx").Select(t => t.Key).ToList();
        Assert.Equal(new[] { "closes", "package", "delivery_terms", "deliver_to", "agreement" }, keys.Take(5));
    }

    [Fact]
    public void A_plain_terms_table_is_read_by_its_labels()
    {
        var grids = new[]
        {
            Table(new[] { "Incoterms", "DDP Dammam" }, new[] { "Payment Terms", "30 days from invoice" },
                new[] { "Offer Validity", "90 days" }, new[] { "Warranty", "18 months from delivery" }),
        };
        var terms = BuyerTerms.Read(grids, "SEC RFQ.docx").ToDictionary(t => t.Key);

        Assert.Equal("DDP Dammam", terms["delivery_terms"].Value);
        Assert.Equal("30 days from invoice", terms["payment"].Value);
        Assert.Equal("90 days", terms["validity"].Value);
        Assert.Equal("18 months from delivery", terms["warranty"].Value);
    }

    [Fact]
    public void A_document_that_states_no_terms_gets_none()
    {
        var grids = new[] { Table(new[] { "Item", "Description", "Qty", "Unit" }, new[] { "1", "Cable gland kit", "4", "EA" }) };
        Assert.Empty(BuyerTerms.Read(grids, "lines.docx"));
    }
}
