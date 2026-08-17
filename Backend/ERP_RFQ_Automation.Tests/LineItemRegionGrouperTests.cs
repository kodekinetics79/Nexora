using ERP_RFQ_Automation.Extraction;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// One region per LINE ITEM, not one per line of text.
///
/// <para>The shapes below are taken from a real customer bid list (Saudi Aramco "MATERIALS
/// E-BIDDING SYSTEM") that lost 250 of its 259 line items in production: a 9-digit material
/// code followed by twenty-odd lines of specification, repeated. Treating each line as an item
/// made the chunker slice mid-specification, so the model was handed "CONTACT RATING:" and
/// "SILVER PLATED;" and asked to find a whole line item in it.</para>
/// </summary>
public sealed class LineItemRegionGrouperTests
{
    /// <summary>One Aramco-style item: a bare material code, then its specification.</summary>
    private static string[] Item(string code, params string[] spec) => [code, .. spec];

    [Fact]
    public void An_item_keeps_its_whole_specification_in_one_region()
    {
        string[] lines = [
            .. Item("906002718",
                "RELAY:GP,240 VAC 5 A, 28 VDC 5 A,4NO4NC,",
                "CONTACT RATING:", "240 VAC 5 A, 28 VDC 5 A;",
                "COIL VOLTAGE:", "200/220 VAC;", "CONTACT:", "SILVER PLATED;"),
            .. Item("906003335",
                "BREAKER,CRCT,MC,400 VAC,2 POLE,3 A,#S282",
                "AMPERAGE:", "CONNECTION:", "SCREW TERMINAL;"),
        ];

        var regions = LineItemRegionGrouper.Group(lines);

        // TWO items, not thirteen lines. This single assertion is the whole defect.
        Assert.Equal(2, regions.Count);

        Assert.StartsWith("906002718", regions[0]);
        Assert.Contains("SILVER PLATED;", regions[0]);
        // The second item's content must NOT bleed into the first.
        Assert.DoesNotContain("BREAKER", regions[0]);

        Assert.StartsWith("906003335", regions[1]);
        Assert.Contains("SCREW TERMINAL;", regions[1]);
    }

    [Fact]
    public void Preamble_before_the_first_item_is_kept_not_dropped()
    {
        // Some bid lists state the delivery term or currency between the header slice and the
        // first item. Losing it silently is how a quote goes out in the wrong currency.
        string[] lines = [
            "For Foreign Suppliers, if the delivery type is CIF or DDP, attach EXW or FOB.",
            "All prices in USD.",
            .. Item("906002718", "RELAY:GP", "CONTACT RATING:"),
            .. Item("906003335", "BREAKER,CRCT", "AMPERAGE:"),
        ];

        var regions = LineItemRegionGrouper.Group(lines);

        Assert.Equal(3, regions.Count);
        Assert.Contains("All prices in USD.", regions[0]);
        Assert.StartsWith("906002718", regions[1]);
        Assert.StartsWith("906003335", regions[2]);
    }

    [Theory]
    [InlineData("1. RELAY, GENERAL PURPOSE")]
    [InlineData("12) BREAKER, CIRCUIT, MOLDED CASE")]
    [InlineData("Item 3: GASKET, SPIRAL WOUND")]
    [InlineData("Line No. 7 - FLANGE, WELD NECK")]
    [InlineData("Pos 4. STUD BOLT SET")]
    public void The_common_ways_a_document_numbers_its_items_all_open_a_region(string opener)
    {
        string[] lines = [opener, "some specification line", opener.Replace("1", "9").Replace("3", "9").Replace("7", "9").Replace("2", "9").Replace("4", "9"), "another spec line"];

        var regions = LineItemRegionGrouper.Group(lines);

        Assert.Equal(2, regions.Count);
        Assert.Contains("some specification line", regions[0]);
    }

