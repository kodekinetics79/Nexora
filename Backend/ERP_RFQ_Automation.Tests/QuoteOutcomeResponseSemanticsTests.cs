using ERP_RFQ_Automation.Sla;

namespace ERP_RFQ_Automation.Tests;

public sealed class QuoteOutcomeResponseSemanticsTests
{
    [Theory]
    [InlineData("won", null, true)]
    [InlineData("lost", "PRICE", true)]
    [InlineData("lost", "NO_RESPONSE", false)]
    [InlineData("lost", " no_response ", false)]
    [InlineData("expired", "AUTO_EXPIRED", false)]
    public void Only_actual_customer_decisions_count_as_responses(
        string outcome, string? reasonCode, bool expected) =>
        Assert.Equal(expected, QuoteOutcomeService.RecordsCustomerResponse(outcome, reasonCode));
}
