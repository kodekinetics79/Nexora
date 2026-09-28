using ERP_RFQ_Automation.DTOs.Lead;
using ERP_RFQ_Automation.DTOs.RfqDTOs;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Services;
using OfficeOpenXml;
using Xunit;

namespace ERP_RFQ_Automation.Tests;

public class LinesWorkbookTests
{
    private static RfqResponseDTO Rfq(int lines)
    {
        var rfq = new RfqResponseDTO
        {
            Id = 42,
            Rfqno = "RFQ/2026 0042",
            CustomerName = "Saudi Aramco",
            CustomerRfqReference = "7000123456",
            BidClosingDate = new DateTime(2026, 10, 5),
            CreatedBy = "qa",
        };
        for (var i = 1; i <= lines; i++)
        {
            rfq.Rfqitems.Add(new RfqitemResponseDTO
            {
                Id = i,
                Rfqid = 42,
                LineItemNo = (i * 10).ToString("D5"),
                ProductName = $"VALVE {i}",
                ProductShortDescription = $"Gate valve 2in class {i}",
                ManufacturerName = "TELEDYNE",
                ManufacturerPartNumber = $"MPN-{i}",
                Quantity = i * 2.5m,
                UnitOfMeasure = i == 2 ? null : "PC",
                ItemMaterialCode = $"1000{i}",
                ParticipationDecision = i == 1 ? Rfqitem.ParticipationQuote
                    : i == 2 ? Rfqitem.ParticipationNoQuote : Rfqitem.ParticipationPending,
                NoQuoteReason = i == 2 ? "Obsolete part" : null,
                ExtraFields = i == 1 ? "{\"Approved manufacturers\": \"TELEDYNE; EMERSON\", \"Plant\": \"Ras Tanura\"}"
                    : i == 2 ? "{\"Plant\": \"Abqaiq\", \"Material group\": \"VALVES\"}" : null,
                CreatedBy = "qa",
            });
        }
        return rfq;
    }

    private static ExcelWorksheet Read(byte[] bytes)
    {
        ExcelPackage.LicenseContext = LicenseContext.NonCommercial;
        var package = new ExcelPackage(new MemoryStream(bytes));
        return package.Workbook.Worksheets["Lines"];
    }

    private static int HeaderRow(ExcelWorksheet sheet)
    {
        for (var r = 1; r <= sheet.Dimension.End.Row; r++)
            if (sheet.Cells[r, 1].Text == "Line" && sheet.Cells[r, 2].Text == "Description") return r;
        throw new Xunit.Sdk.XunitException("Column heading row not found.");
    }

    private static List<string> Headers(ExcelWorksheet sheet) =>
        Enumerable.Range(1, sheet.Dimension.End.Column).Select(c => sheet.Cells[HeaderRow(sheet), c].Text).ToList();

    /// <summary>The cell under a heading, <paramref name="line"/> rows below it (1 = first line).</summary>
    private static ExcelRange At(ExcelWorksheet sheet, string heading, int line)
    {
        var column = Headers(sheet).IndexOf(heading);
        Assert.True(column >= 0, $"No '{heading}' column.");
        return sheet.Cells[HeaderRow(sheet) + line, column + 1];
    }

