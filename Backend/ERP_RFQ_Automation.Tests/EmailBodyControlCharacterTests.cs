using System.Text;
using ERP_RFQ_Automation.Services.Interfaces;
using ERP_RFQ_Automation.Ingestion.Triage;
using ERP_RFQ_Automation.Security.DocumentInspection;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// The body we generate must survive the inspection we apply to it.
///
/// <para><b>The defect.</b> The normalized body is written as <c>&lt;subject&gt;_body.txt</c>
/// and then put through <see cref="DocumentFileInspectionService"/>, which refuses any control
/// character other than CR/LF/TAB/FF — a rule written for files a stranger uploads. Outlook
/// writes VERTICAL TAB (U+000B) as a soft line break, so an ordinary forwarded enquiry failed
/// with "The text file contains binary or unsafe control characters". That held the body
/// component, the assembly barrier could never be satisfied, and the message could never
/// become a Lead however cleanly its attachments read — measured on a live customer RFQ whose
/// three attachments all extracted.</para>
///
/// <para>The fix cleans OUR artifact rather than relaxing the gate: the inspector still
/// protects genuine uploads byte-for-byte.</para>
/// </summary>
public sealed class EmailBodyControlCharacterTests
{
    private const char VerticalTab = '\u000B';
    private const char FormFeed = '\u000C';

    [Fact]
    public void A_vertical_tab_becomes_a_line_break_rather_than_failing_the_body()
    {
        // Outlook's soft line break, inside a quantity table where running two lines together
        // would merge two requested items.
        var outlookBody =
            $"Please quote:{VerticalTab}PN LIVE-1001 qty 7 EA{VerticalTab}PN LIVE-2002 qty 13 EA";

        var parts = EmailBodyNormalizer.Normalize(outlookBody);

        Assert.Contains("PN LIVE-1001 qty 7 EA", parts.Fresh);
        Assert.Contains("PN LIVE-2002 qty 13 EA", parts.Fresh);
        // The two items must stay on separate lines — fusing them loses a requested item.
        Assert.DoesNotContain("7 EAPN", parts.Fresh);
        Assert.DoesNotContain(VerticalTab, parts.Fresh);
    }

    [Fact]
    public void A_form_feed_also_becomes_a_line_break()
    {
        var parts = EmailBodyNormalizer.Normalize($"Page one{FormFeed}Page two");

        Assert.Contains("Page one", parts.Fresh);
        Assert.Contains("Page two", parts.Fresh);
        Assert.DoesNotContain(FormFeed, parts.Fresh);
    }

    [Theory]
    [InlineData('\u0000')] // NUL
    [InlineData('\u0001')] // SOH
    [InlineData('\u001B')] // ESC
    [InlineData('\u007F')] // DEL
    [InlineData('\u0085')] // NEL — a C1 control that survives a mangled Windows-1252 decode
    [InlineData('\u009F')] // APC
    public void Meaningless_control_characters_are_removed(char control)
    {
        var parts = EmailBodyNormalizer.Normalize($"Please quote{control} 5 EA of ABC-123");

        Assert.DoesNotContain(control, parts.Fresh);
        Assert.Contains("ABC-123", parts.Fresh);
    }

    [Fact]
    public void Ordinary_whitespace_is_untouched()
    {
        // The line splitting downstream depends on these, and the inspector accepts them.
        var parts = EmailBodyNormalizer.Normalize("Line one\r\nLine two\n\tindented");

        Assert.Contains("Line one", parts.Fresh);
        Assert.Contains("Line two", parts.Fresh);
        Assert.Contains('\t', parts.Fresh);
    }

    [Fact]
    public void A_clean_body_is_returned_unchanged()
    {
        const string clean = "Please quote 5 EA of ABC-123.";
        Assert.Equal(clean, EmailBodyNormalizer.SanitizeControlCharacters(clean));
    }

    [Fact]
    public void The_generated_body_file_now_PASSES_the_inspection_that_refused_it()
    {
        // THE REGRESSION, end to end: normalize an Outlook-style body, assemble it exactly as
        // the manifest planner does, and put it through the real inspector.
        var parts = EmailBodyNormalizer.Normalize(
            $"Subject line{VerticalTab}Please quote:{VerticalTab}5 EA ABC-123{VerticalTab}10 EA XYZ-900\u0000");

        var bodyDocument =
            $"Subject: Test\nFrom: buyer@example.com\nDate: 2026-08-17\n\n{parts.Fresh}";
        var bytes = Encoding.UTF8.GetBytes(bodyDocument);

        // Must not throw. Before the fix this raised UnsafeArchiveException, held the
        // component, and with it the whole message.
        var service = new DocumentFileInspectionService(new EicarMalwareScanner());
        using var stream = new MemoryStream(bytes, writable: false);
        var detection = service.InspectAsync(new FileInspectionRequest(
            stream, "email_body.txt", DeclaredLength: bytes.LongLength)).GetAwaiter().GetResult();

        Assert.True(detection.IsCleared,
            $"The generated body file was refused: {detection.Reason}");
        Assert.Equal("text/plain", detection.DetectedContentType);
    }
}
