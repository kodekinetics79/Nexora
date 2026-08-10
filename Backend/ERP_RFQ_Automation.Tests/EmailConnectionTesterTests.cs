using System.Reflection;
using System.Text.Json;
using ERP_RFQ_Automation.Email;
using ERP_RFQ_Automation.Mailbox;
using MailKit.Security;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// One connection test, for both directions and both scopes. These tests pin the three properties
/// that make it worth having: it cannot be aimed at internal infrastructure, it never echoes the
/// credential it was given, and it hands back the provider's own explanation of what failed rather
/// than a generic "could not connect".
/// </summary>
public sealed class EmailConnectionTesterTests
{
    private static MailConnectionTester Tester() => new(new MailboxConnectionProbe());

    // ---- SSRF ----------------------------------------------------------------------------

    [Theory]
    // The cloud metadata endpoint, which hands out the instance's own credentials to anything that
    // can reach it from the host.
    [InlineData(MailTransport.Smtp, "169.254.169.254", 465)]
    [InlineData(MailTransport.Imap, "127.0.0.1", 993)]
    [InlineData(MailTransport.Smtp, "10.0.0.5", 587)]
    [InlineData(MailTransport.Smtp, "localhost", 465)]
    // The HTTPS submission path is a separate branch through the tester and would otherwise be an
    // unguarded second door into the same private network.
    [InlineData(MailTransport.HttpApi, "169.254.169.254", 443)]
    [InlineData(MailTransport.HttpApi, "192.168.1.10", 443)]
    public async Task Neither_direction_can_be_aimed_at_internal_infrastructure(
        MailTransport transport, string host, int port)
    {
        var result = await Tester().TestAsync(
            new MailConnectionTestRequest(
                MailDirection.Outbound, transport, host, port, MailTlsMode.Implicit, "u", "p"),
            CancellationToken.None);

        Assert.False(result.Succeeded);

        var policy = Assert.Single(result.Steps, x => x.Stage == MailboxProbeStage.Policy);
        Assert.Equal(MailboxProbeStatus.Failed, policy.Status);
        Assert.False(string.IsNullOrWhiteSpace(policy.Remedy));
    }

    [Fact]
    public async Task A_refused_endpoint_reports_one_real_failure_and_five_unchecked_stages()
    {
        // Reporting six failures when only the first is real sends the operator chasing five
        // problems that do not exist.
        var result = await Tester().TestAsync(
            new MailConnectionTestRequest(
                MailDirection.Outbound, MailTransport.HttpApi, "127.0.0.1", 443, MailTlsMode.Implicit, "", "key"),
            CancellationToken.None);

        Assert.All(result.Steps.Where(x => x.Stage != MailboxProbeStage.Policy),
            x => Assert.Equal(MailboxProbeStatus.Skipped, x.Status));
    }

    [Fact]
    public async Task Both_transports_report_the_same_six_stages_in_the_same_order()
    {
        // A tenant testing their mailbox and an operator testing the platform relay must get the
        // same shaped answer, or the two screens end up with two different qualities of diagnosis.
        var mail = await Tester().TestAsync(
            new MailConnectionTestRequest(
                MailDirection.Inbound, MailTransport.Imap, "10.1.2.3", 993, MailTlsMode.Implicit, "u", "p"),
            CancellationToken.None);

        var api = await Tester().TestAsync(
            new MailConnectionTestRequest(
                MailDirection.Outbound, MailTransport.HttpApi, "10.1.2.3", 443, MailTlsMode.Implicit, "", "k"),
            CancellationToken.None);

        var expected = Enum.GetValues<MailboxProbeStage>();
        Assert.Equal(expected, mail.Steps.Select(x => x.Stage).ToArray());
        Assert.Equal(expected, api.Steps.Select(x => x.Stage).ToArray());
    }

    // ---- the credential never comes back ---------------------------------------------------

