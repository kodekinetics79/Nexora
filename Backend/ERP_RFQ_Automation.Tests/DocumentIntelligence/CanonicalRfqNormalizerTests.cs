using ERP_RFQ_Automation.DTOs.DocumentIntelligence;
using ERP_RFQ_Automation.Services.DocumentIntelligence;

namespace ERP_RFQ_Automation.Tests.DocumentIntelligence;

public class CanonicalRfqNormalizerTests
{
    private readonly CanonicalRfqNormalizer _normalizer = new();

    [Fact]
    public void NormalizeSpreadsheetRows_ValidRows_ProducesCanonicalDocumentWithEvidence()
    {
        var result = _normalizer.NormalizeSpreadsheetRows(new[]
        {
            new RfqSpreadsheetRow
            {
                RowNumber = 2,
                SourceDocumentName = "RFQTemplate",
                RfqNo = " RFQ-1001 ",
                BuyerName = "Global Industries",
                ReceivedDate = "2026-07-14",
                BidClosingDate = "2026-07-30",
                ProductName = "Contactor 220V 40A",
                Quantity = "5",
                UnitPrice = "120.50",
                Currency = "USD",
                ManufacturerName = "Schneider Electric",
                ManufacturerPartNumber = "LC1D40AM7",
                LeadTimeDays = "10"
            }
        }, 7);

        var document = Assert.Single(result.Documents);
        Assert.Equal("rfq-canonical/v1", document.SchemaVersion);
        Assert.Equal(7, document.BusinessUnitId);
        Assert.Equal("RFQ-1001", document.RfqNo.Value);
        Assert.Equal(ValidationStatus.Valid, document.ValidationStatus);
        Assert.Equal("row 2, column A", document.RfqNo.Evidence.Single().Location);
        Assert.Equal(5, document.LineItems.Single().Quantity.Value);
    }

    [Fact]
    public void NormalizeSpreadsheetRows_InvalidQuantity_BlocksDocument()
    {
        var result = _normalizer.NormalizeSpreadsheetRows(new[]
        {
            new RfqSpreadsheetRow
            {
                RowNumber = 2,
                RfqNo = "RFQ-1002",
                BuyerName = "Global Industries",
                ReceivedDate = "2026-07-14",
                ProductName = "Contactor",
                Quantity = "zero"
            }
        }, 7);

        var document = Assert.Single(result.Documents);
        Assert.Equal(ValidationStatus.Invalid, document.ValidationStatus);
        Assert.Contains(result.Issues, issue => issue.Code == "QUANTITY" && issue.Severity == ValidationSeverity.Error);
    }

    [Fact]
    public void NormalizeSpreadsheetRows_FractionalQuantity_PreservesExactDecimal()
    {
        var result = _normalizer.NormalizeSpreadsheetRows(new[]
        {
            new RfqSpreadsheetRow
            {
                RowNumber = 2,
                RfqNo = "RFQ-FRACTION-1",
                BuyerName = "Global Industries",
                ReceivedDate = "2026-07-14",
                ProductName = "Cut cable",
                Quantity = "2.750125",
                UnitOfMeasure = "M"
            }
        }, 7);

        var line = Assert.Single(Assert.Single(result.Documents).LineItems);
        Assert.Equal(2.750125m, line.Quantity.Value);
        Assert.Equal(CanonicalValueKind.Normalized, line.Quantity.Kind);
        Assert.Equal(ValidationStatus.Valid, line.Quantity.ValidationStatus);
    }

    [Fact]
    public void NormalizeSpreadsheetRows_DuplicateLines_FlagsNeedsReview()
    {
        var rows = new[]
        {
            BuildDuplicateRow(2),
            BuildDuplicateRow(3)
        };

        var result = _normalizer.NormalizeSpreadsheetRows(rows, 7);

        var document = Assert.Single(result.Documents);
        Assert.Equal(ValidationStatus.NeedsReview, document.ValidationStatus);
        Assert.Contains(result.Issues, issue => issue.Code == "DUPLICATE_LINE" && issue.Severity == ValidationSeverity.Warning);
    }

    private static RfqSpreadsheetRow BuildDuplicateRow(int rowNumber)
    {
        return new RfqSpreadsheetRow
        {
            RowNumber = rowNumber,
            RfqNo = "RFQ-1003",
            BuyerName = "Global Industries",
            ReceivedDate = "2026-07-14",
            ProductName = "Contactor",
            Quantity = "5",
            UnitPrice = "120.50",
            Currency = "USD",
            ManufacturerPartNumber = "LC1D40AM7"
        };
    }
}