    [Fact]
    public void Every_line_is_one_row_with_every_field()
    {
        var sheet = Read(LinesWorkbook.ForRfq(Rfq(100)));

        Assert.Equal(HeaderRow(sheet) + 100, sheet.Dimension.End.Row);
        Assert.Equal(new[] { "Line", "Description", "Manufacturer", "Part number", "Qty", "Unit", "Quoting", "Reason" },
            Headers(sheet).Take(8));
        foreach (var heading in new[] { "Catalogue product", "Offered part number", "Unit price", "Supplier",
                     "Customer material code", "Item text", "Material PO text", "Lead time (days)", "Customer portal ID" })
            Assert.Contains(heading, Headers(sheet));

        Assert.Equal("00010", At(sheet, "Line", 1).Text);
        Assert.Equal("VALVE 1 — Gate valve 2in class 1", At(sheet, "Description", 1).Text);
        Assert.Equal("TELEDYNE", At(sheet, "Manufacturer", 1).Text);
        Assert.Equal("MPN-1", At(sheet, "Part number", 1).Text);
        Assert.Equal(2.5m, Convert.ToDecimal(At(sheet, "Qty", 1).Value));
        Assert.Equal("PC", At(sheet, "Unit", 1).Text);
        Assert.Equal("Yes", At(sheet, "Quoting", 1).Text);
        Assert.Equal("10001", At(sheet, "Customer material code", 1).Text);

        Assert.Equal("EA", At(sheet, "Unit", 2).Text);
        Assert.Equal("No", At(sheet, "Quoting", 2).Text);
        Assert.Equal("Obsolete part", At(sheet, "Reason", 2).Text);
        Assert.Equal("Not decided", At(sheet, "Quoting", 3).Text);
    }

    [Fact]
    public void The_customers_own_columns_follow_ours_one_column_each()
    {
        var sheet = Read(LinesWorkbook.ForRfq(Rfq(3)));
        var headers = Headers(sheet);

        Assert.Equal(new[] { "Approved manufacturers", "Plant", "Material group" }, headers.TakeLast(3));
        Assert.Equal("TELEDYNE; EMERSON", At(sheet, "Approved manufacturers", 1).Text);
        Assert.Equal("Ras Tanura", At(sheet, "Plant", 1).Text);
        Assert.Equal("Abqaiq", At(sheet, "Plant", 2).Text);
        Assert.Equal("VALVES", At(sheet, "Material group", 2).Text);
        Assert.Equal("", At(sheet, "Plant", 3).Text);
    }

    [Fact]
    public void Unreadable_customer_columns_do_not_break_the_file()
    {
        var rfq = Rfq(1);
        rfq.Rfqitems[0].ExtraFields = "{not json";
        var sheet = Read(LinesWorkbook.ForRfq(rfq));
        Assert.Equal("MPN-1", At(sheet, "Part number", 1).Text);
    }

    [Fact]
    public void The_top_of_the_sheet_says_which_RFQ_and_customer_it_is()
    {
        var sheet = Read(LinesWorkbook.ForRfq(Rfq(3)));

        Assert.Equal("RFQ RFQ/2026 0042", sheet.Cells[1, 1].Text);
        var labels = Enumerable.Range(1, HeaderRow(sheet) - 1).ToDictionary(r => sheet.Cells[r, 1].Text, r => sheet.Cells[r, 2].Text);
        Assert.Equal("Saudi Aramco", labels["Customer"]);
        Assert.Equal("7000123456", labels["Customer reference"]);
        Assert.Equal("05 Oct 2026", labels["Closing date"]);
        Assert.Equal("3", labels["Lines"]);
    }

    [Fact]
    public void An_RFQ_with_no_lines_still_opens()
    {
        var sheet = Read(LinesWorkbook.ForRfq(Rfq(0)));
        Assert.Equal(HeaderRow(sheet), sheet.Dimension.End.Row);
    }

    [Fact]
    public void Customer_text_that_looks_like_a_formula_stays_text()
    {
        var rfq = Rfq(1);
        rfq.Rfqitems[0].ProductName = null;
        rfq.Rfqitems[0].ProductShortDescription = "=HYPERLINK(\"http://x\",\"click\")";
        var sheet = Read(LinesWorkbook.ForRfq(rfq));
        var cell = At(sheet, "Description", 1);

        Assert.True(string.IsNullOrEmpty(cell.Formula));
        Assert.Equal("=HYPERLINK(\"http://x\",\"click\")", cell.Text);
    }

    [Fact]
    public void The_file_is_named_after_the_RFQ()
    {
        Assert.Equal("RFQ-2026-0042-lines.xlsx", LinesWorkbook.RfqFileName(Rfq(0)));
        Assert.Equal("Lead-77-lines.xlsx", LinesWorkbook.LeadFileName(new LeadResponseDTO { Id = 77 }));
    }