    [Fact]
    public async Task A_failed_test_never_echoes_the_secret_it_was_given()
    {
        const string secret = "sup3r-s3cret-app-password";

        foreach (var transport in new[] { MailTransport.Smtp, MailTransport.Imap, MailTransport.HttpApi })
        {
            var result = await Tester().TestAsync(
                new MailConnectionTestRequest(
                    MailDirection.Outbound, transport, "10.1.2.3", 465, MailTlsMode.Implicit,
                    "operator@tenant.sa", secret),
                CancellationToken.None);

            Assert.DoesNotContain(secret, JsonSerializer.Serialize(result), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void The_result_type_has_no_property_that_could_carry_a_secret()
    {
        // Structural rather than behavioural: the leak would reappear the moment somebody added a
        // "credentialUsed" field for debugging.
        Assert.DoesNotContain(
            typeof(MailConnectionTestResult).GetProperties(BindingFlags.Public | BindingFlags.Instance),
            p => p.Name.Contains("password", StringComparison.OrdinalIgnoreCase) ||
                 p.Name.Contains("secret", StringComparison.OrdinalIgnoreCase) ||
                 p.Name.Contains("apikey", StringComparison.OrdinalIgnoreCase));
    }

    // ---- the TLS translation, which is where the old bugs lived ----------------------------

    [Theory]
    // SMTP reads false as STARTTLS, so a 587 endpoint must NOT set the flag.
    [InlineData(MailTransport.Smtp, MailTlsMode.StartTls, false)]
    [InlineData(MailTransport.Smtp, MailTlsMode.Implicit, true)]
    [InlineData(MailTransport.Smtp, MailTlsMode.None, false)]
    // IMAP reads false as NO ENCRYPTION. Asking for STARTTLS on IMAP therefore cannot be honoured
    // by the runtime, and the tester keeps the flag ON rather than silently sending the mailbox
    // password in the clear — the probe's own port/mode warning then explains the mismatch.
    [InlineData(MailTransport.Imap, MailTlsMode.StartTls, true)]
    [InlineData(MailTransport.Imap, MailTlsMode.Implicit, true)]
    [InlineData(MailTransport.Imap, MailTlsMode.None, false)]
    public void The_TLS_mode_maps_onto_the_runtimes_own_UseSsl_interpretation(
        MailTransport transport, MailTlsMode tls, bool expected)
        => Assert.Equal(expected, MailConnectionTester.UseSslFor(transport, tls));

    [Theory]
    // The distinction the UseSsl bool above cannot carry, and the reason the probe is now told the
    // mode instead of re-deriving it. "Encrypted off" on the platform email screen produced
    // MailTlsMode.None, which collapsed to UseSsl=false, which the probe re-expanded to STARTTLS:
    // the test negotiated TLS and passed, and SmtpEmailSender then sent the password in the clear.
    [InlineData(MailTlsMode.None, SecureSocketOptions.None)]
    [InlineData(MailTlsMode.StartTls, SecureSocketOptions.StartTls)]
    [InlineData(MailTlsMode.Implicit, SecureSocketOptions.SslOnConnect)]
    public void The_requested_TLS_mode_is_probed_exactly_as_asked(
        MailTlsMode tls, SecureSocketOptions expected)
        => Assert.Equal(expected, MailConnectionTester.SecurityFor(tls));

    [Fact]
    public void No_tenant_mailbox_case_changes_when_the_mode_is_honoured_rather_than_inferred()
    {
        // MailboxController encodes the tenant runtime's own semantics in the mode it asks for, so
        // honouring the request must reproduce exactly what inference produced — otherwise this
        // fix would have moved the tenant mailbox probe as a side effect.
        foreach (var (protocol, useSsl, requested) in new (string, bool, MailTlsMode)[]
                 {
                     (MailboxConnectionProbe.Smtp, true, MailTlsMode.Implicit),
                     (MailboxConnectionProbe.Smtp, false, MailTlsMode.StartTls),
                     (MailboxConnectionProbe.Imap, true, MailTlsMode.Implicit),
                     (MailboxConnectionProbe.Imap, false, MailTlsMode.None),
                 })
        {
            Assert.Equal(
                MailboxConnectionProbe.SecurityFor(protocol, useSsl),
                MailConnectionTester.SecurityFor(requested));
        }
    }

    [Fact]
    public void Every_catalogue_preset_round_trips_through_the_translation_unchanged()
    {
        // The preset declares a UseSsl value and the tester derives one. If they ever disagree, the
        // connection test would be exercising something other than what the mailbox row stores —
        // which is exactly the class of defect that makes a green test and a broken poller.
        foreach (var provider in EmailProviderCatalog.All)
        foreach (var preset in provider.Presets.Where(x => x.IsMailProtocol))
            Assert.Equal(preset.UseSsl, MailConnectionTester.UseSslFor(preset.Transport, preset.Tls));
    }

    // ---- provider guidance -----------------------------------------------------------------

    [Fact]
    public async Task An_unreachable_Microsoft_365_host_still_infers_its_provider()
    {
        // The provider is attached even when the connection never got far enough to prove anything,
        // so the console can show the right guidance beside the failure.
        var result = await Tester().TestAsync(
            new MailConnectionTestRequest(
                MailDirection.Outbound, MailTransport.Smtp, "smtp.office365.com", 587,
                MailTlsMode.StartTls, "u@tenant.sa", "p", ProviderKey: "microsoft365"),
            CancellationToken.None);

        Assert.Equal("microsoft365", result.ProviderKey);
        Assert.Equal("Microsoft 365 / Outlook", result.ProviderDisplayName);
    }

    [Fact]
    public void An_authentication_failure_against_Microsoft_365_names_the_setting_that_causes_it()
    {
        // The sentence that resolves the ticket. Correct credentials are refused because SMTP
        // submission is off per mailbox by default, and no amount of retyping the password fixes
        // it — but nothing in a generic "the mail server rejected these credentials" says so.
        var notes = NotesFor("microsoft365", MailDirection.Outbound, MailboxProbeStage.Authentication);

        Assert.Contains(notes, x => x.Contains("SMTP submission", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(notes, x => x.Contains("app-specific password", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void An_inbound_authentication_failure_reports_what_must_be_enabled_at_the_provider()
    {
        var notes = NotesFor("microsoft365", MailDirection.Inbound, MailboxProbeStage.Authentication);
        Assert.Contains(notes, x => x.Contains("IMAP is disabled per mailbox", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void A_successful_outbound_test_warns_about_the_volume_ceiling_it_is_about_to_hit()
    {
        // Surfaced on success on purpose: the ceiling is the failure that arrives weeks later, once
        // the tenant is relying on the mailbox, and it reads as an unrelated outage.
        var notes = NotesFor("godaddy", MailDirection.Outbound, failedStage: null);
        Assert.Contains(notes, x => x.Contains("cap", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void A_successful_test_is_not_cluttered_with_advice_about_failures_that_did_not_happen()
    {
        var notes = NotesFor("microsoft365", MailDirection.Outbound, failedStage: null);
        Assert.DoesNotContain(notes, x => x.Contains("SMTP submission", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void A_host_with_no_known_provider_produces_no_invented_advice()
    {
        var notes = NotesFor(providerKey: null, MailDirection.Outbound, MailboxProbeStage.Authentication);
        Assert.Empty(notes);
    }

    /// <summary>Drives the note selection directly, because reaching an AUTH failure through a real
    /// socket would need a live mail server and a wrong password.</summary>
    private static IReadOnlyList<string> NotesFor(
        string? providerKey, MailDirection direction, MailboxProbeStage? failedStage)
    {
        var steps = Enum.GetValues<MailboxProbeStage>()
            .Select(stage => new MailboxProbeStep(stage,
                stage == failedStage ? MailboxProbeStatus.Failed : MailboxProbeStatus.Passed, "", 0))
            .ToList();

        var result = new MailConnectionTestResult(
            Succeeded: failedStage is null, "", direction, MailTransport.Smtp, "host.example.com", 465,
            MailTlsMode.Implicit, steps, null, null, false, providerKey, null, []);

        return (IReadOnlyList<string>)typeof(MailConnectionTester)
            .GetMethod("Notes", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [EmailProviderCatalog.Find(providerKey), direction, result])!;
    }

    // ---- wire contract ---------------------------------------------------------------------

    [Fact]
    public async Task The_result_stays_a_superset_of_what_the_mailbox_screen_already_reads()
    {
        // /api/Mailbox/test now answers from this service. The deployed screen binds to succeeded,
        // protocol, host, port, summary, steps, negotiatedSecurity, inboxMessageCount and
        // credentialsSentInClear — dropping any of them is a breaking change dressed as a rename.
        var result = await Tester().TestAsync(
            new MailConnectionTestRequest(
                MailDirection.Inbound, MailTransport.Imap, "169.254.169.254", 993, MailTlsMode.Implicit, "u", "p"),
            CancellationToken.None);

        // Serialised the way ASP.NET Core serialises it, camel-cased, because the field names the
        // browser actually receives are what a deployed client is bound to.
        var json = JsonSerializer.Serialize(result,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        foreach (var field in new[]
                 {
                     "succeeded", "protocol", "host", "port", "summary", "steps",
                     "negotiatedSecurity", "inboxMessageCount", "credentialsSentInClear"
                 })
            Assert.True(root.TryGetProperty(field, out _), $"The response no longer carries '{field}'.");

        Assert.Equal("IMAP", root.GetProperty("protocol").GetString());

        // Stage and status still cross the wire as names: this API registers no global string-enum
        // converter, and ordinals silently break the moment a stage is inserted.
        Assert.Contains("\"Policy\"", json, StringComparison.Ordinal);
        Assert.Contains("\"Skipped\"", json, StringComparison.Ordinal);
    }

    // ---- the platform console's inference of a stored configuration -------------------------

    [Theory]
    [InlineData(465, true, MailTlsMode.Implicit)]
    [InlineData(587, true, MailTlsMode.StartTls)]
    [InlineData(25, true, MailTlsMode.StartTls)]
    [InlineData(2525, true, MailTlsMode.StartTls)]
    [InlineData(587, false, MailTlsMode.None)]
    public void A_stored_platform_configuration_is_read_the_same_way_the_sender_reads_it(
        int port, bool enableSsl, MailTlsMode expected)
    {
        // SmtpEmailSender derives implicit TLS from the port alone — 465 and nothing else. A test
        // that inferred it differently would pass while production hangs waiting for a greeting
        // that a TLS listener never sends, which is the exact failure that sender was rebuilt to
        // end.
        Assert.Equal(expected, PlatformEmailConnectionController.InferTls(port, enableSsl));
    }
}
