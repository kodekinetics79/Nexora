using System.Text.Json;
using System.Text.RegularExpressions;
using ERP_RFQ_Automation.DTOs.Lead;
using ERP_RFQ_Automation.DTOs.RfqDTOs;
using ERP_RFQ_Automation.Models;
using OfficeOpenXml;

namespace ERP_RFQ_Automation.Services;

/// <summary>
/// A lead's or an RFQ's lines as one Excel sheet with every field Nexora holds for a line, then
/// every extra column the customer's own document carried, so the reader can analyse the whole
/// request without opening the app. Built from the already-scoped DTO, so a rep can only ever
/// export what they could already open.
/// </summary>
public static class LinesWorkbook
{
    public const string ContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    /// <summary>Excel refuses a cell longer than this.</summary>
    private const int MaxCellLength = 32_000;

    /// <summary>
    /// Lines whose text sets the column widths. EPPlus measures every cell it fits: fitting all
    /// 1,500 lines × 30+ columns of a bid list made "Download Excel" take 27–71 s when the data
    /// took ~3 s to read (PERF-09). The heading and the first lines are a fair sample of a column.
    /// </summary>
    internal const int AutoFitSampleRows = 200;

    private sealed record Column<T>(string Header, Func<T, object?> Value);

    private static readonly Column<LeadItemResponseDTO>[] LeadLineColumns =
    {
        new("Line", i => i.LineItemNo),
        new("Description", i => Describe(i.ProductShortName, i.ProductShortDescription)),
        new("Manufacturer", i => i.ManufacturerName),
        new("Part number", i => i.ManufacturerPartNumber),
        // A quantity the document never stated stays blank, as "Not stated" on screen, not 0.
        new("Qty", i => i.Quantity is > 0 ? i.Quantity : null),
        new("Unit", i => i.UnitOfMeasure),
        new("Unit price", i => i.UnitPrice),
        new("Currency", i => i.Currency),
        new("Customer RFQ #", i => i.CustomerRfqno),
        new("Customer material code", i => i.ItemMaterialCode),
        new("Commodity", i => i.CommodityProduct),
        new("Buyer", i => i.BuyerName),
        new("Alternative", i => i.Alternative),
        new("Alternate product", i => i.AlternateProductName),
        new("Alternate part number", i => i.AlternatePartNumber),
        new("Item text", i => i.ItemText),
        new("Material PO text", i => i.MaterialPotext),
        new("Lead time (days)", i => i.LeadTime),
        new("Line closing date", i => Day(i.BidClosingDateLine)),
        new("Storage location", i => i.StorageLocation),
        new("Company ref", i => i.CompanyRef),
        new("Customer portal ID", i => i.CustomerAccountPortalId),
    };

    private static readonly Column<RfqitemResponseDTO>[] RfqLineColumns =
    {
        new("Line", i => i.LineItemNo),
        new("Description", i => Describe(i.ProductName ?? i.ProductShortName, i.ProductShortDescription)),
        new("Manufacturer", i => i.ManufacturerName),
        new("Part number", i => i.ManufacturerPartNumber),
        new("Qty", i => i.Quantity),
        new("Unit", i => i.UnitOfMeasure),
        new("Quoting", i => Quoting(i.ParticipationDecision)),
        new("Reason", i => i.NoQuoteReason),
        new("Decided by", i => i.ParticipationDecidedBy),
        new("Decided on", i => Day(i.ParticipationDecidedOn)),
        new("Catalogue product", i => i.ProductName),
        new("In catalogue", i => i.ProductIsCatalogItem is null ? null : i.ProductIsCatalogItem.Value ? "Yes" : "No"),
        new("Product matched by", i => i.ProductResolvedBy),
        new("Product matched on", i => Day(i.ProductResolvedOn)),
        new("Why this product", i => i.ProductResolutionReason),
        new("Offered part number", i => i.OfferedPartNumber),
        new("Offered maker", i => i.OfferedMakerName),
        new("Offered as", i => i.OfferedKind),
        new("Offered note", i => i.OfferedNote),
        new("Offered specs", i => i.OfferedSpecs),
        new("Unit price", i => i.UnitPrice),
        new("Currency", i => i.Currency),
        new("Supplier", i => i.SupplierName),
        new("Warehouse", i => i.WarehouseName),
        new("Storage location", i => i.StorageLocation),
        new("Lead time (days)", i => i.LeadTime),
        new("Required by", i => Day(i.RequiredDesiredDate)),
        new("Line closing date", i => Day(i.BidClosingDateLine)),
        new("Customer RFQ #", i => i.CustomerRfqno),
        new("Customer material code", i => i.ItemMaterialCode),
        new("Commodity", i => i.CommodityProduct),
        new("Buyer", i => i.BuyerName),
        new("Alternative", i => i.Alternative),
        new("Alternate product", i => i.AlternateProductName),
        new("Alternate part number", i => i.AlternatePartNumber),
        new("Item text", i => i.ItemText),
        new("Material PO text", i => i.MaterialPotext),
        new("Company ref", i => i.CompanyRef),
        new("Customer portal ID", i => i.CustomerAccountPortalId),
    };

