using System.Reflection;
using ERP_RFQ_Automation.Mailbox;
using ERP_RFQ_Automation.Security;
using MailKit.Security;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// The mailbox administration surface lets a tenant administrator tell this server which host to
/// connect to, with which credential. That makes it the highest-leverage screen in the product
/// from a security standpoint, and these tests pin the three things that must not regress: the
/// credential never travels outbound, the server cannot be aimed at internal infrastructure, and
/// the connection test reflects what the RUNTIME will actually do.
/// </summary>
public sealed class MailboxAdministrationTests
{
    // ---- the credential must never leave ------------------------------------------------

    [Fact]
    public void The_mailbox_response_carries_no_password_field_of_any_kind()
    {
        // Not a style check. EmailConfiguration.Password is decrypted transparently by the value
        // converter, so ANY password-shaped property on the outbound DTO — masked, nulled, or
        // "write-only" — is one careless mapping away from serialising a live customer mailbox
        // credential to the browser. The safe design is that the field does not exist, and that
        // is a property a test can hold.
        var offenders = typeof(MailboxResponseDTO)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.Name.Contains("password", StringComparison.OrdinalIgnoreCase)
                     || p.Name.Contains("secret", StringComparison.OrdinalIgnoreCase)
                     || p.Name.Contains("credential", StringComparison.OrdinalIgnoreCase)
                        && p.PropertyType == typeof(string))
            .Select(p => p.Name)
            .ToList();

