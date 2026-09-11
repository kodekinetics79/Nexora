using System.Text;
using ERP_RFQ_Automation.Security.DocumentInspection;
using ERP_RFQ_Automation.Services.DocumentIntelligence;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// A sourcing portal's "print version of the event" saved as <c>.doc</c> is an HTML page with a
/// Word name. It used to be refused at the door ("rename the file so it ends in .html"); the
/// same print saved as a real Word file was read deterministically. Both now take one path.
/// </summary>
public sealed class HtmlNamedAsWordTests
{
    /// <summary>The shape of an SAP Ariba event print, as the SEC portal exports it.</summary>
    private const string AribaPrint = """
        <!-- class: ariba.sourcing.rfxui.PrintRFXEncode -->
        <html><head><meta content="text/html; charset=UTF-8" http-equiv="Content-Type"/>
        <style>td { border: 1px solid #666699; }</style></head>
        <body>
        <table><tr><th colspan="2" class="sectionHead">Introduction</th></tr>
        <tr><td colspan="2">This is a print version of the event.</td></tr></table>
        <table><tr><th colspan="2">Overview</th></tr>
        <tr><th class="fieldLabel">Owner</th><td>TURKI ABDULLAH ALAHMARI</td></tr>
        <tr><th class="fieldLabel">Event Type</th><td>RFP</td></tr>
        <tr><th class="fieldLabel">Currency</th><td>Saudi Riyal</td></tr>
        <tr><th class="fieldLabel">Commodity</th><td>Rechargeable batteries 26111701</td></tr></table>
        <table><tr><th colspan="2">Timing Rules</th></tr>
        <tr><th class="fieldLabel">Publish time</th><td>9/10/2026 3:43 PM</td></tr>
        <tr><th class="fieldLabel">Due date</th><td>9/16/2026 1:30 AM</td></tr></table>
        <table><tr><th colspan="3">Content</th></tr>
        <tr><th>Name</th><th>Alternative</th><th>Value</th></tr>
        <tr><td>2 Local vendors MUST bid in SAR only</td><td></td><td></td></tr>
        <tr><td>6 Materials Required</td><td></td><td></td></tr>
        <tr><td>8 10 909101154 BATTERY,STORAGE,MAX VOLT 1.5 V,830AH</td><td></td><td></td></tr>
        <tr><td colspan="2">10 909101154 BATTERY,STORAGE,MAX VOLT 1.5 V,830AH</td><td></td></tr>
        <tr><td>Price</td><td></td><td></td></tr>
        <tr><td>Quantity</td><td></td><td>92 each</td></tr>
        <tr><td>Extended Price</td><td></td><td></td></tr>
        <tr><td>Storage Location</td><td></td><td>Saudi Electricity Company-Jizan Area</td></tr>
        <tr><td>Manufacturer Name</td><td></td><td></td></tr>
        <tr><td>Manufacturer Part Number</td><td></td><td></td></tr>
        <tr><td>Material Code</td><td></td><td>909101154</td></tr>
        <tr><td>Item Text</td><td></td><td>;Item text:Including Supply, Installation<br/>and Testing in Jizan</td></tr>
        <tr><td>Delivery time (in Days)</td><td></td><td></td></tr>
        </table>
        </body></html>
        """;

    [Fact]
    public void An_HTML_page_named_doc_is_read_from_its_tables_like_the_Word_print()
    {
        var reading = HtmlTableGrids.Read(Encoding.UTF8.GetBytes(AribaPrint));
        Assert.Equal(4, reading.Grids.Count);                       // top-level tables, document order
        Assert.Equal("Due date", reading.Grids[2][2][0]);
        Assert.Equal("9/16/2026 1:30 AM", reading.Grids[2][2][1]);
        Assert.Equal(3, reading.Grids[3][1].Count);                 // Name | Alternative | Value
        Assert.Null(reading.Grids[3][3][1]);                        // a colspan pads, it does not shift

        var parser = new DocxTableParser(new NativeSpreadsheetParser());
        var rows = parser.ParseGrids(reading.Grids, reading.Paragraphs, "SE RFP-C001835789.doc");

        var row = Assert.Single(rows);
        Assert.Equal("10", row.CustomerLineNumber);
        Assert.Equal("92", row.Quantity);
        Assert.Equal("each", row.UnitOfMeasure);
        Assert.Equal("BATTERY,STORAGE,MAX VOLT 1.5 V,830AH", row.ProductName);   // line number and material stripped
        Assert.Equal("909101154", row.ManufacturerPartNumber ?? row.CustomerMaterialCode);
        Assert.Contains("Installation\nand Testing", row.ItemText);   // the <br> survived as a line
        Assert.Equal("C001835789", row.RfqNo);                       // from the file name, as for the Word print
        Assert.Equal("9/16/2026 1:30 AM", row.BidClosingDate);
        Assert.Equal("Saudi Riyal", row.Currency);
        Assert.Equal("Saudi Electricity Company-Jizan Area", row.UnmappedColumns["Storage Location"]);
    }

    [Fact]
    public async Task Inspection_accepts_a_web_page_named_as_a_Word_file()
    {
        var inspection = new DocumentFileInspectionService(new AlwaysCleanScanner());
        using var content = new MemoryStream(Encoding.UTF8.GetBytes(AribaPrint));

        var result = await inspection.InspectAsync(
            new FileInspectionRequest(content, "SE RFP-C001835789.doc", "application/msword"));

        Assert.True(result.IsCleared, result.Reason);
        Assert.Equal("text/html", result.DetectedContentType);
    }

    [Fact]
    public async Task Inspection_still_refuses_a_web_page_named_as_a_PDF()
    {
        var inspection = new DocumentFileInspectionService(new AlwaysCleanScanner());
        using var content = new MemoryStream(Encoding.UTF8.GetBytes(AribaPrint));

        var result = await inspection.InspectAsync(new FileInspectionRequest(content, "bid.pdf", "application/pdf"));

        Assert.False(result.IsCleared);
        Assert.Contains("web page", result.Reason);
    }

    private sealed class AlwaysCleanScanner : IMalwareScanner
    {
        public Task<MalwareScanResult> ScanAsync(Stream content, CancellationToken cancellationToken = default)
            => Task.FromResult(MalwareScanResult.Clean("test-scanner"));
    }
}
