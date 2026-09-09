using ERP_RFQ_Automation.Services.DocumentIntelligence;

namespace ERP_RFQ_Automation.Tests.DocumentIntelligence;

/// <summary>
/// Reading a Word RFP that states each line item DOWN the page — the Aramco/ASMO e-bidding shape,
/// where one enormous "Name | Alternative | Value" table repeats a block of labels per item.
/// </summary>
public class DocxFormBlockParserTests
{
    private readonly DocxFormBlockParser _parser = new();

    private static IReadOnlyList<IReadOnlyList<string?>> Grid(params string[] rows)
        => rows.Select(r => (IReadOnlyList<string?>)r.Split('|').Select(c => (string?)c.Trim()).ToList()).ToList();

    /// <summary>One item block as these exports actually write it, blank price included.</summary>
    private static IEnumerable<string> Item(string heading, string description, string qty, string material)
        => new[]
        {
            $"{heading}||",
            $"{description}||",
            "Price||",
            $"Quantity||{qty}",
            "Extended Price||",
            "Lead Time (In Days)||",
            "Requested Delivery Date||Fri, 1 Jan, 2027",
            $"Material Number||{material}",
            "Remarks||"
        };

    [Fact]
    public void RepeatedLabelBlocks_BecomeOneRowPerItem()
    {
        var rows = new List<string> { "Content||", "Name|Alternative|Value" };
        rows.AddRange(Item("8 BATTERY: LEAD ACID, 12 V", "BATTERY: LEAD ACID, 12 V, 6 CELLS", "1 each", "000000002000008534"));
        rows.AddRange(Item("9 BEARING Shell two halves", "BEARING Shell two halves for DE and NDE", "4 each", "000000002000010473"));
        rows.AddRange(Item("10 BEARING SLEEVE", "BEARING SLEEVE, SHELL", "2 set", "000000002000010961"));

        var parsed = _parser.Parse(Grid(rows.ToArray()), "rfp.docx", "Table 7");

        Assert.Equal(3, parsed.Count);
        Assert.Equal("BATTERY: LEAD ACID, 12 V, 6 CELLS", parsed[0].ProductName);
        Assert.Equal("1", parsed[0].Quantity);
        Assert.Equal("each", parsed[0].UnitOfMeasure);
        Assert.Equal("000000002000008534", parsed[0].ManufacturerPartNumber);
        Assert.Equal("Fri, 1 Jan, 2027", parsed[0].RequiredDeliveryDate);
        Assert.Equal("4", parsed[1].Quantity);
        Assert.Equal("2", parsed[2].Quantity);
        Assert.Equal("set", parsed[2].UnitOfMeasure);
    }

    [Fact]
    public void ThePriceLabel_IsLeftEmpty_BecauseTheSupplierQuotesIt()
    {
        var rows = new List<string> { "Name|Alternative|Value" };
        rows.AddRange(Item("8 BATTERY", "BATTERY, 12 V", "1 each", "M-1"));
        rows.AddRange(Item("9 BEARING", "BEARING, SHELL", "1 each", "M-2"));

        var parsed = _parser.Parse(Grid(rows.ToArray()), "rfp.docx", "Table 7");

        // An RFP asks US for the price. Reading anything into it would invent a number the buyer
        // never wrote, and it would look exactly like a real one.
        Assert.All(parsed, r => Assert.Null(r.UnitPrice));
    }

    [Fact]
    public void AScoringTableWithTheSameLabels_YieldsNothing()
    {
        // THE TABLE THAT SITS BESIDE THE REAL ONE. Identical labels, identical block count — but
        // its values are weightings. Without the countable-quantity rule the reader returned
        // 3,028 rows for a 1,514-line document and stamped "0%" onto unit price.
        var rows = new List<string> { "Scoring||", "Name|Weight|" };
        foreach (var n in new[] { "8 BATTERY", "9 BEARING", "10 SLEEVE" })
            rows.AddRange(new[] { $"{n}|0%|", "Price|0%|", "Quantity|0%|", "Extended Price|0%|", "Remarks|0%|" });

        var parsed = _parser.Parse(Grid(rows.ToArray()), "rfp.docx", "Table 8");

        Assert.Empty(parsed);
    }