        Assert.Empty(offenders);
    }

    [Fact]
    public void The_probe_request_is_the_only_type_that_carries_a_password_and_it_is_inbound_only()
    {
        // Inbound DTOs legitimately carry a password. This asserts the direction: the types the
        // controller RETURNS have none.
        Assert.Contains(typeof(MailboxCreateRequestDTO).GetProperties(), p => p.Name == "Password");
        Assert.DoesNotContain(typeof(OutboundMailStatusDTO).GetProperties(),
            p => p.Name.Contains("Password", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(typeof(MailboxProbeResult).GetProperties(),
            p => p.Name.Contains("Password", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(typeof(MailboxProbeStep).GetProperties(),
            p => p.Name.Contains("Password", StringComparison.OrdinalIgnoreCase));
    }

    // ---- SSRF ---------------------------------------------------------------------------

    [Theory]
    // The cloud metadata endpoint: the single highest-value SSRF target, since it hands out the
    // instance's own credentials to anything that can issue a plain HTTP GET from the host.
    [InlineData("169.254.169.254", 993)]
    [InlineData("127.0.0.1", 993)]
    [InlineData("localhost", 993)]
    [InlineData("MAIL.LOCALHOST", 993)]
    [InlineData("10.0.0.5", 143)]
    [InlineData("192.168.1.10", 587)]
    [InlineData("172.16.4.4", 25)]
    [InlineData("[::1]", 993)]
    [InlineData("0.0.0.0", 993)]
    [InlineData("mail.example.com", 0)]
    [InlineData("mail.example.com", 70000)]
    [InlineData("", 993)]
    [InlineData(null, 993)]
    public void Endpoints_that_would_let_a_tenant_reach_internal_infrastructure_are_refused(string? host, int port)
        => Assert.False(MailEndpointPolicy.IsAllowedEndpoint(host, port));

    [Theory]
    [InlineData("outlook.office365.com", 993)]
    [InlineData("imap.gmail.com", 993)]
    [InlineData("smtp.office365.com", 587)]
    [InlineData("mail.yourcompany.com.", 465)]   // trailing dot is a legal FQDN
    [InlineData("8.8.8.8", 993)]                 // a public literal is allowed
    public void Ordinary_public_mail_endpoints_are_permitted(string host, int port)
        => Assert.True(MailEndpointPolicy.IsAllowedEndpoint(host, port));

    [Fact]
    public void Loopback_is_refused_in_every_environment_including_development()
    {
        // A local mail sink is genuinely convenient in development, which is exactly why the
        // temptation to add an environment-conditional bypass exists. An SSRF control with an
        // environment flag is one misconfigured deploy away from being no control at all, so
        // there is deliberately no overload that takes an environment.
        Assert.False(MailEndpointPolicy.IsAllowedEndpoint("localhost", 1025));
        Assert.False(MailEndpointPolicy.IsAllowedEndpoint("127.0.0.1", 1025));
        Assert.DoesNotContain(typeof(MailEndpointPolicy).GetMethods(),
            m => m.Name == nameof(MailEndpointPolicy.IsAllowedEndpoint) &&
                 m.GetParameters().Any(p => p.ParameterType == typeof(bool)));
    }

    // ---- the probe must not lie about what the runtime does ------------------------------

    [Fact]
    public void The_probe_reproduces_the_runtimes_own_reading_of_UseSsl_per_protocol()
    {
        // UseSsl is interpreted differently by the IMAP poller and the SMTP transport. That is a
        // wart, but a probe that "corrects" it would pass while the poller fails — the worst
        // possible outcome for a connection test. These four expectations are copied from the
        // call sites: EmailService.FetchEmails and MailKitOutboundSmtpTransport.SendAsync.
        Assert.Equal(SecureSocketOptions.SslOnConnect, MailboxConnectionProbe.SecurityFor("IMAP", true));
        Assert.Equal(SecureSocketOptions.None, MailboxConnectionProbe.SecurityFor("IMAP", false));
        Assert.Equal(SecureSocketOptions.SslOnConnect, MailboxConnectionProbe.SecurityFor("SMTP", true));
        Assert.Equal(SecureSocketOptions.StartTls, MailboxConnectionProbe.SecurityFor("SMTP", false));
    }

    [Fact]
    public void An_unencrypted_IMAP_mailbox_is_reported_as_sending_the_password_in_clear()
    {
        // IMAP with UseSsl off connects with SecureSocketOptions.None — genuinely no encryption,
        // so the mailbox password crosses the network readable. An operator ticking a box labelled
        // only "use SSL" has no way to know that; the screen has to say it.
        Assert.Equal(SecureSocketOptions.None, MailboxConnectionProbe.SecurityFor("IMAP", useSsl: false));

        // SMTP with the same setting still negotiates STARTTLS, so it is NOT in the clear. The
        // distinction is why this is reported per-row rather than derived from UseSsl in the UI.
        Assert.NotEqual(SecureSocketOptions.None, MailboxConnectionProbe.SecurityFor("SMTP", useSsl: false));
    }

    [Theory]
    [InlineData("IMAP", 993, false)]   // implicit-TLS port with encryption off
    [InlineData("SMTP", 465, false)]
    [InlineData("IMAP", 143, true)]    // STARTTLS port with implicit TLS on
    [InlineData("SMTP", 587, true)]
    public void A_port_that_disagrees_with_the_encryption_setting_is_flagged(string protocol, int port, bool useSsl)
    {
        var advice = MailboxConnectionProbe.EncryptionAdvice(protocol, port, useSsl);

        Assert.NotNull(advice);
        Assert.Equal(MailboxProbeStatus.Warning, advice.Status);
        Assert.False(string.IsNullOrWhiteSpace(advice.Remedy));
    }

    [Theory]
    [InlineData("IMAP", 993, true)]
    [InlineData("IMAP", 143, false)]
    [InlineData("SMTP", 465, true)]
    [InlineData("SMTP", 587, false)]
    public void Matching_port_and_encryption_settings_produce_no_warning(string protocol, int port, bool useSsl)
        => Assert.Null(MailboxConnectionProbe.EncryptionAdvice(protocol, port, useSsl));

    // ---- probe reporting ----------------------------------------------------------------

    [Fact]
    public async Task A_refused_endpoint_fails_at_the_policy_stage_and_skips_the_rest()
    {
        // The distinction that makes the report usable: one real failure, and five stages plainly
        // marked "not checked". Reporting six failures would send the operator chasing five
        // problems that do not exist.
        var result = await new MailboxConnectionProbe().ProbeAsync(
            new MailboxProbeRequest("IMAP", "169.254.169.254", 993, "u", "p", true),
            CancellationToken.None);

        Assert.False(result.Succeeded);

        var policy = Assert.Single(result.Steps, s => s.Stage == MailboxProbeStage.Policy);
        Assert.Equal(MailboxProbeStatus.Failed, policy.Status);
        Assert.False(string.IsNullOrWhiteSpace(policy.Remedy));

        Assert.All(result.Steps.Where(s => s.Stage != MailboxProbeStage.Policy),
            s => Assert.Equal(MailboxProbeStatus.Skipped, s.Status));
    }

    [Fact]
    public async Task Every_probe_reports_all_six_stages_in_a_fixed_order()
    {
        // A ragged list forces the reader to work out which checks ran. A fixed six-row report
        // reads the same way every time, whichever stage failed.
        var result = await new MailboxConnectionProbe().ProbeAsync(
            new MailboxProbeRequest("SMTP", "127.0.0.1", 587, "u", "p", false),
            CancellationToken.None);

        Assert.Equal(Enum.GetValues<MailboxProbeStage>(), result.Steps.Select(s => s.Stage).ToArray());
    }

    [Fact]
    public async Task A_failed_probe_never_echoes_the_password_back_to_the_caller()
    {
        const string secret = "sup3r-s3cret-mailbox-password";
        var result = await new MailboxConnectionProbe().ProbeAsync(
            new MailboxProbeRequest("IMAP", "10.1.2.3", 993, "operator@tenant.sa", secret, true),
            CancellationToken.None);

        var rendered = System.Text.Json.JsonSerializer.Serialize(result);
        Assert.DoesNotContain(secret, rendered, StringComparison.Ordinal);
    }

    // ---- presets ------------------------------------------------------------------------

    [Fact]
    public void Every_provider_preset_points_at_an_endpoint_the_policy_would_actually_permit()
    {
        // A preset that the SSRF policy then refuses would be a guaranteed dead end presented as
        // a recommended default. "custom" carries empty hosts by design and is excluded.
        var presets = typeof(MailboxProbeResult).Assembly
            .GetType("ERP_RFQ_Automation.Controllers.MailboxPresets")!
            .GetField("All", BindingFlags.Public | BindingFlags.Static)!
            .GetValue(null) as IReadOnlyList<MailboxPresetDTO>;

        Assert.NotNull(presets);
        foreach (var preset in presets.Where(p => !string.IsNullOrEmpty(p.ImapHost)))
        {
            Assert.True(MailEndpointPolicy.IsAllowedEndpoint(preset.ImapHost, preset.ImapPort),
                $"IMAP preset '{preset.Key}' is refused by the endpoint policy.");
            Assert.True(MailEndpointPolicy.IsAllowedEndpoint(preset.SmtpHost, preset.SmtpPort),
                $"SMTP preset '{preset.Key}' is refused by the endpoint policy.");
            Assert.False(string.IsNullOrWhiteSpace(preset.Guidance));
        }
    }
}
