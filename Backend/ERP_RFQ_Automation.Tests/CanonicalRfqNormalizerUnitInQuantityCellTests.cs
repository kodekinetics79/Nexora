using ERP_RFQ_Automation.DTOs.DocumentIntelligence;
using ERP_RFQ_Automation.Services;
using ERP_RFQ_Automation.Services.DocumentIntelligence;
using ERP_RFQ_Automation.Tests.Support;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// "Not picking if UOM is missing." A sheet with no unit column that writes "10 Nos" in the
/// quantity cell states its unit — in the quantity cell. The quantity was read and the word was
/// thrown away, so every line reached the decision screen asking the rep for a unit the customer
/// had written. The word is the document's own, never a default: a cell with only a number still
/// has no unit, and a word the canonicaliser does not know is not taken as one.
/// </summary>
public sealed class CanonicalRfqNormalizerUnitInQuantityCellTests
{
    private static readonly CanonicalRfqNormalizer Normalizer = new();

    private static RfqSpreadsheetRow Row(string quantity, string? unit = null) => new()
    {
        RowNumber = 2,
        SourceDocumentName = "bid-list.xlsx",
        RfqNo = "RFQ-U1",
        BuyerName = "Aramco",
        ProductName = "Gasket spiral wound",
        Quantity = quantity,
        UnitOfMeasure = unit,
    };

    private static CanonicalRfqLineItem Line(string quantity, string? unit = null)
        => Assert.Single(Assert.Single(Normalizer.NormalizeSpreadsheetRows(new[] { Row(quantity, unit) }, businessUnitId: 7)
            .Documents).LineItems);

    [Theory]
    [InlineData("10 Nos", 10, "Nos")]
    [InlineData("500 PCS", 500, "PCS")]
    [InlineData("12 Sets", 12, "Sets")]
    [InlineData("40 Mtr", 40, "Mtr")]
    [InlineData("25 Pack", 25, "Pack")]   // stated, and still refused later: a person says how many are in a pack
    public void A_unit_written_in_the_quantity_cell_is_read_as_the_lines_unit(string cell, int quantity, string unit)
    {
        var line = Line(cell);

        Assert.Equal(quantity, line.Quantity.Value);
        Assert.Equal(unit, line.UnitOfMeasure.Value);
        Assert.NotEqual(CanonicalValueKind.Missing, line.UnitOfMeasure.Kind);
        Assert.Contains("unit_read_from_quantity_cell", line.UnitOfMeasure.Transformations);
        // The evidence is the quantity cell, carrying the word itself, so the unit can be proven.
        var evidence = Assert.Single(line.UnitOfMeasure.Evidence);
        Assert.Equal(unit, evidence.RawValue);
        Assert.Equal(Assert.Single(line.Quantity.Evidence).Location, evidence.Location);
    }

    [Fact]
    public void A_bare_number_still_has_no_unit()
    {
        var line = Line("10");
        Assert.Null(line.UnitOfMeasure.Value);
        Assert.Equal(CanonicalValueKind.Missing, line.UnitOfMeasure.Kind);
    }

    [Fact]
    public void A_word_that_is_not_a_unit_is_not_taken_as_one()
    {
        var line = Line("10 approx");
        Assert.Equal(10, line.Quantity.Value);
        Assert.Null(line.UnitOfMeasure.Value);
    }

    [Fact]
    public void The_unit_column_wins_over_a_word_in_the_quantity_cell()
    {
        var line = Line("10 Nos", unit: "M");
        Assert.Equal("M", line.UnitOfMeasure.Value);
        Assert.DoesNotContain("unit_read_from_quantity_cell", line.UnitOfMeasure.Transformations);
    }

    [Fact]
    public void The_stored_unit_is_the_canonical_code_for_the_word_the_customer_wrote()
    {
        var line = Line("10 Nos");
        var stored = LeadItemMapper.Map(Ext.Item(0.9) with { UnitOfMeasure = line.UnitOfMeasure.Value }, _ => null);
        Assert.Equal("EA", stored.UnitOfMeasure);
    }
}
