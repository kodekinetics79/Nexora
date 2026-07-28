using System.Text;
using ERP_RFQ_Automation.Services.DocumentIntelligence;
using OfficeOpenXml;

namespace ERP_RFQ_Automation.Tests.DocumentIntelligence;

public class NativeSpreadsheetParserTests
{
    private readonly NativeSpreadsheetParser _parser = new();

    [Fact]
    public void ParseCsv_QuotedMultilineField_PreservesPhysicalRowsAndCellAddresses()
    {
        const string csv = "Quantity,Description,RFQ,Buyer Name\r\n" +
                           "5,\"Control panel,\r\nwith enclosure\",RFQ-10,Acme\r\n" +
                           "2,Motor,RFQ-11,Globex\r\n";

        var rows = _parser.ParseCsv(Encoding.UTF8.GetBytes(csv), "requests.csv");

        Assert.Equal(2, rows.Count);
        Assert.Equal("Control panel,\r\nwith enclosure", rows[0].ProductName);
        Assert.Equal(2, rows[0].RowNumber);
        Assert.Equal(4, rows[1].RowNumber);
        Assert.Equal("CSV", rows[0].WorksheetName);
        Assert.Equal("Description", rows[0].HeadersByColumn[2]);
        Assert.Equal(2, rows[0].FieldColumnNumbers[RfqSpreadsheetFields.ProductName]);
        Assert.Equal("'CSV'!B2", rows[0].FieldSourceAddresses[RfqSpreadsheetFields.ProductName]);
        Assert.Equal("'CSV'!C4", rows[1].FieldSourceAddresses[RfqSpreadsheetFields.RfqNo]);
    }

    [Fact]
    public void ParseXlsx_ReorderedColumnsAndMultipleWorksheets_PreserveNativeCoordinates()
    {
        var bytes = BuildWorkbook(package =>
        {
            var primary = package.Workbook.Worksheets.Add("Primary RFQs");
            primary.Cells[3, 2].Value = "Description";
            primary.Cells[3, 4].Value = "Qty";
            primary.Cells[3, 6].Value = "RFQ No";
            primary.Cells[3, 7].Value = "Buyer";
            primary.Cells[4, 2].Value = "Valve";
            primary.Cells[4, 4].Value = "7";
            primary.Cells[4, 6].Value = "RFQ-20";
            primary.Cells[4, 7].Value = "Acme";

            var secondary = package.Workbook.Worksheets.Add("O'Brien");
            secondary.Cells[1, 1].Value = "Buyer Name";
            secondary.Cells[1, 2].Value = "RFQ";
            secondary.Cells[1, 3].Value = "Product Name";
            secondary.Cells[1, 4].Value = "Quantity";
            secondary.Cells[2, 1].Value = "Globex";
            secondary.Cells[2, 2].Value = "RFQ-21";
            secondary.Cells[2, 3].Value = "Motor";
            secondary.Cells[2, 4].Value = "3";
        });

        var rows = _parser.ParseXlsx(bytes, "requests.xlsx");

        Assert.Equal(2, rows.Count);
        var primary = Assert.Single(rows, row => row.WorksheetName == "Primary RFQs");
        Assert.Equal(3, primary.HeaderRowNumber);
        Assert.Equal(4, primary.RowNumber);
        Assert.Equal(6, primary.FieldColumnNumbers[RfqSpreadsheetFields.RfqNo]);
        Assert.Equal("'Primary RFQs'!F4", primary.FieldSourceAddresses[RfqSpreadsheetFields.RfqNo]);
        Assert.Equal("'Primary RFQs'!B4", primary.FieldSourceAddresses[RfqSpreadsheetFields.ProductName]);

        var secondary = Assert.Single(rows, row => row.WorksheetName == "O'Brien");
        Assert.Equal("'O''Brien'!C2", secondary.FieldSourceAddresses[RfqSpreadsheetFields.ProductName]);
    }

    [Fact]
    public void ParseXls_LegacyBiffWorkbook_PreservesMappedValuesAndCoordinates()
    {
        var bytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "legacy-rfq.xls"));

        var row = Assert.Single(_parser.ParseXls(bytes, "legacy-rfq.xls"));

        Assert.Equal("RFQ-XLS-1", row.RfqNo);
        Assert.Equal("Acme", row.BuyerName);
        Assert.Equal("Legacy pump", row.ProductName);
        Assert.Equal("12", row.Quantity);
        Assert.Equal("P-100", row.ManufacturerPartNumber);
        Assert.Equal("'RFQ'!A2", row.FieldSourceAddresses[RfqSpreadsheetFields.RfqNo]);
        Assert.Equal("'RFQ'!E2", row.FieldSourceAddresses[RfqSpreadsheetFields.ManufacturerPartNumber]);
    }

    [Fact]
    public void Normalizer_UsesParserNativeAddressesForEveryMappedField()
    {
        const string csv = "Buyer,Quantity,RFQ,Received Date,Description,Unit Price,Currency\n" +
                           "Acme,5,RFQ-30,2026-07-23,Valve,12.50,USD\n";
        var row = Assert.Single(_parser.ParseCsv(Encoding.UTF8.GetBytes(csv), "rfq.csv"));

        var document = Assert.Single(new CanonicalRfqNormalizer()
            .NormalizeSpreadsheetRows(new[] { row }, businessUnitId: 9)
            .Documents);

        Assert.Equal("'CSV'!C2", document.RfqNo.Evidence.Single().Location);
        Assert.Equal("'CSV'!A2", document.BuyerName.Evidence.Single().Location);
        Assert.Equal("'CSV'!D2", document.ReceivedDate.Evidence.Single().Location);
        Assert.Equal("'CSV'!E2", document.LineItems.Single().ProductName.Evidence.Single().Location);
        Assert.Equal("'CSV'!B2", document.LineItems.Single().Quantity.Evidence.Single().Location);
        Assert.Equal("'CSV'!F2", document.LineItems.Single().UnitPrice.Evidence.Single().Location);
        Assert.Equal("'CSV'!G2", document.LineItems.Single().Currency.Evidence.Single().Location);
    }

    private static byte[] BuildWorkbook(Action<ExcelPackage> populate)
    {
        ExcelPackage.LicenseContext = LicenseContext.NonCommercial;
        using var package = new ExcelPackage();
        populate(package);
        return package.GetAsByteArray();
    }
}
