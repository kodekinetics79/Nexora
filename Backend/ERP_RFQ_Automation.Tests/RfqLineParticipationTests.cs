using ERP_RFQ_Automation.Models;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// Line-level bid participation — the decision that turns an 84-line customer bid list into a
/// 12-line Nexora quote. The reason rule is enforced in the domain so that every caller obeys
/// it, and these tests pin that rather than the controller that happens to call it today.
/// </summary>
public sealed class RfqLineParticipationTests
{
    private static Rfqitem NewLine() => new() { Id = 1, Rfqid = 1, Quantity = 5, CreatedBy = "seed" };

    private static readonly DateTime Now = new(2026, 8, 6, 4, 30, 0, DateTimeKind.Utc);

    [Fact]
    public void ALineStartsPending_NotQuoted()
    {
        // The load-bearing default. A line nobody has looked at must never read as an implicit
        // "yes" — that is how a supplier ends up committed to a line no engineer ever priced.
        var line = NewLine();

        Assert.Equal(Rfqitem.ParticipationPending, line.ParticipationDecision);
        Assert.False(line.IsMarkedForQuote);
        Assert.Null(line.NoQuoteReason);
        Assert.Null(line.ParticipationDecidedBy);
        Assert.Null(line.ParticipationDecidedOn);
    }

    [Fact]
    public void MarkingALineForQuote_RecordsTheActorAndTime()
    {
        var line = NewLine();

        line.DecideParticipation(Rfqitem.ParticipationQuote, null, "sara@nexora.sa", Now);

        Assert.True(line.IsMarkedForQuote);
        Assert.Equal("sara@nexora.sa", line.ParticipationDecidedBy);
        Assert.Equal(Now, line.ParticipationDecidedOn);
        Assert.Null(line.NoQuoteReason);
    }

    [Fact]
    public void NoQuote_WithoutAReason_IsRefused()
    {
        // A silent decline is indistinguishable from an oversight when the buyer asks why a
        // line is missing from our quote.
        var line = NewLine();

        var ex = Assert.Throws<InvalidOperationException>(
            () => line.DecideParticipation(Rfqitem.ParticipationNoQuote, null, "sara@nexora.sa", Now));
        Assert.Contains("requires a reason", ex.Message);

        // and the line is untouched — a refused decision must not half-apply
        Assert.Equal(Rfqitem.ParticipationPending, line.ParticipationDecision);
        Assert.Null(line.ParticipationDecidedBy);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("no")]
    [InlineData("n/a")]
    public void NoQuote_WithAnEmptyOrTokenReason_IsRefused(string reason)
    {
        var line = NewLine();

        Assert.ThrowsAny<Exception>(
            () => line.DecideParticipation(Rfqitem.ParticipationNoQuote, reason, "sara@nexora.sa", Now));
        Assert.Equal(Rfqitem.ParticipationPending, line.ParticipationDecision);
    }

    [Fact]
    public void NoQuote_WithAReason_IsRecorded()
    {
        var line = NewLine();

        line.DecideParticipation(
            Rfqitem.ParticipationNoQuote, "  Obsolete Alstom part, no supplier source  ", "sara@nexora.sa", Now);

        Assert.False(line.IsMarkedForQuote);
        Assert.Equal(Rfqitem.ParticipationNoQuote, line.ParticipationDecision);
        Assert.Equal("Obsolete Alstom part, no supplier source", line.NoQuoteReason); // trimmed
    }

    [Fact]
    public void ReversingANoQuote_ClearsTheDeclineReason()
    {
        // Otherwise the audit trail keeps asserting a decline that was reversed, and the reason
        // would print on a quote line we are in fact quoting.
        var line = NewLine();
        line.DecideParticipation(Rfqitem.ParticipationNoQuote, "Lead time exceeds bid validity", "sara@nexora.sa", Now);

        line.DecideParticipation(Rfqitem.ParticipationQuote, null, "omar@nexora.sa", Now.AddMinutes(5));

        Assert.True(line.IsMarkedForQuote);
        Assert.Null(line.NoQuoteReason);
        Assert.Equal("omar@nexora.sa", line.ParticipationDecidedBy);
        Assert.Equal(Now.AddMinutes(5), line.ParticipationDecidedOn);
    }

    [Fact]
    public void AnUnknownDecisionIsRefused_RatherThanStoredAsFreeText()
    {
        // The defect this whole field replaces was exactly this: RFQ.BiddingDecision accepted
        // any string at all.
        var line = NewLine();

        Assert.Throws<ArgumentException>(
            () => line.DecideParticipation("Maybe", null, "sara@nexora.sa", Now));
        Assert.Equal(Rfqitem.ParticipationPending, line.ParticipationDecision);
    }

    [Theory]
    [InlineData("quote")]
    [InlineData("QUOTE")]
    [InlineData(" noquote ")]
    public void DecisionMatchingIsCaseAndWhitespaceInsensitive_ButStoresTheCanonicalForm(string input)
    {
        var line = NewLine();

        line.DecideParticipation(input, "Supplier declined to bid", "sara@nexora.sa", Now);

        Assert.Contains(line.ParticipationDecision,
            new[] { Rfqitem.ParticipationQuote, Rfqitem.ParticipationNoQuote });
        Assert.DoesNotContain(" ", line.ParticipationDecision);
    }

    [Fact]
    public void ADecisionRequiresAnAuthenticatedActor()
    {
        var line = NewLine();

        Assert.Throws<ArgumentException>(
            () => line.DecideParticipation(Rfqitem.ParticipationQuote, null, "  ", Now));
    }
}
