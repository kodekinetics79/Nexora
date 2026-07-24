namespace ERP_RFQ_Automation.Services.DocumentIntelligence;

public sealed class RfqSpreadsheetRow
{
    public int RowNumber { get; set; }
    public string SourceDocumentName { get; set; } = "RFQ spreadsheet";
    public string WorksheetName { get; set; } = "CSV";
    public int HeaderRowNumber { get; set; } = 1;
    public Dictionary<int, string> HeadersByColumn { get; set; } = new();
    public Dictionary<string, int> FieldColumnNumbers { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> FieldSourceAddresses { get; set; } = new(StringComparer.Ordinal);
    public string? RfqNo { get; set; }
    public string? BuyerName { get; set; }
    public string? ReceivedDate { get; set; }
    public string? BidClosingDate { get; set; }
    public string? ProductName { get; set; }
    public string? Quantity { get; set; }
    public string? UnitPrice { get; set; }
    public string? Currency { get; set; }
    public string? ManufacturerName { get; set; }
    public string? ManufacturerPartNumber { get; set; }
    public string? LeadTimeDays { get; set; }

    public string SourceAddress(string fieldName, string legacyColumn)
    {
        if (FieldSourceAddresses.TryGetValue(fieldName, out var address))
            return address;

        return legacyColumn == "row"
            ? $"row {RowNumber}"
            : $"row {RowNumber}, column {legacyColumn}";
    }
}

public static class RfqSpreadsheetFields
{
    public const string RfqNo = nameof(RfqSpreadsheetRow.RfqNo);
    public const string BuyerName = nameof(RfqSpreadsheetRow.BuyerName);
    public const string ReceivedDate = nameof(RfqSpreadsheetRow.ReceivedDate);
    public const string BidClosingDate = nameof(RfqSpreadsheetRow.BidClosingDate);
    public const string ProductName = nameof(RfqSpreadsheetRow.ProductName);
    public const string Quantity = nameof(RfqSpreadsheetRow.Quantity);
    public const string UnitPrice = nameof(RfqSpreadsheetRow.UnitPrice);
    public const string Currency = nameof(RfqSpreadsheetRow.Currency);
    public const string ManufacturerName = nameof(RfqSpreadsheetRow.ManufacturerName);
    public const string ManufacturerPartNumber = nameof(RfqSpreadsheetRow.ManufacturerPartNumber);
    public const string LeadTimeDays = nameof(RfqSpreadsheetRow.LeadTimeDays);
}

public sealed class CanonicalRfqImportResult
{
    public List<DTOs.DocumentIntelligence.CanonicalRfqDocument> Documents { get; set; } = new();
    public List<DTOs.DocumentIntelligence.CanonicalValidationIssue> Issues { get; set; } = new();
}