    public static byte[] ForRfq(RfqResponseDTO rfq) => Build(
        $"RFQ {rfq.Rfqno}",
        new[]
        {
            ("Customer", rfq.CustomerName ?? rfq.BuyersName),
            ("Customer reference", rfq.CustomerRfqReference),
            ("Buyer", rfq.BuyersName),
            ("Closing date", Day(rfq.BidClosingDate)),
            ("Required delivery", Day(rfq.RequiredDeliveryDate)),
            ("Delivery location", rfq.DeliveryLocation),
            ("Status", rfq.RfqstatusValue),
            ("Lines", rfq.Rfqitems.Count.ToString()),
        },
        RfqLineColumns,
        rfq.Rfqitems.Select(i => (i, ParseExtra(i.ExtraFields))).ToList());

    public static byte[] ForLead(LeadResponseDTO lead) => Build(
        string.IsNullOrWhiteSpace(lead.Rfqno) ? $"Lead {lead.Id}" : $"Lead {lead.Id} · {lead.Rfqno}",
        new[]
        {
            ("Customer", lead.CustomerName ?? lead.BuyersName),
            ("Customer reference", lead.Rfqno),
            ("Buyer", lead.BuyersName),
            ("Closing date", Day(lead.BidClosingDate)),
            ("Required delivery", Day(lead.RequiredDeliveryDate)),
            ("Delivery location", lead.DeliveryLocation),
            ("Lines", lead.LeadItems.Count.ToString()),
        },
        LeadLineColumns,
        lead.LeadItems.Select(i => (i, (IReadOnlyDictionary<string, string>?)i.ExtraFields)).ToList());

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

    /// <summary>The customer's own columns, stored per RFQ line as a JSON object. Unreadable JSON yields none.</summary>
    public static IReadOnlyDictionary<string, string>? ParseExtra(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in doc.RootElement.EnumerateObject())
                fields[p.Name] = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() ?? string.Empty : p.Value.GetRawText();
            return fields;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Day(DateTime? value) => value?.ToString("dd MMM yyyy");

    private static string FileName(string? number, string fallback)
    {
        var safe = Regex.Replace(number ?? string.Empty, @"[^A-Za-z0-9._-]+", "-").Trim('-');
        return $"{(safe.Length == 0 ? fallback : safe)}-lines.xlsx";
    }

    /// <summary>Every customer column in the order it first appears, so line 1's columns lead.</summary>
    private static List<string> ExtraHeaders(IEnumerable<IReadOnlyDictionary<string, string>?> lines)
    {
        var headers = new List<string>();
        foreach (var extra in lines)
            if (extra is not null)
                foreach (var key in extra.Keys)
                    if (!headers.Contains(key, StringComparer.OrdinalIgnoreCase)) headers.Add(key);
        return headers;
    }

    private static byte[] Build<T>(string title, (string Label, string? Value)[] header, Column<T>[] columns,
        IReadOnlyList<(T Line, IReadOnlyDictionary<string, string>? Extra)> lines)
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

        var extraHeaders = ExtraHeaders(lines.Select(l => l.Extra));
        var headers = columns.Select(c => c.Header).Concat(extraHeaders).ToArray();
        var headerRow = row;
        for (var c = 0; c < headers.Length; c++)
        {
            sheet.Cells[row, c + 1].Value = headers[c];
            sheet.Cells[row, c + 1].Style.Font.Bold = true;
        }
        row += 1;

        for (var i = 0; i < lines.Count; i++)
        {
            var (line, extra) = lines[i];
            for (var c = 0; c < columns.Length; c++) sheet.Cells[row, c + 1].Value = Cell(columns[c].Value(line));
            // The buyer's own line number, as on screen; the position only when the document had none.
            if (string.IsNullOrWhiteSpace(sheet.Cells[row, 1].Text)) sheet.Cells[row, 1].Value = (i + 1).ToString();
            if (string.IsNullOrWhiteSpace(sheet.Cells[row, 6].Text)) sheet.Cells[row, 6].Value = "EA";
            for (var e = 0; e < extraHeaders.Count; e++)
            {
                var key = extra?.Keys.FirstOrDefault(k => string.Equals(k, extraHeaders[e], StringComparison.OrdinalIgnoreCase));
                if (key is not null) sheet.Cells[row, columns.Length + e + 1].Value = Cell(extra![key]);
            }
            row += 1;
        }

        if (lines.Count > 0)
            sheet.Cells[headerRow, 1, row - 1, headers.Length].AutoFilter = true;
        sheet.View.FreezePanes(headerRow + 1, 3);
        sheet.Column(5).Style.Numberformat.Format = "#,##0.##";
        if (sheet.Dimension is not null)
            sheet.Cells[1, 1, Math.Min(sheet.Dimension.End.Row, headerRow + AutoFitSampleRows), sheet.Dimension.End.Column]
                .AutoFitColumns(8, 50);
        sheet.Column(2).Style.WrapText = true;

        return package.GetAsByteArray();
    }

    private static object? Cell(object? value) =>
        value is string text && text.Length > MaxCellLength ? text[..MaxCellLength] + "…" : value;
}
