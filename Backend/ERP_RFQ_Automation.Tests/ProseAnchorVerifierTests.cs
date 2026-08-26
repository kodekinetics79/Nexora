using ERP_RFQ_Automation.Extraction.Conversational;
using ERP_RFQ_Automation.Services.Interfaces;
using ERP_RFQ_Automation.Tests.Support;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// A prose message has no rows, so there is no count to conserve. What is conserved instead is
/// ANCHORS: every item must quote the submitted message verbatim. Whether a quote occurs in a
/// string is a FACT — which is exactly why this is the invention guard that survives the
/// fabricated-confidence problem (the model's self-reported confidence proves nothing).
/// </summary>
public class ProseAnchorVerifierTests
{
    private const string Message =
        "Hi, please quote 40 nos cable tray 300mm and 12 nos junction box IP65, "
        + "delivery to Jebel Ali by 20th.\nRegards\nAhmed";

    private static LeadItemData Anchored(string name, string span, int? quantity = null, string? token = null)
        => Ext.Item(0.9, name, quantity ?? 1) with { SourceSpan = span, QuantityToken = token, Quantity = quantity };

    [Fact]
    public void ItemsQuotingTheMessageInOrderAreKept()
    {
        var items = new[]
        {
            Anchored("Cable tray 300mm", "40 nos cable tray 300mm", 40, "40 nos"),
            Anchored("Junction box IP65", "12 nos junction box IP65", 12, "12 nos")
        };

        var verification = ProseAnchorVerifier.Verify(Message, items);

        Assert.Equal(2, verification.Items.Count);
        Assert.Equal(0, verification.UnanchoredItemCount);
        Assert.True(verification.Clean);
        Assert.All(verification.Items, item => Assert.True(item.SourceSpanVerified));
    }

    [Fact]
    public void AnInventedItemIsKeptButFlaggedUnverified()
    {
        // The message never mentions switchgear. Nothing in the confidence score would have
        // revealed that; the missing quote does. The item is still KEPT — a reviewer sees
        // every line the model claimed and the flag says which one could not be corroborated.
        // Deleting it here would have hidden the model's behaviour from the only person able
        // to judge it, and would delete a real request whenever the quote check is wrong.
        var items = new[]
        {
            Anchored("Cable tray 300mm", "40 nos cable tray 300mm", 40, "40 nos"),
            Anchored("Switchgear panel", "2 nos switchgear panel 11kV", 2, "2 nos")
        };

        var verification = ProseAnchorVerifier.Verify(Message, items);

        Assert.Equal(2, verification.Items.Count);
        Assert.Equal(1, verification.UnanchoredItemCount);
        Assert.False(verification.Clean);
        Assert.True(verification.Items[0].SourceSpanVerified);
        Assert.False(verification.Items[1].SourceSpanVerified);
        Assert.Contains(verification.Diagnostics, d => d.Contains("UNVERIFIED"));
    }

    [Fact]
    public void ItemWithNoSpanIsKeptButFlagged()
    {
        var verification = ProseAnchorVerifier.Verify(Message, new[] { Ext.Item(0.99, "Cable tray") });

        Assert.Single(verification.Items);
        Assert.Equal(1, verification.UnanchoredItemCount);
        Assert.False(verification.Clean);
        Assert.False(verification.Items[0].SourceSpanVerified);
    }

    [Fact]
    public void TheSameSentenceQuotedTwiceStillFlagsTheSecondItem()
    {
        // The anti-duplication signal survives the switch to keep-and-flag: the first item
        // claims the sentence, so the second finds no unclaimed occurrence and is reported
        // unverified. Both are kept for the reviewer to judge.
        var items = new[]
        {
            Anchored("Cable tray 300mm", "40 nos cable tray 300mm", 40),
            Anchored("Cable tray 300mm again", "40 nos cable tray 300mm", 40)
        };

        var verification = ProseAnchorVerifier.Verify(Message, items);

        Assert.Equal(2, verification.Items.Count);
        Assert.Equal(1, verification.UnanchoredItemCount);
        Assert.True(verification.Items[0].SourceSpanVerified);
        Assert.False(verification.Items[1].SourceSpanVerified);
    }

    [Fact]
    public void OutOfOrderSpansBothVerify()
    {
        // Models routinely group by product family rather than document order. Under the old
        // single advancing cursor, the second item here — quoting text EARLIER in the message
        // — failed to locate and was deleted, and so was everything after it. Claiming regions
        // instead of advancing a cursor means each item anchors to its own text.
        var items = new[]
        {
            Anchored("Junction box IP65", "12 nos junction box IP65", 12),
            Anchored("Cable tray 300mm", "40 nos cable tray 300mm", 40) // occurs EARLIER
        };

        var verification = ProseAnchorVerifier.Verify(Message, items);

        Assert.Equal(2, verification.Items.Count);
        Assert.Equal(0, verification.UnanchoredItemCount);
        Assert.True(verification.Clean);
    }

    [Fact]
    public void ReWrappedWhitespaceStillCounts()
    {
        var items = new[] { Anchored("Cable tray", "40   nos\ncable tray 300mm", 40) };

        var verification = ProseAnchorVerifier.Verify(Message, items);

        Assert.Single(verification.Items);
        Assert.True(verification.Clean);
    }

