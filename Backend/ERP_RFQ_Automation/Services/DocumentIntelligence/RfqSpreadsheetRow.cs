namespace ERP_RFQ_Automation.Services.DocumentIntelligence;

public sealed class RfqSpreadsheetRow
{
    public int RowNumber { get; set; }
    public string SourceDocumentName { get; set; } = "RFQ spreadsheet";
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
}

public sealed class CanonicalRfqImportResult
{
    public List<DTOs.DocumentIntelligence.CanonicalRfqDocument> Documents { get; set; } = new();
    public List<DTOs.DocumentIntelligence.CanonicalValidationIssue> Issues { get; set; } = new();
}
