using ERP_RFQ_Automation.Services.DocumentIntelligence;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// Which rows belong to which inquiry. A Word bid list of 1,500 lines that named no RFQ number
/// on any row was keyed by row number and became 1,500 canonical inquiries with 1,500 sets of
/// header evidence; rows with no RFQ identity of their own are one document.
/// </summary>
public sealed class CanonicalRfqNormalizerKeyTests
{
    private static RfqSpreadsheetRow Row(int number, string product, string? rfq = null, string? buyer = null) => new()
    {
        RowNumber = number,
        RfqNo = rfq,
        BuyerName = buyer,
        ProductName = product,
        Quantity = "1",
        UnitOfMeasure = "EA",
    };

    [Fact]
    public void Rows_without_an_rfq_number_or_buyer_form_one_inquiry()
    {
        var result = new CanonicalRfqNormalizer().NormalizeSpreadsheetRows(
            new[] { Row(1, "Relay module"), Row(2, "Display module"), Row(3, "Alarm switch") }, businessUnitId: 1);

        var document = Assert.Single(result.Documents);
        Assert.Equal(3, document.LineItems.Count);
    }

    [Fact]
    public void Rows_that_name_different_rfqs_stay_separate_inquiries()
    {
        var result = new CanonicalRfqNormalizer().NormalizeSpreadsheetRows(
            new[] { Row(1, "Relay module", rfq: "RFQ-A", buyer: "SEC"), Row(2, "Display module", rfq: "RFQ-B", buyer: "SEC"), Row(3, "Alarm switch", rfq: "RFQ-A", buyer: "SEC") },
            businessUnitId: 1);

        Assert.Equal(2, result.Documents.Count);
        Assert.Equal(2, result.Documents.Single(d => d.RfqNo.Value == "RFQ-A").LineItems.Count);
    }

    [Fact]
    public void Identity_less_rows_do_not_merge_into_a_named_rfq_beside_them()
    {
        var result = new CanonicalRfqNormalizer().NormalizeSpreadsheetRows(
            new[] { Row(1, "Relay module", rfq: "RFQ-A", buyer: "SEC"), Row(2, "Display module") },
            businessUnitId: 1);

        Assert.Equal(2, result.Documents.Count);
    }
}
