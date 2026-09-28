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
                ParticipationDecision = i == 1 ? Rfqitem.ParticipationQuote
                    : i == 2 ? Rfqitem.ParticipationNoQuote : Rfqitem.ParticipationPending,
                NoQuoteReason = i == 2 ? "Obsolete part" : null,
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

    [Fact]
    public void Every_line_is_one_row_in_the_on_screen_columns()
    {
        var sheet = Read(LinesWorkbook.ForRfq(Rfq(100)));
        var header = HeaderRow(sheet);

        for (var c = 0; c < LinesWorkbook.RfqColumns.Length; c++)
            Assert.Equal(LinesWorkbook.RfqColumns[c], sheet.Cells[header, c + 1].Text);
        Assert.Equal(header + 100, sheet.Dimension.End.Row);

        var first = header + 1;
        Assert.Equal("00010", sheet.Cells[first, 1].Text);
        Assert.Equal("VALVE 1 — Gate valve 2in class 1", sheet.Cells[first, 2].Text);
        Assert.Equal("TELEDYNE", sheet.Cells[first, 3].Text);
        Assert.Equal("MPN-1", sheet.Cells[first, 4].Text);
        Assert.Equal(2.5m, Convert.ToDecimal(sheet.Cells[first, 5].Value));
        Assert.Equal("PC", sheet.Cells[first, 6].Text);
        Assert.Equal("Yes", sheet.Cells[first, 7].Text);

        Assert.Equal("EA", sheet.Cells[first + 1, 6].Text);
        Assert.Equal("No", sheet.Cells[first + 1, 7].Text);
        Assert.Equal("Obsolete part", sheet.Cells[first + 1, 8].Text);
        Assert.Equal("Not decided", sheet.Cells[first + 2, 7].Text);
    }

    [Fact]
    public void The_top_of_the_sheet_says_which_RFQ_and_customer_it_is()
    {
        var sheet = Read(LinesWorkbook.ForRfq(Rfq(3)));

        Assert.Equal("RFQ RFQ/2026 0042", sheet.Cells[1, 1].Text);
        var labels = Enumerable.Range(1, HeaderRow(sheet)).ToDictionary(r => sheet.Cells[r, 1].Text, r => sheet.Cells[r, 2].Text);
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
        var cell = sheet.Cells[HeaderRow(sheet) + 1, 2];

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
    public void A_lead_exports_the_same_columns_without_the_quote_decision()
    {
        var lead = new LeadResponseDTO { Id = 77, Rfqno = "7000999", CustomerName = "SEC", BidClosingDate = new DateTime(2026, 10, 1) };
        lead.LeadItems.Add(new LeadItemResponseDTO { Id = 1, LineItemNo = "10", ProductShortName = "CABLE", ProductShortDescription = "Cu 4c 16mm", ManufacturerName = "ELSEWEDY", ManufacturerPartNumber = "C-16", Quantity = 500, UnitOfMeasure = "M" });
        lead.LeadItems.Add(new LeadItemResponseDTO { Id = 2, ProductShortName = "GLAND", Quantity = 0 });

        var sheet = Read(LinesWorkbook.ForLead(lead));
        var header = HeaderRow(sheet);

        Assert.Equal("Lead 77 · 7000999", sheet.Cells[1, 1].Text);
        for (var c = 0; c < LinesWorkbook.LeadColumns.Length; c++)
            Assert.Equal(LinesWorkbook.LeadColumns[c], sheet.Cells[header, c + 1].Text);
        Assert.Equal("", sheet.Cells[header, LinesWorkbook.LeadColumns.Length + 1].Text);

        Assert.Equal("10", sheet.Cells[header + 1, 1].Text);
        Assert.Equal("CABLE — Cu 4c 16mm", sheet.Cells[header + 1, 2].Text);
        Assert.Equal(500m, Convert.ToDecimal(sheet.Cells[header + 1, 5].Value));
        Assert.Equal("M", sheet.Cells[header + 1, 6].Text);

        // No line number on the document: position. Quantity never stated: blank, not 0.
        Assert.Equal("2", sheet.Cells[header + 2, 1].Text);
        Assert.Null(sheet.Cells[header + 2, 5].Value);
        Assert.Equal("EA", sheet.Cells[header + 2, 6].Text);
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
