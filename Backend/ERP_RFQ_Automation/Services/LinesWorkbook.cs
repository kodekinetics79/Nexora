using System.Text.RegularExpressions;
using ERP_RFQ_Automation.DTOs.Lead;
using ERP_RFQ_Automation.DTOs.RfqDTOs;
using ERP_RFQ_Automation.Models;
using OfficeOpenXml;

namespace ERP_RFQ_Automation.Services;

/// <summary>
/// A lead's or an RFQ's lines as one Excel sheet, in the columns the rep sees on screen, so the
/// file needs no explanation. Built from the already-scoped DTO, so a rep can only ever export
/// what they could already open.
/// </summary>
public static class LinesWorkbook
{
    public const string ContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    public static readonly string[] LeadColumns =
        { "Line", "Description", "Manufacturer", "Part number", "Qty", "Unit" };

    /// <summary>An RFQ also carries the quote-or-skip decision per line.</summary>
    public static readonly string[] RfqColumns = LeadColumns.Concat(new[] { "Quoting", "Reason" }).ToArray();

    public static byte[] ForRfq(RfqResponseDTO rfq) => Build(
        $"RFQ {rfq.Rfqno}",
        new[]
        {
            ("Customer", rfq.CustomerName ?? rfq.BuyersName),
            ("Customer reference", rfq.CustomerRfqReference),
            ("Closing date", rfq.BidClosingDate?.ToString("dd MMM yyyy")),
            ("Lines", rfq.Rfqitems.Count.ToString()),
        },
        RfqColumns,
        rfq.Rfqitems.Select(item => new object?[]
        {
            item.LineItemNo, Describe(item.ProductName ?? item.ProductShortName, item.ProductShortDescription),
            item.ManufacturerName, item.ManufacturerPartNumber, item.Quantity, item.UnitOfMeasure,
            Quoting(item.ParticipationDecision), item.NoQuoteReason,
        }).ToList());

    public static byte[] ForLead(LeadResponseDTO lead) => Build(
        string.IsNullOrWhiteSpace(lead.Rfqno) ? $"Lead {lead.Id}" : $"Lead {lead.Id} · {lead.Rfqno}",
        new[]
        {
            ("Customer", lead.CustomerName ?? lead.BuyersName),
            ("Customer reference", lead.Rfqno),
            ("Closing date", lead.BidClosingDate?.ToString("dd MMM yyyy")),
            ("Lines", lead.LeadItems.Count.ToString()),
        },
        LeadColumns,
        lead.LeadItems.Select(item => new object?[]
        {
            item.LineItemNo, Describe(item.ProductShortName, item.ProductShortDescription),
            item.ManufacturerName, item.ManufacturerPartNumber,
            // A quantity the document never stated stays blank, as "Not stated" on screen, not 0.
            item.Quantity is > 0 ? item.Quantity : null, item.UnitOfMeasure,
        }).ToList());

    public static string RfqFileName(RfqResponseDTO rfq) => FileName(rfq.Rfqno, $"RFQ-{rfq.Id}");

    public static string LeadFileName(LeadResponseDTO lead) => FileName(lead.Rfqno, $"Lead-{lead.Id}");

    /// <summary>One description per line: the name, plus the longer text only when it adds something.</summary>
    public static string? Describe(string? name, string? description)
    {
        if (string.IsNullOrWhiteSpace(description)) return name;
        if (string.IsNullOrWhiteSpace(name) || description.Trim().StartsWith(name.Trim(), StringComparison.OrdinalIgnoreCase))
            return description;
        return $"{name} — {description}";
    }

    public static string Quoting(string? decision) => decision switch
    {
        Rfqitem.ParticipationQuote => "Yes",
        Rfqitem.ParticipationNoQuote => "No",
        _ => "Not decided"
    };

    private static string FileName(string? number, string fallback)
    {
        var safe = Regex.Replace(number ?? string.Empty, @"[^A-Za-z0-9._-]+", "-").Trim('-');
        return $"{(safe.Length == 0 ? fallback : safe)}-lines.xlsx";
    }

    private static byte[] Build(string title, (string Label, string? Value)[] header, string[] columns,
        IReadOnlyList<object?[]> lines)
    {
        ExcelPackage.LicenseContext = LicenseContext.NonCommercial;

        using var package = new ExcelPackage();
        var sheet = package.Workbook.Worksheets.Add("Lines");
        var row = 1;

        sheet.Cells[row, 1].Value = title;
        sheet.Cells[row, 1].Style.Font.Bold = true;
        sheet.Cells[row, 1].Style.Font.Size = 14;
        row += 1;
        foreach (var (label, value) in header)
        {
            sheet.Cells[row, 1].Value = label;
            sheet.Cells[row, 1].Style.Font.Bold = true;
            sheet.Cells[row, 2].Value = string.IsNullOrWhiteSpace(value) ? "—" : value;
            row += 1;
        }
        row += 1;

        var headerRow = row;
        for (var c = 0; c < columns.Length; c++)
        {
            sheet.Cells[row, c + 1].Value = columns[c];
            sheet.Cells[row, c + 1].Style.Font.Bold = true;
        }
        row += 1;

        for (var i = 0; i < lines.Count; i++)
        {
            var values = lines[i];
            // The buyer's own line number, as on screen; the position only when the document had none.
            sheet.Cells[row, 1].Value = values[0] is string no && !string.IsNullOrWhiteSpace(no) ? no : (i + 1).ToString();
            for (var c = 1; c < columns.Length; c++) sheet.Cells[row, c + 1].Value = values[c];
            if (values[5] is not string unit || string.IsNullOrWhiteSpace(unit)) sheet.Cells[row, 6].Value = "EA";
            row += 1;
        }

        if (lines.Count > 0)
            sheet.Cells[headerRow, 1, row - 1, columns.Length].AutoFilter = true;
        sheet.View.FreezePanes(headerRow + 1, 1);
        sheet.Column(5).Style.Numberformat.Format = "#,##0.##";
        if (sheet.Dimension is not null) sheet.Cells[sheet.Dimension.Address].AutoFitColumns(8, 60);
        sheet.Column(2).Style.WrapText = true;

        return package.GetAsByteArray();
    }
}
