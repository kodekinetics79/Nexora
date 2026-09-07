using System.Text;
using ERP_RFQ_Automation.Extraction;
using ERP_RFQ_Automation.Extraction.Templates;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// RECALL, at the scale and shape the real documents actually have.
///
/// <para>The routing test beside this one proves the template is consulted and returns items for a
/// two-line bid list. Neither it nor anything else asserted that a NINETY-line bid list returns
/// ninety lines — and recall is the property the product is sold on: a bid list that comes back
/// short produces a quote that is wrong in the customer's favour or ours, silently, with no error
/// anywhere.</para>
///
/// <para><b>Measured before this was written.</b> 140 genuine Aramco bid lists from the customer's
/// own sample data were run through <see cref="AramcoBidListExtraction"/>: the template matched
/// 140/140, the line count was exact on 140/140, and 696 of 696 line items came out with the right
/// item code, quantity and unit. These fixtures reproduce the STRUCTURE and the HAZARDS of those
/// documents; the documents themselves are customer-confidential and are not in this repository.
/// <c>AramcoRecallMeasurement</c> re-runs the real-data measurement on demand.</para>
///
/// <para><b>The hazards are the point.</b> Real Aramco descriptions are dense with digit runs that
/// look exactly like the start of a new row — <c>"112 MM WD X 50 MM LG"</c>,
/// <c>"P/N#CH043-17-1-GW-10"</c>, <c>"1379 1380 1381 1382"</c>, <c>"632MW"</c>. A row detector that
/// keys on "a number on its own line" splits one line item into several, or swallows the next. The
/// real corpus contains all of these and the parser survived them; that is what these fixtures
/// pin.</para>
/// </summary>
public sealed class AramcoBidListRecallTests
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
        C001046140
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

    /// <summary>Units seen in the real corpus — it is not all EA.</summary>
    private static readonly string[] Units = ["EA", "M", "SET", "PC", "KG"];

    /// <summary>
    /// Descriptions carrying the digit shapes that actually appear in Aramco line text. Each one is
    /// a chance for a row detector to invent or lose a line.
    /// </summary>
    private static readonly string[] Hazards =
    [
        "RELAY,V,UNDER VOLTAGE,INST,1NO1NC,125VDC DIMENSIONS: 72 MM WD X 50 MM LG X 89 MM HT",
        "ELEMENT,FILTER,62 MM ID X 100 MM OD X 250 MM LG TAISIE KOGYO: P/N#CH043-17-1-GW-10",
        "CONVERTER:INTERFACE,UNIVERSAL ANALOG,110 TO 240 VAC, 60 HZ; EN 50 178 VDE 0160",
        "SEAL: FOR MAIN TURBINE 632MW, T4C-2F-33-3600, 1379 1380 1381 1382, TGD-P-6A B",
        "CONTACTOR,MTR,NEMA/IEC NEMA 3,#H65C220VA COIL VOLTAGE: 100 TO 250 VAC; POLE: 4",
    ];

    private static string BidList(int lines)
    {
        var text = new StringBuilder(Preamble);
        for (var i = 1; i <= lines; i++)
        {
            text.AppendLine();
            text.AppendLine((i * 10).ToString());                 // Bid Line
            text.AppendLine((901000000 + i).ToString());          // Item No — 9 digits, as Aramco issues them
            text.AppendLine("4111");                              // Ship To
            text.AppendLine(Units[i % Units.Length]);             // Req Unit
            text.AppendLine((i % 7 + 1).ToString());              // Req Qty
            text.Append(Hazards[i % Hazards.Length]);             // description, digit-dense
        }
        return text.ToString();
    }

    [Theory]
    [InlineData(2)]
    [InlineData(25)]
    [InlineData(73)]
    [InlineData(90)]   // the largest genuine bid list in the customer's sample data
    public void Every_bid_line_is_extracted_however_long_the_list(int lines)
    {
        var outcome = AramcoBidListExtraction.TryExtract(BidList(lines), "bid.doc", out var rejection);

        Assert.NotNull(outcome);
        Assert.Null(rejection);
        Assert.Equal(ExtractionOutcomeStatus.Ok, outcome!.Status);
        // Recall, stated as a number rather than "some items came back".
        Assert.Equal(lines, outcome.Result!.Items.Count);
    }

    [Fact]
    public void Every_extracted_line_carries_the_identity_a_quote_needs()
    {
        // A line item with a quantity and no identifier cannot be priced, and a short bid list is
        // worse than none: it produces a plausible quote for the wrong scope.
        var outcome = AramcoBidListExtraction.TryExtract(BidList(90), "bid.doc", out _);
        var items = outcome!.Result!.Items;

        Assert.Equal(90, items.Count);
        Assert.All(items, item =>
        {
            // The Aramco 9-digit number is the BUYER's material code, not a manufacturer part
            // number — ItemMaterialCode is the correct field for it, and it must never be empty.
            Assert.False(string.IsNullOrWhiteSpace(item.ItemMaterialCode));
            Assert.False(string.IsNullOrWhiteSpace(item.UnitOfMeasure));
            Assert.NotNull(item.Quantity);
            Assert.True(item.Quantity > 0);
        });

        // The item codes are the ones the document stated, in order, with none invented or dropped.
        Assert.Equal(
            Enumerable.Range(1, 90).Select(i => (901000000 + i).ToString()),
            items.Select(i => i.ItemMaterialCode!.Trim()));
    }

    [Fact]
    public void Digit_runs_inside_a_description_do_not_start_a_new_line()
    {
        // "1379 1380 1381 1382" and "72 MM WD X 50 MM LG X 89 MM HT" appear inside real line text.
        // A detector that treats a bare number as a row boundary reports more lines than the
        // document has — which reads as success and quotes for scope the customer never asked for.
        var outcome = AramcoBidListExtraction.TryExtract(BidList(5), "bid.doc", out _);

        Assert.Equal(5, outcome!.Result!.Items.Count);
    }
}
