using ERP_RFQ_Automation.Ingestion.Triage;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// Only the sender's OWN words are submitted for extraction. A forwarded thread contains the
/// original request two or three times; extracting the whole body produces the same line items
/// repeatedly — or invents a "second inquiry" out of the quoted copy. The signature block is
/// deliberately KEPT in the fresh text: in this segment it is usually the only place the
/// buying organisation is named.
/// </summary>
public class EmailBodyNormalizerTests
{
    [Fact]
    public void ThreeDeepForwardYieldsExactlyOneFreshBlock()
    {
        var body = string.Join("\n", new[]
        {
            "Hi Ahmed,",
            "",
            "Please quote 40 nos cable tray 300mm as discussed.",
            "",
            "On Tue, 4 Aug 2026 at 09:12, Sara <sara@gulfmep.ae> wrote:",
            "> Forwarding the client requirement below.",
            "> Please quote 40 nos cable tray 300mm.",
            ">",
            "> -----Original Message-----",
            "> From: Client <client@site.example>",
            "> Sent: Monday, 3 August 2026 16:40",
            "> To: Sara <sara@gulfmep.ae>",
            "> Subject: Cable tray",
            ">",
            "> We need 40 nos cable tray 300mm for the Jebel Ali site."
        });

        var parts = EmailBodyNormalizer.Normalize(body);

        Assert.Contains("Please quote 40 nos cable tray 300mm as discussed.", parts.Fresh);
        Assert.DoesNotContain("Forwarding the client requirement", parts.Fresh);
        Assert.DoesNotContain("Original Message", parts.Fresh);
        Assert.DoesNotContain("Jebel Ali site", parts.Fresh);
        // The request appears exactly ONCE in what will be submitted.
        Assert.Equal(1, CountOccurrences(parts.Fresh, "40 nos cable tray 300mm"));
        Assert.Contains("Forwarding the client requirement", parts.Quoted);
        Assert.True(parts.ThreadDepth >= 2, $"expected a nested thread, got depth {parts.ThreadDepth}");
        Assert.False(parts.BodyEmptyAfterStrip);
    }

    [Fact]
    public void OutlookHeaderBlockStartsTheQuotedSection()
    {
        var body = string.Join("\n", new[]
        {
            "Kindly advise best price for the below.",
            "",
            "From: Client <client@site.example>",
            "Sent: Monday, 3 August 2026 16:40",
            "To: Sales <sales@tenant.example>",
            "Subject: Cable tray",
            "",
            "We need 12 nos junction box IP65."
        });

        var parts = EmailBodyNormalizer.Normalize(body);

        Assert.Equal("Kindly advise best price for the below.", parts.Fresh);
        Assert.Contains("12 nos junction box IP65", parts.Quoted);
    }

    [Fact]
    public void ReplyThatAddsNothingIsEmptyAfterStrip()
    {
        var body = string.Join("\n", new[]
        {
            "",
            "On Tue, 4 Aug 2026 at 09:12, Sara <sara@gulfmep.ae> wrote:",
            "> Please quote 40 nos cable tray 300mm."
        });

        var parts = EmailBodyNormalizer.Normalize(body);

        Assert.True(parts.BodyEmptyAfterStrip);
        Assert.Equal(string.Empty, parts.Fresh);
    }

    [Fact]
    public void SignatureOnlyReplyIsEmptyAfterStripButKeepsTheSignatureText()
    {
        var body = string.Join("\n", new[]
        {
            "",
            "Regards,",
            "Ahmed Al Mansoori",
            "Al Noor Trading LLC",
            "+971 4 555 1234",
            "",
            "On Tue, 4 Aug 2026 at 09:12, Sara <sara@gulfmep.ae> wrote:",
            "> Please quote."
        });

        var parts = EmailBodyNormalizer.Normalize(body);

        Assert.True(parts.BodyEmptyAfterStrip);
        Assert.NotNull(parts.Signature);
        Assert.Contains("Al Noor Trading LLC", parts.Signature!);
    }

    [Fact]
    public void SignatureBlockIsDetectedAndKeptInTheSubmittedText()
    {
        // The signature stays in Fresh ON PURPOSE: it is the buyer organisation evidence the
        // conversational prompt is told to read.
        var body = string.Join("\n", new[]
        {
            "Dear Sir,",
            "",
            "Please quote 40 nos cable tray 300mm.",
            "",
            "Best regards,",
            "Ahmed Al Mansoori",
            "Procurement Officer",
            "Al Noor Trading LLC",
            "ahmed@alnoortrading.ae",
            "+971 4 555 1234"
        });

        var parts = EmailBodyNormalizer.Normalize(body);

        Assert.NotNull(parts.Signature);
        Assert.Contains("Al Noor Trading LLC", parts.Signature!);
        Assert.Contains("Al Noor Trading LLC", parts.Fresh);
        Assert.Contains("Please quote 40 nos cable tray 300mm.", parts.Fresh);
        Assert.False(parts.BodyEmptyAfterStrip);
    }

    [Fact]
    public void DashDashDelimiterDefinesTheSignature()
    {
        var body = "We require 250 mtrs cable.\n\n--\nAhmed\nAl Noor Trading LLC";

        var parts = EmailBodyNormalizer.Normalize(body);

        Assert.NotNull(parts.Signature);
        Assert.StartsWith("Ahmed", parts.Signature!);
        Assert.False(parts.BodyEmptyAfterStrip);
    }

    [Fact]
    public void TrailingDisclaimerAndMobileFooterAreStripped()
    {
        var body = string.Join("\n", new[]
        {
            "Please quote 40 nos cable tray 300mm.",
            "Sent from my iPhone",
            "",
            "This email and any attachments are confidential and intended solely for the "
                + "addressee. If you are not the intended recipient, delete it."
        });

        var parts = EmailBodyNormalizer.Normalize(body);

        Assert.Contains("40 nos cable tray 300mm", parts.Fresh);
        Assert.DoesNotContain("Sent from my iPhone", parts.Fresh);
        Assert.DoesNotContain("intended recipient", parts.Fresh);
    }

    [Fact]
    public void ADisclaimerStripNeverSwallowsAParagraphThatCarriesLineItems()
    {
        // A conservative guard: losing a line item to a boilerplate strip is precisely the
        // class of silent loss this work item exists to remove.
        var body = "Kindly treat this as confidential: we need 40 nos cable tray 300mm.";

        var parts = EmailBodyNormalizer.Normalize(body);

        Assert.Contains("40 nos cable tray 300mm", parts.Fresh);
    }

    [Fact]
    public void EmptyBodyIsReportedAsEmpty()
    {
        var parts = EmailBodyNormalizer.Normalize("   \n  \n");

        Assert.Equal(string.Empty, parts.Fresh);
        Assert.True(parts.BodyEmptyAfterStrip);
        Assert.Equal(0, parts.ThreadDepth);
    }

    [Fact]
    public void AnOriginalMessageHasNoQuotedSectionAndNoThreadDepth()
    {
        var parts = EmailBodyNormalizer.Normalize(
            "Hi,\n\nPlease quote 40 nos cable tray 300mm.\n\nRegards\nAhmed");

        Assert.Equal(0, parts.ThreadDepth);
        Assert.Equal(string.Empty, parts.Quoted);
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var index = haystack.IndexOf(needle, StringComparison.Ordinal);
        while (index >= 0)
        {
            count++;
            index = haystack.IndexOf(needle, index + needle.Length, StringComparison.Ordinal);
        }
        return count;
    }
}
