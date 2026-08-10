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

    /// <summary>
    /// The buyer's own unit word, verbatim. Spreadsheets were previously parsed with no unit
    /// column at all, so a line reading "500 M" of cable was ingested as a bare 500 and quoted
    /// as 500 each. The column is read here and canonicalised downstream; it is never defaulted.
    /// </summary>
    public string? UnitOfMeasure { get; set; }

    public string? UnitPrice { get; set; }
    public string? Currency { get; set; }
    public string? ManufacturerName { get; set; }
    public string? ManufacturerPartNumber { get; set; }
    public string? LeadTimeDays { get; set; }

    /// <summary>FR-RFQ-04. Saudi region or city for delivery, in the buyer's own wording.</summary>
    public string? DeliveryLocation { get; set; }

    /// <summary>FR-RFQ-04. The delivery date the buyer is asking for — never a supplier lead time.</summary>
    public string? RequiredDeliveryDate { get; set; }

    /// <summary>FR-RFQ-03. Standing agreement / frame contract this inquiry calls off against.</summary>
    public string? AgreementReference { get; set; }

    /// <summary>
    /// The buyer's own note against the line ("OEM only", "Urgent requirement", "Equivalent
    /// accepted"). Commercially load-bearing — it changes what may be quoted — and was
    /// previously read from no format at all, so it was dropped with no diagnostic.
    /// </summary>
    public string? ItemText { get; set; }

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
    public const string UnitOfMeasure = nameof(RfqSpreadsheetRow.UnitOfMeasure);
    public const string UnitPrice = nameof(RfqSpreadsheetRow.UnitPrice);
    public const string Currency = nameof(RfqSpreadsheetRow.Currency);
    public const string ManufacturerName = nameof(RfqSpreadsheetRow.ManufacturerName);
    public const string ManufacturerPartNumber = nameof(RfqSpreadsheetRow.ManufacturerPartNumber);
    public const string LeadTimeDays = nameof(RfqSpreadsheetRow.LeadTimeDays);
    public const string ItemText = nameof(RfqSpreadsheetRow.ItemText);
    public const string DeliveryLocation = nameof(RfqSpreadsheetRow.DeliveryLocation);
    public const string RequiredDeliveryDate = nameof(RfqSpreadsheetRow.RequiredDeliveryDate);
    public const string AgreementReference = nameof(RfqSpreadsheetRow.AgreementReference);
}

public sealed class CanonicalRfqImportResult
{
    public List<DTOs.DocumentIntelligence.CanonicalRfqDocument> Documents { get; set; } = new();
    public List<DTOs.DocumentIntelligence.CanonicalValidationIssue> Issues { get; set; } = new();
}
