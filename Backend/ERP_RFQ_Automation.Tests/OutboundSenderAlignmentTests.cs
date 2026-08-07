using ERP_RFQ_Automation.Email;
using ERP_RFQ_Automation.Notifications;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// A From address that is not the authenticated mailbox is the configuration that tests clean and
/// then delivers nothing.
///
/// <para><b>The failure this pins.</b> Sending activation links as <c>info@</c> while holding the
/// password for <c>zack@</c> is the natural thing to set up — one is the address customers should
/// see, the other is the mailbox you have credentials for. A mailbox host refuses it, and refuses
/// it at SEND time: the connection test authenticates and disconnects without ever stating a From
/// address, so it passes. Every tenant provisioned in between reports success while its founding
/// administrator receives nothing, and nothing in the console says why.</para>
/// </summary>
public sealed class OutboundSenderAlignmentTests
{
    private static NotificationsOptions Smtp(string host, string from, string? username) =>
        new()
        {
            Provider = "smtp",
            FromAddress = from,
            Smtp = new SmtpOptions { Host = host, Port = 465, Username = username, Password = "x" },
            // Live is the production posture and produces its own (separate) warning. Pinned to
            // AllowListOnly here so the assertions below are about sender alignment alone.
            OutboundGuard = new OutboundEmailGuardOptions
            {
                Mode = "AllowListOnly", AllowedRecipients = ["ops@nexora.example"]
            }
        };

    private static bool WarnsAboutSender(NotificationsOptions options) =>
        options.Validate().Any(w => w.Contains("not the mailbox being signed in as"));

    [Fact]
    public void A_mailbox_host_warns_when_the_sender_is_not_the_account_being_signed_in_as()
    {
        // The exact configuration the product was about to ship with.
        Assert.True(WarnsAboutSender(
            Smtp("smtpout.secureserver.net", "info@kodekinetics.com", "zack@kodekinetics.com")));

        // Named, so the operator knows where to go and add the alias.
        var warning = Smtp("smtpout.secureserver.net", "info@kodekinetics.com", "zack@kodekinetics.com")
            .Validate().Single(w => w.Contains("not the mailbox being signed in as"));
        Assert.Contains("GoDaddy", warning);
        Assert.Contains("info@kodekinetics.com", warning);
        Assert.Contains("zack@kodekinetics.com", warning);
    }

    [Theory]
    [InlineData("smtp.office365.com")]
    [InlineData("smtp.gmail.com")]
    [InlineData("smtp.zoho.com")]
    public void Every_mailbox_hosting_provider_in_the_catalogue_is_covered(string host)
        => Assert.True(WarnsAboutSender(Smtp(host, "billing@customer.example", "ap@customer.example")));

    [Theory]
    [InlineData("smtp.sendgrid.net")]
    [InlineData("smtp.postmarkapp.com")]
    [InlineData("smtp.mailgun.org")]
    public void A_relay_is_never_warned_about_because_sending_as_other_addresses_is_its_purpose(string host)
    {
        // SendGrid's username is the literal string "apikey" — it will NEVER equal the From
        // address, and a warning here would fire on every correctly configured relay until the
        // operator learned to ignore the banner entirely.
        Assert.False(WarnsAboutSender(Smtp(host, "no-reply@nexora.example", "apikey")));
    }

    [Fact]
    public void An_unrecognised_host_is_left_alone_rather_than_guessed_at()
    {
        // It might be a corporate relay that is supposed to send for the whole domain. A warning
        // fired on a guess costs more credibility than it buys.
        Assert.False(WarnsAboutSender(Smtp("mail.internal.example", "no-reply@x.example", "relay-user")));
    }

    [Fact]
    public void Matching_addresses_are_silent_regardless_of_case_or_padding()
    {
        Assert.False(WarnsAboutSender(
            Smtp("smtpout.secureserver.net", " Info@KodeKinetics.com ", "info@kodekinetics.com")));
    }

    [Fact]
    public void Anonymous_relay_is_not_a_mismatch()
    {
        // No username means no authenticated identity to disagree with. An internal smarthost that
        // requires no credential is a legitimate configuration, not a half-filled form.
        Assert.False(WarnsAboutSender(Smtp("smtpout.secureserver.net", "info@kodekinetics.com", null)));
        Assert.False(WarnsAboutSender(Smtp("smtpout.secureserver.net", "info@kodekinetics.com", "")));
    }

    [Fact]
    public void The_rule_is_derived_from_what_a_provider_IS_not_from_a_flag_somebody_remembered_to_set()
    {
        foreach (var provider in EmailProviderCatalog.All)
        {
            // A provider that both receives and sends hosts a MAILBOX: one credential, one
            // address. A send-only provider is a RELAY, authorised for whole domains.
            Assert.Equal(provider.SupportsTenantMailbox, provider.RequiresSenderMatchesMailbox);

            if (provider.RequiresSenderMatchesMailbox)
                Assert.True(provider.SupportsInbound && provider.OutboundSmtp is not null);
        }

        // And the catalogue genuinely contains both kinds, so the assertion above is not vacuous.
        Assert.Contains(EmailProviderCatalog.All, p => p.RequiresSenderMatchesMailbox);
        Assert.Contains(EmailProviderCatalog.All, p => !p.RequiresSenderMatchesMailbox && p.SupportsOutbound);
    }
}
