using System.Text.RegularExpressions;
using ERP_RFQ_Automation.DTOs.RfqDTOs;
using ERP_RFQ_Automation.Models;
using OfficeOpenXml;

namespace ERP_RFQ_Automation.Services;

/// <summary>
/// The RFQ's lines as one Excel sheet: the same columns the rep sees on the RFQ screen, so the
/// file needs no explanation. Built from the scoped <see cref="RfqResponseDTO"/>, so a rep can
/// only ever export an RFQ they could already open.
/// </summary>
public static class RfqLinesWorkbook
{
    public const string ContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    public static readonly string[] Columns =
        { "Line", "Item", "Description", "Manufacturer", "Part number", "Qty", "Unit", "Quoting", "Reason" };

    public static string FileName(RfqResponseDTO rfq)
    {
        var safe = Regex.Replace(rfq.Rfqno ?? $"RFQ-{rfq.Id}", @"[^A-Za-z0-9._-]+", "-").Trim('-');
        return $"{(safe.Length == 0 ? $"RFQ-{rfq.Id}" : safe)}-lines.xlsx";
    }

    public static byte[] Build(RfqResponseDTO rfq)
    {
        ExcelPackage.LicenseContext = LicenseContext.NonCommercial;

        using var package = new ExcelPackage();
        var sheet = package.Workbook.Worksheets.Add("Lines");
        var row = 1;

        sheet.Cells[row, 1].Value = $"RFQ {rfq.Rfqno}";
        sheet.Cells[row, 1].Style.Font.Bold = true;
        sheet.Cells[row, 1].Style.Font.Size = 14;
        row += 1;
        row = Header(sheet, row, "Customer", rfq.CustomerName ?? rfq.BuyersName);
        row = Header(sheet, row, "Customer reference", rfq.CustomerRfqReference);
        row = Header(sheet, row, "Closing date", rfq.BidClosingDate?.ToString("dd MMM yyyy"));
        row = Header(sheet, row, "Lines", rfq.Rfqitems.Count.ToString());
        row += 1;

        var headerRow = row;
        for (var c = 0; c < Columns.Length; c++)
        {
            sheet.Cells[row, c + 1].Value = Columns[c];
            sheet.Cells[row, c + 1].Style.Font.Bold = true;
        }
        row += 1;

        var position = 0;
        foreach (var item in rfq.Rfqitems)
        {
            position += 1;
            // The buyer's own line number, as on screen; the position only when the document had none.
            sheet.Cells[row, 1].Value = string.IsNullOrWhiteSpace(item.LineItemNo) ? position.ToString() : item.LineItemNo;
            sheet.Cells[row, 2].Value = item.ProductName ?? item.ProductShortName;
            sheet.Cells[row, 3].Value = item.ProductShortDescription;
            sheet.Cells[row, 4].Value = item.ManufacturerName;
            sheet.Cells[row, 5].Value = item.ManufacturerPartNumber;
            sheet.Cells[row, 6].Value = item.Quantity;
            sheet.Cells[row, 7].Value = string.IsNullOrWhiteSpace(item.UnitOfMeasure) ? "EA" : item.UnitOfMeasure;
            sheet.Cells[row, 8].Value = Quoting(item.ParticipationDecision);
            sheet.Cells[row, 9].Value = item.NoQuoteReason;
            row += 1;
        }

        if (rfq.Rfqitems.Count > 0)
            sheet.Cells[headerRow, 1, row - 1, Columns.Length].AutoFilter = true;
        sheet.View.FreezePanes(headerRow + 1, 1);
        sheet.Column(6).Style.Numberformat.Format = "#,##0.##";
        if (sheet.Dimension is not null) sheet.Cells[sheet.Dimension.Address].AutoFitColumns(8, 60);
        sheet.Column(3).Style.WrapText = true;

        return package.GetAsByteArray();
    }

    public static string Quoting(string? decision) => decision switch
    {
        Rfqitem.ParticipationQuote => "Yes",
        Rfqitem.ParticipationNoQuote => "No",
        _ => "Not decided"
    };

    private static int Header(ExcelWorksheet sheet, int row, string label, string? value)
    {
        sheet.Cells[row, 1].Value = label;
        sheet.Cells[row, 1].Style.Font.Bold = true;
        sheet.Cells[row, 2].Value = string.IsNullOrWhiteSpace(value) ? "—" : value;
        return row + 1;
    }
}
