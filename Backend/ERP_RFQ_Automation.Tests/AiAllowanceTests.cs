using ERP_RFQ_Automation.AI;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// The allowance an operator sets is in DOCUMENTS; the ledger enforces TOKENS. One conversion,
/// served to the console rather than duplicated in it, because two copies drift the first time
/// either is edited — and the drift is invisible until a tenant is cut off earlier than the
/// operator promised.
/// </summary>
public sealed class AiAllowanceTests
{
    [Theory]
    [InlineData(100, 1_200_000)]
    [InlineData(500, 6_000_000)]
    [InlineData(2_000, 24_000_000)]
    public void A_document_allowance_becomes_the_token_ceiling_the_ledger_enforces(int documents, long tokens)
        => Assert.Equal(tokens, AiAllowance.TokensForDocuments(documents));

    [Fact]
    public void No_ceiling_is_a_deliberate_null_never_a_zero()
    {
        // Zero is a kill switch wearing a budget's clothes: every other control reads open and the
        // ledger then refuses every document under hard_budget_exceeded.
        Assert.Null(AiAllowance.TokensForDocuments(null));
        Assert.Null(AiAllowance.TokensForDocuments(0));
        Assert.Null(AiAllowance.TokensForDocuments(-5));
    }

    [Fact]
    public void An_existing_ceiling_reads_back_in_documents_rounded_down()
    {
        // Round DOWN: an allowance that reads as more documents than it can pay for is the one
        // direction that produces a surprise for the customer.
        Assert.Equal(500, AiAllowance.DocumentsForTokens(6_000_000));
        Assert.Equal(1, AiAllowance.DocumentsForTokens(AiAllowance.TokensPerDocument + 1));
        Assert.Null(AiAllowance.DocumentsForTokens(null));
    }

    [Fact]
    public void The_round_trip_never_inflates_what_the_customer_was_sold()
    {
        foreach (var documents in AiAllowance.DocumentPresets)
        {
            var back = AiAllowance.DocumentsForTokens(AiAllowance.TokensForDocuments(documents));
            Assert.Equal(documents, back);
        }
    }
}
