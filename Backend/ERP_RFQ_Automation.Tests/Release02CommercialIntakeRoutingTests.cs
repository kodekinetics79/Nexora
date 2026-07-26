using ERP_RFQ_Automation.Extraction;

namespace ERP_RFQ_Automation.Tests;

public sealed class Release02CommercialIntakeRoutingTests
{
    [Theory]
    [InlineData("SUPPLIER_QUOTE")]
    [InlineData("SUPPLIER_QUOTE_REVISION")]
    [InlineData("CUSTOMER_ORDER")]
    public void Non_customer_commercial_documents_cannot_enter_Lead_persistence(string hint) =>
        Assert.True(ExtractionJobMetadata.IsNonLeadCommercialType(hint));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("CUSTOMER_RFQ")]
    [InlineData("customer_rfq_revision")]
    public void Customer_inquiry_or_unspecified_intake_retains_Lead_path(string? hint) =>
        Assert.False(ExtractionJobMetadata.IsNonLeadCommercialType(hint));
}
