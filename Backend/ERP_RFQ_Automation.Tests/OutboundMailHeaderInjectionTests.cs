using ERP_RFQ_Automation.Notifications;
using ERP_RFQ_Automation.Notifications.Providers;
using MimeKit;
using Xunit;

/// <summary>
/// Security audit 2026-09-04, lane 3. The From display name of a tenant send is now the
/// business-unit NAME — tenant-controlled free text — and Reply-To can carry a user's profile
/// name. This pins that a CRLF in any of those, or in the Subject, cannot smuggle an extra header
/// (a Bcc, an X-header) into the message MimeKit serialises. It is a PIN on library behaviour,
/// not a regression test for a fix: MimeKit RFC-2047-encodes control characters in phrases and
/// strips them from Subject. If a future change bypasses <see cref="MimeMessageComposer"/> and
/// concatenates headers by hand, this is the test that goes red.
/// </summary>
public sealed class OutboundMailHeaderInjectionTests
{
    [Fact]
    public void A_crlf_in_display_names_or_subject_cannot_add_a_header()
    {
        var message = new EmailMessage
        {
            Subject = "Quote 42\r\nBcc: attacker@evil.test",
            HtmlBody = "<p>hi</p>",
            ReplyTo = new EmailAddress("rep@tenant.test", "Rep\r\nBcc: attacker2@evil.test"),
        };
        message.AddTo("customer@example.test", "Customer\r\nBcc: attacker3@evil.test");
        var from = new EmailAddress(
            "info@tenant.test",
            "Noor & Sons, Ltd <x@y>\r\nBcc: attacker4@evil.test\r\nX-Injected: yes");

        using var mime = MimeMessageComposer.Compose(message, "<id@test>", from, null);
        using var wire = new MemoryStream();
        mime.WriteTo(wire);
        wire.Position = 0;
        var parsed = MimeMessage.Load(wire);

        Assert.Empty(parsed.Bcc);
        Assert.False(parsed.Headers.Contains("X-Injected"));
        Assert.Equal("info@tenant.test", Assert.Single(parsed.From.Mailboxes).Address);
        Assert.Equal("customer@example.test", Assert.Single(parsed.To.Mailboxes).Address);
        Assert.Equal("rep@tenant.test", Assert.Single(parsed.ReplyTo.Mailboxes).Address);
        Assert.DoesNotContain("\n", parsed.Subject);
    }
}
