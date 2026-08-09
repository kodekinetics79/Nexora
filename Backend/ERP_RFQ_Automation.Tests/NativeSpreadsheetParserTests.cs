using System.Text;
using ERP_RFQ_Automation.Services.DocumentIntelligence;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// Spreadsheets are the one ingestion path that works without the AI gateway, so anything this
/// parser drops is dropped for real customers today.
/// </summary>
public sealed class NativeSpreadsheetParserTests
{
    private static byte[] Csv(params string[] lines)
        => Encoding.UTF8.GetBytes(string.Join("\r\n", lines));

    private static readonly NativeSpreadsheetParser Parser = new();

    // ------------------------------------------------------------------- unit of measure
    // There was no unit column at all before: "500 M" of cable ingested as a bare 500 and was
    // quoted as 500 each.

    [Fact]
    public void The_unit_column_is_read()
    {
        var rows = Parser.ParseCsv(Csv(
            "RFQ No,Product Name,Quantity,UOM",
            "RFQ-1,Copper cable,500,M"), "test.csv");

        var row = Assert.Single(rows);
        Assert.Equal("500", row.Quantity);
        Assert.Equal("M", row.UnitOfMeasure);
    }

    [Theory]
    [InlineData("UOM")]
    [InlineData("Unit of Measure")]
    [InlineData("U/M")]
    [InlineData("Unit")]
    [InlineData("Units")]
    [InlineData("unit_of_measurement")]
    public void Unit_column_spellings_are_recognised(string header)
    {
        var rows = Parser.ParseCsv(Csv(
            $"Product Name,Quantity,{header}",
            "Copper cable,500,M"), "test.csv");

        Assert.Equal("M", Assert.Single(rows).UnitOfMeasure);
    }

    [Fact]
    public void A_missing_unit_stays_null_and_is_never_defaulted()
    {
        var rows = Parser.ParseCsv(Csv(
            "Product Name,Quantity,UOM",
            "Copper cable,500,"), "test.csv");

        Assert.Null(Assert.Single(rows).UnitOfMeasure);
    }

    // ------------------------------------------------------------------------ header row
    // The header used to be assumed to be the first row. Any workbook opening with a title or
    // covering block mapped zero columns and lost every data row with no diagnostic.

    [Fact]
    public void A_title_block_above_the_table_no_longer_discards_every_row()
    {
        var rows = Parser.ParseCsv(Csv(
            "ACME CONTRACTING COMPANY",
            "Request for Quotation",
            "Project: Riyadh Metro Phase 2",
            "",
            "RFQ No,Product Name,Quantity,UOM",
            "RFQ-77,Ball valve,12,EA",
            "RFQ-77,Copper cable,500,M"), "test.csv");

        Assert.Equal(2, rows.Count);
        Assert.Equal("Ball valve", rows[0].ProductName);
        Assert.Equal("EA", rows[0].UnitOfMeasure);
        Assert.Equal("500", rows[1].Quantity);
        Assert.Equal("M", rows[1].UnitOfMeasure);
        Assert.All(rows, r => Assert.Equal(5, r.HeaderRowNumber));
    }

    [Fact]
    public void Rows_above_the_header_are_not_emitted_as_line_items()
    {
        var rows = Parser.ParseCsv(Csv(
            "Some covering note",
            "RFQ No,Product Name,Quantity",
            "RFQ-9,Gasket,4"), "test.csv");

        Assert.Equal("Gasket", Assert.Single(rows).ProductName);
    }

    [Fact]
    public void A_sheet_with_no_recognisable_header_yields_no_rows_as_before()
    {
        // Preserves the previous contract: the caller falls through to the unstructured path.
        var rows = Parser.ParseCsv(Csv(
            "alpha,beta,gamma",
            "1,2,3"), "test.csv");

        Assert.Empty(rows);
    }

    [Fact]
    public void A_stray_row_matching_one_column_does_not_outrank_the_real_header()
    {
        var rows = Parser.ParseCsv(Csv(
            "RFQ No,Product Name,Quantity,UOM",
            "RFQ-5,Ball valve,2,EA",
            "Quantity",
            "99"), "test.csv");

        Assert.All(rows, r => Assert.Equal(1, r.HeaderRowNumber));
        Assert.Equal("Ball valve", rows[0].ProductName);
    }

    // ------------------------------------------------------------- real-world header spelling

    [Fact]
    public void Header_punctuation_and_spacing_do_not_defeat_matching()
    {
        var rows = Parser.ParseCsv(Csv(
            "RFQ No.,Item Description,Qty.,U/M,Part No.,Make",
            "RFQ-3,Butterfly valve,6,PCS,BV-200,KITZ"), "test.csv");

        var row = Assert.Single(rows);
        Assert.Equal("RFQ-3", row.RfqNo);
        Assert.Equal("Butterfly valve", row.ProductName);
        Assert.Equal("6", row.Quantity);
        Assert.Equal("PCS", row.UnitOfMeasure);
        Assert.Equal("BV-200", row.ManufacturerPartNumber);
        Assert.Equal("KITZ", row.ManufacturerName);
    }

    [Fact]
    public void Original_header_spellings_still_map()
    {
        // Regression guard for the aliases that existed before the map was widened.
        var rows = Parser.ParseCsv(Csv(
            "RFQNo,BuyerName,ReceivedDate,BidClosingDate,ProductName,Quantity,UnitPrice,Currency,ManufacturerName,ManufacturerPartNumber,LeadTimeDays",
            "R-1,Acme,2026-01-01,2026-02-01,Valve,3,10.50,SAR,KITZ,BV-1,14"), "test.csv");

        var row = Assert.Single(rows);
        Assert.Equal("R-1", row.RfqNo);
        Assert.Equal("Acme", row.BuyerName);
        Assert.Equal("Valve", row.ProductName);
        Assert.Equal("3", row.Quantity);
        Assert.Equal("10.50", row.UnitPrice);
        Assert.Equal("SAR", row.Currency);
        Assert.Equal("KITZ", row.ManufacturerName);
        Assert.Equal("BV-1", row.ManufacturerPartNumber);
        Assert.Equal("14", row.LeadTimeDays);
    }

    [Fact]
    public void A_total_price_column_does_not_capture_the_unit_price_field()
    {
        // Matching is exact after normalisation; substring matching would mis-bind this.
        var rows = Parser.ParseCsv(Csv(
            "Product Name,Quantity,Total Price",
            "Valve,3,31.50"), "test.csv");

        Assert.Null(Assert.Single(rows).UnitPrice);
    }
}
