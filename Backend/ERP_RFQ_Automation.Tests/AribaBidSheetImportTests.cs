using ERP_RFQ_Automation.DocumentIntelligence.Persistence;
using ERP_RFQ_Automation.DTOs.DocumentIntelligence;
using ERP_RFQ_Automation.Services.DocumentIntelligence;
using OfficeOpenXml;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// XS-15 (P0): SAP Ariba's own Excel bid sheet — the file Aramco hands a bidder
/// (<c>ARAMCO Enquiry 6031.xlsx</c>, <c>Aramco_4203207346_LAMP CADMIUM.xlsx</c>) — dead-lettered after
/// five attempts with "Currency code must contain three upper-case ASCII letters". Ariba prints
/// help rows under its header, one of which says "Currency" under Currency; that row passed as a
/// line and ONE bad cell killed the whole document. The workbook below reproduces the real sheet's
/// top rows cell for cell (headers, the "Help And Options" row, the help row, the blank row).
/// </summary>
public sealed class AribaBidSheetImportTests
{
    private static readonly string[] Headers =
    {
        "Number", "Name", "Description", "Intend To Respond", "Reason for not bidding", "Currency",
        "Unit of Measure", "* Price", "Quantity", "* Lead Time (In Days)", "Material Number",
        "Material Type", "Manufacturing Part Text", "Material PO Text", "Hazardous Indicator",
    };

    private static readonly string?[] HelpOptionsRow = { "Help And Options. Click on the + sign on the left to expand." };

    private static readonly string?[] HelpRow =
    {
        "Hierarchical, unique number of the section, lot, or question", "Question, item, or lot name",
        "Item or lot description", "Intend To Respond", "Reason for not bidding", "Currency", "Unit of Measure",
    };

    private static string?[] Line(string number, string name, string quantity, string material, string partText) => new[]
    {
        number, name, name, "No", null, "USD", "each", null, quantity, "", material, "9CAT", partText, name, "No",
    };

    private const string TwoApprovedMakers =
        "4000019054 - 0060003313 - ## SIEMENS AG  AUTOMATION AND DRIVE - DE\n000000005001915323 - 10004620 - ## SIEMENS AG  AUTOMATION AND DRIVE - DE\n" +
        "PART_NUMBER - 6ES7414-5HM06-0AB0\n\n" +
        "4000019055 - 0060003314 - $$ SIEMENS ENERGY GLOBAL GMBH - DE\n000000005001915324 - 10004621 - $$ SIEMENS ENERGY GLOBAL GMBH - DE\n" +
        "PART_NUMBER - 6ES7414-5HM06-0AB0";

    private const string OneApprovedMaker =
        "4000651915 - 0060000235 - BENTLY-NEVADA LLC - US\n000000005002431810 - 10030469 - BENTLY-NEVADA LLC - US\n" +
        "PART_NUMBER - 3500/33-01-00";

    private static byte[] AribaWorkbook()
    {
        ExcelPackage.LicenseContext = LicenseContext.NonCommercial;
        using var package = new ExcelPackage();
        var sheet = package.Workbook.Worksheets.Add("Sheet1");
        var rows = new List<string?[]>
        {
            Headers, HelpOptionsRow, HelpRow, new string?[] { "" },
            Line("8", "MODULE: WESTERM D20 K11", "1", "000000002000162253", OneApprovedMaker),
            Line("9", "MODULE; central processing unit", "2", "000000002000008806", TwoApprovedMakers),
            Line("10", "MONITOR 18 TO 36 VDC / 24 W", "1", "000000002000168942", OneApprovedMaker),
        };
        for (var r = 0; r < rows.Count; r++)
            for (var c = 0; c < rows[r].Length; c++)
                if (rows[r][c] is { } value) sheet.Cells[r + 1, c + 1].Value = value;
        return package.GetAsByteArray();
    }

    private static IReadOnlyList<RfqSpreadsheetRow> Parse()
        => new NativeSpreadsheetParser().ParseXlsx(AribaWorkbook(), "ARAMCO Enquiry 6031.xlsx");

