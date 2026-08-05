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
    }

    [Fact]
    public void AnInventedItemIsDropped()
    {
        // The message never mentions switchgear. Nothing in the confidence score would have
        // revealed that; the missing quote does.
        var items = new[]
        {
            Anchored("Cable tray 300mm", "40 nos cable tray 300mm", 40, "40 nos"),
            Anchored("Switchgear panel", "2 nos switchgear panel 11kV", 2, "2 nos")
        };

        var verification = ProseAnchorVerifier.Verify(Message, items);

        Assert.Single(verification.Items);
        Assert.Equal("Cable tray 300mm", verification.Items[0].ProductShortName);
        Assert.Equal(1, verification.UnanchoredItemCount);
        Assert.False(verification.Clean);
        Assert.Contains(verification.Diagnostics, d => d.Contains("does not occur"));
    }

    [Fact]
    public void ItemWithNoSpanIsDropped()
    {
        var verification = ProseAnchorVerifier.Verify(Message, new[] { Ext.Item(0.99, "Cable tray") });

        Assert.Empty(verification.Items);
        Assert.Equal(1, verification.UnanchoredItemCount);
    }

    [Fact]
    public void TheSameSentenceQuotedTwiceCannotProduceTwoItems()
    {
        var items = new[]
        {
            Anchored("Cable tray 300mm", "40 nos cable tray 300mm", 40),
            Anchored("Cable tray 300mm again", "40 nos cable tray 300mm", 40)
        };

        var verification = ProseAnchorVerifier.Verify(Message, items);

        Assert.Single(verification.Items);
        Assert.Equal(1, verification.UnanchoredItemCount);
    }

    [Fact]
    public void OutOfOrderSpansAreRejected()
    {
        var items = new[]
        {
            Anchored("Junction box IP65", "12 nos junction box IP65", 12),
            Anchored("Cable tray 300mm", "40 nos cable tray 300mm", 40) // occurs EARLIER
        };

        var verification = ProseAnchorVerifier.Verify(Message, items);

        Assert.Single(verification.Items);
        Assert.Equal("Junction box IP65", verification.Items[0].ProductShortName);
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
    public void AnOverlongSpanIsNotAnAnchor()
    {
        var longSpan = new string('x', ProseAnchorVerifier.MaxSpanLength + 1);
        var verification = ProseAnchorVerifier.Verify(Message + longSpan,
            new[] { Anchored("Padding", longSpan) });

        Assert.Empty(verification.Items);
        Assert.Equal(1, verification.UnanchoredItemCount);
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
    public void ItemsBeyondTheCeilingAreDroppedAndReported()
    {
        const string message = "Please quote cable tray 300mm.";
        var items = new[]
        {
            Anchored("Cable tray", "cable tray 300mm"),
            Anchored("Invented", "cable tray 300mm")
        };

        var verification = ProseAnchorVerifier.Verify(message, items);

        Assert.Single(verification.Items);
        Assert.Equal(1, verification.Ceiling);
        Assert.False(verification.Clean);
    }

    [Fact]
    public void NoItemsIsACleanZeroNotAFailure()
    {
        var verification = ProseAnchorVerifier.Verify(Message, Array.Empty<LeadItemData>());

        Assert.Empty(verification.Items);
        Assert.True(verification.Clean);
    }
}