    [Fact]
    public void AnOverlongSpanIsNotAnAnchorButTheItemSurvives()
    {
        var longSpan = new string('x', ProseAnchorVerifier.MaxSpanLength + 1);
        var verification = ProseAnchorVerifier.Verify(Message + longSpan,
            new[] { Anchored("Padding", longSpan) });

        Assert.Single(verification.Items);
        Assert.Equal(1, verification.UnanchoredItemCount);
    }

    [Fact]
    public void ADetailedTechnicalQuoteIsStillAnAnchor()
    {
        // The bound exists to reject a paraphrase of the whole message, not to punish a line
        // for being specific. A real spec line runs well past the old 120-character limit.
        const string line =
            "2 x 300mm hot-dip galvanised perforated cable tray, 2.5m length, supplied with "
            + "coupler plates, M8 fixings and earth continuity straps, to BS EN 61537";
        var verification = ProseAnchorVerifier.Verify(
            $"Please quote {line}.", new[] { Anchored("Cable tray", line, 2) });

        Assert.Single(verification.Items);
        Assert.Equal(0, verification.UnanchoredItemCount);
        Assert.True(verification.Clean);
    }

    [Fact]
    public void AQuoteThatDiffersOnlyInCaseStillAnchors()
    {
        // Every regex in the verifier is IgnoreCase; the span comparison was not, so a model
        // that title-cased a description while quoting it lost the line.
        var verification = ProseAnchorVerifier.Verify(
            "please quote 40 nos cable tray 300mm",
            new[] { Anchored("Cable tray", "40 NOS Cable Tray 300MM", 40) });

        Assert.Single(verification.Items);
        Assert.Equal(0, verification.UnanchoredItemCount);
    }

    [Fact]
    public void RedactedContactDetailsInASpanDoNotDropARealItem()
    {
        // On an EXTERNAL provider the model is shown "[REDACTED_PHONE]" where the submitted
        // text has digits. The item is real; only the characters it could quote differ.
        const string message = "Please quote 2 sets of 400/230 V 50 Hz panel boards.";
        var items = new[]
        {
            Anchored("Panel board", "2 sets of [REDACTED_PHONE] Hz panel boards", 2, "2 sets")
        };

        var verification = ProseAnchorVerifier.Verify(message, items);

        Assert.Single(verification.Items);
        Assert.Equal(0, verification.UnanchoredItemCount);
    }

    [Fact]
    public void CeilingCountsEveryQuantityTokenOnOneLine()
    {
        // The owner's own sentence puts TWO requests on ONE line. A line-only ceiling would
        // have deleted the junction box.
        Assert.Equal(2, ProseAnchorVerifier.ComputeCeiling(Message));
    }

    [Fact]
    public void CeilingCountsBulletedRequestsWithoutQuantities()
    {
        const string message = "Please quote:\n- cable tray 300mm\n- junction box IP65\n- gland kit";

        Assert.Equal(3, ProseAnchorVerifier.ComputeCeiling(message));
    }

    [Fact]
    public void CeilingIsNeverBelowOne()
    {
        Assert.Equal(1, ProseAnchorVerifier.ComputeCeiling("please quote cable tray 300mm"));
        Assert.Equal(1, ProseAnchorVerifier.ComputeCeiling(""));
    }

    [Fact]
    public void ItemsBeyondTheCeilingAreReportedButNeverDropped()
    {
        const string message = "Please quote cable tray 300mm.";
        var items = new[]
        {
            Anchored("Cable tray", "cable tray 300mm"),
            Anchored("Invented", "cable tray 300mm")
        };

        var verification = ProseAnchorVerifier.Verify(message, items);

        Assert.Equal(2, verification.Items.Count);
        Assert.Equal(1, verification.Ceiling);
        Assert.False(verification.Clean);
    }

    [Fact]
    public void AProseRfqUsingUnknownUnitsKeepsEveryLine()
    {
        // The ceiling is derived from a short English unit list. A real enquiry written in
        // prose using "each", "box" and "roll" scores a ceiling of 1 — which used to discard
        // every line but the first, silently, on a completely genuine RFQ. This is the single
        // most expensive way the extractor could be wrong, so it is pinned.
        const string message =
            "Kindly quote the following: 12 each of junction box IP65, 5 box of gland kits, "
            + "and 3 roll of earth tape.";
        var items = new[]
        {
            Anchored("Junction box IP65", "12 each of junction box IP65", 12),
            Anchored("Gland kits", "5 box of gland kits", 5),
            Anchored("Earth tape", "3 roll of earth tape", 3)
        };

        var verification = ProseAnchorVerifier.Verify(message, items);

        Assert.Equal(3, verification.Items.Count);
        Assert.Equal(0, verification.UnanchoredItemCount);
    }

    [Fact]
    public void NoItemsIsACleanZeroNotAFailure()
    {
        var verification = ProseAnchorVerifier.Verify(Message, Array.Empty<LeadItemData>());

        Assert.Empty(verification.Items);
        Assert.True(verification.Clean);
    }
}