    [Fact]
    public void AMultiWordUnit_StillSplitsAndTheItemSurvives()
    {
        // REGRESSION, found on the real document. Exactly one line of 1,514 read
        // "1 square meter/second"; a single-word unit rule failed to split it, the quantity then
        // failed to parse, and the item vanished with no diagnostic. One silent loss in fifteen
        // hundred is the kind that reaches a customer as a missing line.
        var rows = new List<string> { "Name|Alternative|Value" };
        rows.AddRange(Item("586 FAN, 280 2P, PLASTIC", "FAN, 280 2P, PLASTIC", "1 square meter/second", "M-586"));
        rows.AddRange(Item("587 FAN, 300 2P", "FAN, 300 2P", "3 each", "M-587"));
        rows.AddRange(Item("588 FAN, 320 4P", "FAN, 320 4P", "2 each", "M-588"));

        var parsed = _parser.Parse(Grid(rows.ToArray()), "rfp.docx", "Table 7");

        Assert.Equal(3, parsed.Count);
        Assert.Equal("1", parsed[0].Quantity);
        Assert.Equal("square meter/second", parsed[0].UnitOfMeasure);
    }

    [Fact]
    public void TheNearestTitleNamesTheItem_NotTheLongestPrecedingProse()
    {
        // The preamble of a real RFP runs to paragraphs far longer than any item description.
        // Preferring the longest pending line named the first item after a block of bidding
        // instructions; the nearest line is the item's own description.
        var rows = new List<string>
        {
            "Name|Alternative|Value",
            "7.1 Commercial Documents Please attach all relevant commercial documentation to your response||",
            "8 BATTERY: LEAD ACID||",
            "BATTERY: LEAD ACID, 12 V||"
        };
        rows.AddRange(new[] { "Price||", "Quantity||1 each", "Extended Price||", "Material Number||M-1", "Remarks||" });
        rows.AddRange(Item("9 BEARING", "BEARING, SHELL", "1 each", "M-2"));
        rows.AddRange(Item("10 SLEEVE", "SLEEVE, SHELL", "1 each", "M-3"));

        var parsed = _parser.Parse(Grid(rows.ToArray()), "rfp.docx", "Table 7");

        Assert.Equal("BATTERY: LEAD ACID, 12 V", parsed[0].ProductName);
        Assert.DoesNotContain("Commercial Documents", parsed[0].ProductName!);
    }

    [Fact]
    public void FewerThanThreeRepetitions_IsNotTreatedAsAForm()
    {
        // A left-hand value is only believed to be a LABEL once it recurs, and two occurrences is
        // not enough to tell a repeated form from a two-row panel that happens to share a word.
        // Nothing is lost by refusing: a two-item enquiry is a single cheap chunk on the model
        // path, whereas mistaking a panel for a line-item list invents items nobody asked for.
        var rows = new List<string> { "Name|Alternative|Value" };
        rows.AddRange(Item("8 BATTERY", "BATTERY, 12 V", "1 each", "M-1"));
        rows.AddRange(Item("9 BEARING", "BEARING, SHELL", "1 each", "M-2"));

        Assert.Empty(_parser.Parse(Grid(rows.ToArray()), "rfp.docx", "Table 7"));
    }

    [Fact]
    public void ASingleBlock_IsNotTreatedAsAForm()
    {
        // One block is a summary panel, not a line-item list. Refusing costs nothing: the
        // document keeps whatever behaviour it already had.
        var parsed = _parser.Parse(Grid(new List<string> { "Name|Alternative|Value" }
            .Concat(Item("8 BATTERY", "BATTERY, 12 V", "1 each", "M-1")).ToArray()), "rfp.docx", "Table 7");

        Assert.Empty(parsed);
    }

    [Fact]
    public void AnOrdinaryTwoColumnTermsTable_IsNotReadAsLineItems()
    {
        // "Payment Terms | 30 days" repeated is a terms panel. No countable quantity anywhere,
        // so nothing is emitted.
        var parsed = _parser.Parse(Grid(
            "Term|Value",
            "Payment Terms|30 days",
            "Incoterms|DDP Dammam",
            "Validity|90 days",
            "Payment Terms|60 days",
            "Incoterms|FOB Jubail",
            "Validity|30 days"), "rfp.docx", "Table 3");

        Assert.Empty(parsed);
    }
}