    [Fact]
    public void A_lead_exports_every_line_field_and_the_customers_columns()
    {
        var lead = new LeadResponseDTO { Id = 77, Rfqno = "7000999", CustomerName = "SEC", BidClosingDate = new DateTime(2026, 10, 1) };
        lead.LeadItems.Add(new LeadItemResponseDTO
        {
            Id = 1, LineItemNo = "10", ProductShortName = "CABLE", ProductShortDescription = "Cu 4c 16mm",
            ManufacturerName = "ELSEWEDY", ManufacturerPartNumber = "C-16", Quantity = 500, UnitOfMeasure = "M",
            ItemMaterialCode = "5003100001", ExtraFields = new Dictionary<string, string> { ["Plant"] = "Qurayyah" },
        });
        lead.LeadItems.Add(new LeadItemResponseDTO { Id = 2, ProductShortName = "GLAND", Quantity = 0 });

        var sheet = Read(LinesWorkbook.ForLead(lead));

        Assert.Equal("Lead 77 · 7000999", sheet.Cells[1, 1].Text);
        Assert.DoesNotContain("Quoting", Headers(sheet));
        Assert.Equal("Plant", Headers(sheet).Last());
        Assert.Equal("CABLE — Cu 4c 16mm", At(sheet, "Description", 1).Text);
        Assert.Equal(500m, Convert.ToDecimal(At(sheet, "Qty", 1).Value));
        Assert.Equal("M", At(sheet, "Unit", 1).Text);
        Assert.Equal("5003100001", At(sheet, "Customer material code", 1).Text);
        Assert.Equal("Qurayyah", At(sheet, "Plant", 1).Text);

        // No line number on the document: position. Quantity never stated: blank, not 0.
        Assert.Equal("2", At(sheet, "Line", 2).Text);
        Assert.Null(At(sheet, "Qty", 2).Value);
        Assert.Equal("EA", At(sheet, "Unit", 2).Text);
    }

    /// <summary>
    /// PERF-09: "Download Excel" of the 1,500-line request took 27–71 s, almost none of it SQL.
    /// EPPlus measured every cell of every column to fit widths. Widths now come from the heading
    /// and the first lines, so a long value far down a column no longer sets its width.
    /// </summary>
    [Fact]
    public void Column_widths_come_from_the_heading_and_the_first_lines_only()
    {
        var rfq = Rfq(1_500);
        rfq.Rfqitems[1_399].NoQuoteReason = new string('W', 400);

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var bytes = LinesWorkbook.ForRfq(rfq);
        Console.WriteLine($"1,500-line workbook built in {watch.ElapsedMilliseconds} ms");
        var sheet = Read(bytes);

        var reason = Headers(sheet).IndexOf("Reason") + 1;
        var description = Headers(sheet).IndexOf("Description") + 1;
        Assert.True(sheet.Column(reason).Width < 30, $"Reason column width {sheet.Column(reason).Width}");
        Assert.True(sheet.Column(description).Width > 12, $"Description column width {sheet.Column(description).Width}");
        Assert.Equal(new string('W', 400), At(sheet, "Reason", 1_400).Text);
    }

    [Theory]
    [InlineData("CABLE", "Cu 4c 16mm", "CABLE — Cu 4c 16mm")]
    [InlineData("CIRCUIT BREAKER, MCCB", "CIRCUIT BREAKER, MCCB, 3P, 250A", "CIRCUIT BREAKER, MCCB, 3P, 250A")]
    [InlineData("GLAND", null, "GLAND")]
    [InlineData(null, "Gland 20mm", "Gland 20mm")]
    public void A_line_has_one_description_without_repeating_itself(string? name, string? description, string expected)
    {
        Assert.Equal(expected, LinesWorkbook.Describe(name, description));
    }
}
