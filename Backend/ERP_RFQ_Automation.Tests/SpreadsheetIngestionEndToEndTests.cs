using System.Text;
using ERP_RFQ_Automation.DTOs.DocumentIntelligence;
using ERP_RFQ_Automation.Services.DocumentIntelligence;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// Parser → canonical normalizer, on documents shaped like the ones customers actually send.
/// The unit tests either side of this seam both pass while the seam itself drops a field, which
/// is exactly how the unit column went missing for the whole life of the product.
/// </summary>
public sealed class SpreadsheetIngestionEndToEndTests
{
    private static readonly NativeSpreadsheetParser Parser = new();
    private static readonly CanonicalRfqNormalizer Normalizer = new();

    private static CanonicalRfqDocument Ingest(params string[] csvLines)
    {
        var rows = Parser.ParseCsv(Encoding.UTF8.GetBytes(string.Join("\r\n", csvLines)), "rfq.csv");
        var result = Normalizer.NormalizeSpreadsheetRows(rows, businessUnitId: 7);
        return Assert.Single(result.Documents);
    }

    [Fact]
    public void A_realistic_tender_survives_intact_from_cell_to_canonical_line()
    {
        var document = Ingest(
            "ACME CONTRACTING COMPANY",
            "Request for Quotation - C001046140",
            "",
            "RFQ No.,Buyer Name,Bid Closing Date,Material Description,Req Qty,U/M,Part No.,Make",
            "RFQ-4471,Acme Contracting,15/03/2026,Copper cable 3-core,500,M,CBL-3C,Riyadh Cables",
            "RFQ-4471,Acme Contracting,15/03/2026,Ball valve DN50,12,EA,BV-50,KITZ");

        Assert.Equal("RFQ-4471", document.RfqNo.Value);
        Assert.Equal("Acme Contracting", document.BuyerName.Value);
        Assert.Equal(new DateTime(2026, 3, 15), document.BidClosingDate.Value.Date);
        Assert.Equal(2, document.LineItems.Count);

        var cable = document.LineItems[0];
        Assert.Equal("Copper cable 3-core", cable.ProductName.Value);
        Assert.Equal(500, cable.Quantity.Value);
        // The whole point: metres, not "500 of something we quoted as each".
        Assert.Equal("M", cable.UnitOfMeasure.Value);
        Assert.Equal("CBL-3C", cable.ManufacturerPartNumber.Value);
        Assert.Equal("Riyadh Cables", cable.ManufacturerName.Value);

        Assert.Equal("EA", document.LineItems[1].UnitOfMeasure.Value);
        Assert.Equal(12, document.LineItems[1].Quantity.Value);
    }

    [Fact]
    public void An_excel_date_cell_is_valid_not_an_unsupported_format()
    {
        // The legacy .xls reader renders a real date cell with the round-trip "O" format. The
        // normalizer's private format list did not include it, so a genuine Excel date came out
        // Invalid — on the only ingestion path that works without the AI gateway.
        var document = Ingest(
            "RFQ No,Buyer Name,Bid Closing Date,Product Name,Quantity,UOM",
            "RFQ-9,Acme,2026-03-15T00:00:00.0000000,Gasket,4,EA");

        Assert.Equal(ValidationStatus.Valid, document.BidClosingDate.ValidationStatus);
        Assert.Equal(new DateTime(2026, 3, 15), document.BidClosingDate.Value.Date);
    }

    [Fact]
    public void A_spelled_month_closing_date_is_accepted_on_the_spreadsheet_path()
    {
        var document = Ingest(
            "RFQ No,Buyer Name,Bid Closing Date,Product Name,Quantity,UOM",
            "RFQ-10,Acme,15 Mar 2026,Gasket,4,EA");

        Assert.Equal(ValidationStatus.Valid, document.BidClosingDate.ValidationStatus);
        Assert.Equal(new DateTime(2026, 3, 15), document.BidClosingDate.Value.Date);
    }

    [Fact]
    public void A_sentinel_closing_date_is_rejected_rather_than_stored()
    {
        var document = Ingest(
            "RFQ No,Buyer Name,Bid Closing Date,Product Name,Quantity,UOM",
            "RFQ-11,Acme,0001-01-01,Gasket,4,EA");

        Assert.NotEqual(ValidationStatus.Valid, document.BidClosingDate.ValidationStatus);
    }

    [Fact]
    public void A_missing_unit_reaches_the_canonical_line_as_missing_not_as_a_guess()
    {
        var document = Ingest(
            "RFQ No,Buyer Name,Product Name,Quantity,UOM",
            "RFQ-12,Acme,Gasket,4,");

        Assert.Null(Assert.Single(document.LineItems).UnitOfMeasure.Value);
    }
}
