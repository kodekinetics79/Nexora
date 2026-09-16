using System.Text;
using ERP_RFQ_Automation.Services.DocumentIntelligence;
using OfficeOpenXml;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// The "check against the document" pane was empty for a spreadsheet source — the most common
/// shape an RFQ arrives in — so the rep confirmed six lines against nothing. The reader hands the
/// parser's own cells back as rows, numbered as Excel numbers them, with the heading row the
/// parser located picked out.
/// </summary>
public sealed class SpreadsheetGridReaderTests
{
    private static byte[] BidListXlsx()
    {
        ExcelPackage.LicenseContext = LicenseContext.NonCommercial;
        using var package = new ExcelPackage();
        var sheet = package.Workbook.Worksheets.Add("Bid");
        sheet.Cells[1, 1].Value = "Al Jazirah — request for quotation";
        // Row 2 is blank on purpose: the numbering must keep the gap.
        sheet.Cells[3, 1].Value = "Item"; sheet.Cells[3, 2].Value = "Description"; sheet.Cells[3, 3].Value = "Qty"; sheet.Cells[3, 4].Value = "Unit";
        sheet.Cells[4, 1].Value = 1; sheet.Cells[4, 2].Value = "Gate valve 2in"; sheet.Cells[4, 3].Value = 4; sheet.Cells[4, 4].Value = "EA";
        sheet.Cells[5, 1].Value = 2; sheet.Cells[5, 2].Value = "Ball valve 1in"; sheet.Cells[5, 3].Value = 12; sheet.Cells[5, 4].Value = "EA";
        var notes = package.Workbook.Worksheets.Add("Notes");
        notes.Cells[1, 1].Value = "Deliver to Dammam";
        return package.GetAsByteArray();
    }

    [Fact]
    public void An_xlsx_comes_back_as_its_rows_with_the_parsers_heading_row_and_excels_own_numbering()
    {
        var grid = SpreadsheetGridReader.Read(BidListXlsx(), "bid-list.xlsx",
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");

        Assert.NotNull(grid);
        Assert.Equal(["Bid", "Notes"], grid!.Sheets.Select(sheet => sheet.Name));
        var bid = grid.Sheets[0];
        Assert.Equal(3, bid.HeaderRowNumber);
        Assert.False(bid.Truncated);
        Assert.Equal([1, 3, 4, 5], bid.Rows.Select(row => row.Number));
        Assert.Equal(["Item", "Description", "Qty", "Unit"], bid.Rows.Single(row => row.Number == 3).Cells);
        Assert.Equal(["1", "Gate valve 2in", "4", "EA"], bid.Rows.Single(row => row.Number == 4).Cells);
        // A sheet the parser found no RFQ columns on still shows its rows, just without a heading.
        Assert.Null(grid.Sheets[1].HeaderRowNumber);
        Assert.Equal(["Deliver to Dammam"], grid.Sheets[1].Rows.Single().Cells);
    }

    [Fact]
    public void A_csv_is_one_sheet_numbered_by_line()
    {
        var csv = Encoding.UTF8.GetBytes("Description,Qty,Unit\nGate valve 2in,4,EA\n\nBall valve 1in,12,EA\n");

        var grid = SpreadsheetGridReader.Read(csv, "inquiry.csv", "text/csv");

        var sheet = Assert.Single(grid!.Sheets);
        Assert.Equal("CSV", sheet.Name);
        Assert.Equal(1, sheet.HeaderRowNumber);
        Assert.Equal([1, 2, 4], sheet.Rows.Select(row => row.Number));
        Assert.Equal(["Ball valve 1in", "12", "EA"], sheet.Rows.Last().Cells);
    }

    [Fact]
    public void Only_spreadsheets_are_read()
    {
        Assert.True(SpreadsheetGridReader.IsSpreadsheet("bid.xlsx", null));
        Assert.True(SpreadsheetGridReader.IsSpreadsheet("bid.XLS", "application/octet-stream"));
        Assert.True(SpreadsheetGridReader.IsSpreadsheet("bid", "text/csv"));
        Assert.False(SpreadsheetGridReader.IsSpreadsheet("bid.pdf", "application/pdf"));
        Assert.False(SpreadsheetGridReader.IsSpreadsheet("bid.docx",
            "application/vnd.openxmlformats-officedocument.wordprocessingml.document"));
        Assert.Null(SpreadsheetGridReader.Read([1, 2, 3], "bid.pdf", "application/pdf"));
    }

    [Fact]
    public void A_long_sheet_is_cut_at_the_row_limit_and_says_so()
    {
        var builder = new StringBuilder("Description,Qty,Unit\n");
        for (var i = 1; i <= SpreadsheetGridReader.MaxRows + 5; i++) builder.Append($"Item {i},1,EA\n");

        var sheet = Assert.Single(SpreadsheetGridReader.Read(Encoding.UTF8.GetBytes(builder.ToString()), "big.csv", "text/csv")!.Sheets);

        Assert.Equal(SpreadsheetGridReader.MaxRows, sheet.Rows.Count);
        Assert.True(sheet.Truncated);
    }
}
