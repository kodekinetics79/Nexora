using System.Text;
using ERP_RFQ_Automation.Services.DocumentIntelligence;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// A spelling list only knows what it has seen. A bid number is the same value on every line
/// and an item number counts up; those shapes are read whatever the heading says, with the
/// heading only asked whether it points the same way.
/// </summary>
public sealed class UnmappedColumnShapesTests
{
    private static byte[] Csv(params string[] lines) => Encoding.UTF8.GetBytes(string.Join("\r\n", lines));
    private static readonly NativeSpreadsheetParser Parser = new();

    [Fact]
    public void A_sheet_with_unknown_headings_is_read_by_what_its_columns_hold()
    {
        var rows = Parser.ParseCsv(Csv(
            "Bid #,Sl,Mat. Reference,Make,Description,Qty,Ccy",
            "B-2026-0417,10,3RT2015-1BB41,Siemens,Contactor 9A,4,USD",
            "B-2026-0417,20,LC1D09BD,Schneider,Contactor 9A 24VDC,2,USD",
            "B-2026-0417,30,1SDA054523R1,ABB,Breaker 3P 250A,6,USD"), "bid.csv");

        Assert.Equal(3, rows.Count);
        Assert.All(rows, row =>
        {
            Assert.Equal("B-2026-0417", row.RfqNo);
            Assert.Equal("USD", row.Currency);
            Assert.StartsWith("read_from_column_shape", row.FieldProvenance[RfqSpreadsheetFields.RfqNo].Note);
            Assert.Equal(UnmappedColumnShapes.ShapeConfidence, row.FieldProvenance[RfqSpreadsheetFields.RfqNo].Confidence);
        });
        Assert.Equal(new[] { "10", "20", "30" }, rows.Select(r => r.CustomerLineNumber));
        Assert.Equal(new[] { "3RT2015-1BB41", "LC1D09BD", "1SDA054523R1" }, rows.Select(r => r.ManufacturerPartNumber));
        Assert.Equal(new[] { "Siemens", "Schneider", "ABB" }, rows.Select(r => r.ManufacturerName));
        Assert.All(rows, row => Assert.Empty(row.UnmappedColumns));
    }

    [Fact]
    public void A_constant_code_under_an_unrelated_heading_is_not_the_request_number()
    {
        var rows = Parser.ParseCsv(Csv(
            "Zone,Description,Qty",
            "9CAT,Relay,4", "9CAT,Module,2", "9CAT,Switch,1"), "bid.csv");

        Assert.All(rows, row => Assert.Null(row.RfqNo));
        Assert.All(rows, row => Assert.Equal("9CAT", row.UnmappedColumns["Zone"]));
    }

    [Fact]
    public void A_request_heading_whose_value_changes_per_line_is_not_the_request_number()
    {
        var rows = Parser.ParseCsv(Csv(
            "Bid Ref,Description,Qty",
            "B-1,Relay,4", "B-2,Module,2", "B-3,Switch,1"), "bid.csv");

        Assert.All(rows, row => Assert.Null(row.RfqNo));
    }

    [Fact]
    public void A_recognised_heading_always_wins_over_a_shape()
    {
        var rows = Parser.ParseCsv(Csv(
            "RFQ No,Bid #,Description,Qty",
            "RFQ-9,B-1,Relay,4", "RFQ-9,B-1,Module,2", "RFQ-9,B-1,Switch,1"), "bid.csv");

        Assert.All(rows, row => Assert.Equal("RFQ-9", row.RfqNo));
        Assert.All(rows, row => Assert.Equal("B-1", row.UnmappedColumns["Bid #"]));
    }

    [Fact]
    public void A_constant_date_is_the_closing_date_only_when_the_heading_speaks_of_closing()
    {
        var closing = Parser.ParseCsv(Csv(
            "Submission cut-off,Description,Qty",
            "2026-10-08,Relay,4", "2026-10-08,Module,2", "2026-10-08,Switch,1"), "bid.csv");
        Assert.All(closing, row => Assert.Equal("2026-10-08", row.BidClosingDate));

        var unknown = Parser.ParseCsv(Csv(
            "Audit stamp,Description,Qty",
            "2026-10-08,Relay,4", "2026-10-08,Module,2", "2026-10-08,Switch,1"), "bid.csv");
        Assert.All(unknown, row => Assert.Null(row.BidClosingDate));
    }

    [Fact]
    public void A_shape_read_value_reaches_the_lead_with_its_provenance()
    {
        var rows = Parser.ParseCsv(Csv(
            "Tender ID,Description,Qty",
            "T-55,Relay,4", "T-55,Module,2", "T-55,Switch,1"), "bid.csv");
        var document = Assert.Single(new CanonicalRfqNormalizer().NormalizeSpreadsheetRows(rows, 1, new DateTime(2026, 9, 11)).Documents);

        Assert.Equal("T-55", document.RfqNo.Value);
        Assert.Equal(UnmappedColumnShapes.ShapeConfidence, document.RfqNo.Confidence);
        Assert.Contains(document.RfqNo.Transformations, t => t.StartsWith("read_from_column_shape", StringComparison.Ordinal));
        Assert.Equal("'CSV'!A2", document.RfqNo.Evidence[0].Location);
    }
}