    [Fact]
    public void A_document_with_no_recognisable_item_structure_is_returned_unchanged()
    {
        // THE SAFETY PROPERTY. A document class this cannot read must keep exactly today's
        // behaviour rather than trade one wrong grouping for another.
        string[] lines = [
            "Please quote the following requirement",
            "as per attached specification",
            "delivery to site within four weeks",
        ];

        var regions = LineItemRegionGrouper.Group(lines);

        Assert.Equal(lines.Length, regions.Count);
        Assert.Equal(lines, regions);
    }

    [Fact]
    public void A_single_boundary_is_not_enough_to_trust_the_pattern()
    {
        // One long number inside a specification ("500 MVA transformer 380110138") must not be
        // mistaken for an item boundary and split a single item in two.
        string[] lines = [
            "TRANSFORMER SPECIFICATION",
            "3801101380",
            "rated power 500 MVA",
        ];

        var regions = LineItemRegionGrouper.Group(lines);

        Assert.Equal(lines.Length, regions.Count);
    }

    [Fact]
    public void An_empty_document_yields_no_regions()
    {
        Assert.Empty(LineItemRegionGrouper.Group([]));
    }

    [Fact]
    public void The_expected_count_now_describes_items_rather_than_lines()
    {
        // The operator-facing ratio ("2/90 items") is only meaningful if the denominator is a
        // count of items. Ninety lines of three items must report three.
        var lines = new List<string>();
        foreach (var code in new[] { "906002718", "906003335", "901476746" })
        {
            lines.Add(code);
            for (var i = 0; i < 29; i++) lines.Add($"SPEC LINE {i}:");
        }
        Assert.Equal(90, lines.Count);

        Assert.Equal(3, LineItemRegionGrouper.Group(lines).Count);
    }

    // ---------------------------------------------------------------- cost controls

    [Fact]
    public void A_document_full_of_numbered_paragraphs_is_not_read_as_an_item_list()
    {
        // THE 54-PAGE .docx. Contract prose numbers its clauses, and those numbers restart per
        // section rather than running 1..n. Treating each as a line item reported 1,603 items,
        // planned 70 chunks, spent an entire monthly token budget and returned 24 real items.
        var lines = new List<string>();
        foreach (var section in new[] { "SCOPE", "DELIVERY", "WARRANTY", "PAYMENT" })
        {
            lines.Add(section);
            for (var clause = 1; clause <= 6; clause++)
            {
                lines.Add($"{clause}. The supplier shall observe the requirements of this section");
                lines.Add("   and shall provide evidence of compliance on request.");
            }
        }

        var regions = LineItemRegionGrouper.Group(lines);

        // Restarting ordinals are prose, so the document is returned ungrouped rather than
        // shredded into dozens of "items".
        Assert.Equal(lines.Count, regions.Count);
    }

    [Fact]
    public void A_genuine_numbered_item_list_still_groups()
    {
        // The counterpart: ordinals that RUN are a real item list and must keep working.
        var lines = new List<string>();
        for (var item = 1; item <= 8; item++)
        {
            lines.Add($"{item}. VALVE, BALL, 2IN CLASS 300");
            lines.Add("   material: stainless steel");
            lines.Add("   quantity: 4 EA");
        }

        var regions = LineItemRegionGrouper.Group(lines);

        Assert.Equal(8, regions.Count);
        Assert.All(regions, r => Assert.Contains("quantity: 4 EA", r));
    }

    [Fact]
    public void An_item_claimed_on_more_than_every_other_line_is_treated_as_mis_read()
    {
        // The density guard. Codes on almost every line cannot all be items — a real item
        // carries a description, a quantity and a unit, so it occupies several lines.
        var lines = new List<string>();
        for (var i = 0; i < 40; i++)
        {
            lines.Add($"90600{i:D4}");
            lines.Add("ref");
        }
        lines.RemoveAt(lines.Count - 1); // tip the ratio past one-in-two

        var regions = LineItemRegionGrouper.Group(lines);

        Assert.Equal(lines.Count, regions.Count);
    }
}