    [Fact]
    public void The_help_rows_under_the_header_are_not_lines()
    {
        var rows = Parse();

        Assert.Equal(new[] { "MODULE: WESTERM D20 K11", "MODULE; central processing unit", "MONITOR 18 TO 36 VDC / 24 W" },
            rows.Select(r => r.ProductName));
        Assert.DoesNotContain(rows, r => r.Currency == "Currency");
    }

    [Fact]
    public void Ariba_numbers_are_the_line_numbers_not_part_numbers()
    {
        // With the help row read as a line, its prose under "Number" stopped the column reading as
        // line numbers, and every line took its line number as its maker part number.
        var rows = Parse();

        Assert.Equal(new[] { "8", "9", "10" }, rows.Select(r => r.CustomerLineNumber));
        Assert.DoesNotContain(rows, r => r.ManufacturerPartNumber is "8" or "9" or "10");
    }

    [Fact]
    public void The_sheet_reaches_the_evidence_ledger_without_a_bad_currency()
    {
        // The entry the worker uses after reading: normalise, then write each line to the ledger.
        // Before the fix the help row became a line with currency "Currency" and Enrich threw here.
        var document = Assert.Single(new CanonicalRfqNormalizer().NormalizeSpreadsheetRows(Parse(), businessUnitId: 7).Documents);

        Assert.Equal(3, document.LineItems.Count);
        foreach (var line in document.LineItems)
        {
            Assert.Equal("USD", line.Currency.Value);
            var ledger = CanonicalLineItem.Create(7, 1, 1, line.ProductName.Value!, line.Quantity.Value, line.UnitOfMeasure.Value);
            ledger.Enrich(line.ManufacturerName.Value, line.ManufacturerPartNumber.Value, line.Currency.Value,
                null, null, "{}", CanonicalValidationStatus.Valid);
        }
    }

    [Fact]
    public void Line_separated_vendor_records_in_the_sheet_give_the_maker_and_part_number()
    {
        var lines = Assert.Single(new CanonicalRfqNormalizer().NormalizeSpreadsheetRows(Parse(), businessUnitId: 7).Documents).LineItems;

        // One approved maker: the maker and its part number are the line's.
        Assert.Equal("BENTLY-NEVADA LLC", lines[0].ManufacturerName.Value);
        Assert.Equal("3500/33-01-00", lines[0].ManufacturerPartNumber.Value);

        // Two approved makers: the field stays blank on purpose, the list travels with the line,
        // flags and padding gone, and the number both vendors state is the part number.
        Assert.Null(lines[1].ManufacturerName.Value);
        Assert.StartsWith("SIEMENS AG AUTOMATION AND DRIVE (DE)", lines[1].ExtraFields!["Approved manufacturers"]);
        Assert.Contains("SIEMENS ENERGY GLOBAL GMBH (DE)", lines[1].ExtraFields!["Approved manufacturers"]);
        Assert.DoesNotContain("$$", lines[1].ExtraFields!["Approved manufacturers"]);
        Assert.Equal("6ES7414-5HM06-0AB0", lines[1].ManufacturerPartNumber.Value);
    }

    [Theory]
    [InlineData("Currency")]
    [InlineData("TBD")]
    [InlineData("US Dollars and Cents")]
    public void An_unknown_currency_holds_the_line_for_review_instead_of_killing_the_document(string word)
    {
        var row = new RfqSpreadsheetRow
        {
            RowNumber = 5, SourceDocumentName = "bid.xlsx", ProductName = "Relay", Quantity = "2", Currency = word,
        };
        var line = Assert.Single(Assert.Single(new CanonicalRfqNormalizer().NormalizeSpreadsheetRows(new[] { row }, 7).Documents).LineItems);

        Assert.Null(line.Currency.Value);
        Assert.Equal(ValidationStatus.NeedsReview, line.ValidationStatus);
        var ledger = CanonicalLineItem.Create(7, 1, 1, "Relay", 2m, null);
        ledger.Enrich(null, null, line.Currency.Value, null, null, "{}", CanonicalValidationStatus.Warning);
        Assert.Null(ledger.CurrencyCode);
    }
}
