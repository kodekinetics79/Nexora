using System.ComponentModel.DataAnnotations;
using ERP_RFQ_Automation.Notifications.Runtime;
using ERP_RFQ_Automation.Platform.Auth;
using ERP_RFQ_Automation.Platform.Services;
using ERP_RFQ_Automation.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ERP_RFQ_Automation.Email;

/// <summary>
/// What to connect to. Every field is optional: what is supplied overrides the stored platform
/// settings for this one attempt, and what is not is inherited — so an operator can test a new host
/// without re-typing a password the console never received and cannot display.
/// </summary>
public sealed class PlatformConnectionTestRequest
{
    /// <summary>A catalogue key. Supplying it fills host, port and TLS mode from the provider's own
    /// published settings, which is the whole point of the catalogue: the operator should not have
    /// to find them.</summary>
    [StringLength(32)]
    public string? ProviderKey { get; set; }

    [StringLength(253)]
    public string? Host { get; set; }

    [Range(1, 65535)]
    public int? Port { get; set; }

    /// <summary>None, StartTls or Implicit. Omitted, it is taken from the provider preset, and
    /// failing that inferred from the stored <c>SmtpEnableSsl</c> flag and the port.</summary>
    public MailTlsMode? Tls { get; set; }

    [StringLength(320)]
    public string? Username { get; set; }

    /// <summary>Omitted or empty means USE THE STORED CREDENTIAL. The console is never given the
    /// stored password, so requiring it here would make it impossible to test a port change.</summary>
    [StringLength(512)]
    public string? Password { get; set; }
}

/// <summary>
/// Staged connection diagnosis for the platform's own outbound mail, and the provider catalogue it
/// is chosen from.
///
/// <para><b>Why this exists next to <c>test-send</c>.</b> A verification send answers "did a message
/// get out", which is the right question once everything works and a useless one while it does not:
/// a blocked port, a TLS-mode mismatch, an app password the provider refuses and a mailbox with
/// SMTP submission disabled all arrive at the console as one failed send. The tenant mailbox screen
/// has had a staged answer to that since the mailbox admin surface was built. A platform operator
/// diagnosing the product's own relay deserves the same answer, from the same service — which is
/// what <see cref="IMailConnectionTester"/> is.</para>
///
/// <para><b>Nothing is sent.</b> The test authenticates and disconnects. That is what makes it safe
/// to run against a live production relay at any hour.</para>
///
/// <para><b>Owner, like every other write on this surface.</b> Reading the catalogue is open to the
/// whole platform scope — it is a table of published hostnames — but the connection test makes this
/// server open a socket to an operator-chosen host and present credentials to it, and reports
/// timings and error detail back. That is an Owner-rank act for the same reason the test send
/// is.</para>
/// </summary>
[ApiController]
[Route("api/platform/notifications/email")]
[Authorize(Policy = PlatformPolicies.PlatformScope)]
public sealed class PlatformEmailConnectionController(
    IMailConnectionTester tester,
    IPlatformAuditService audit,
    IOutboundEmailSettingsSource? storedSettings = null) : ControllerBase
{
    /// <summary>Whole-request ceiling. Six network stages against a black-holed host must not hold
    /// a request thread indefinitely.</summary>
    private static readonly TimeSpan TestDeadline = TimeSpan.FromSeconds(45);

    public const string ConnectionTestAction = "platform.email.connection-test";

    /// <summary>The providers the platform can send through, with their presets and the guidance
    /// that stops an operator hunting through help pages.</summary>
    // GET /api/platform/notifications/email/providers
    [HttpGet("providers")]
    public ActionResult<IReadOnlyList<EmailProviderCapabilityDto>> Providers()
        => Ok(EmailProviderCatalog.ForPlatformOutbound.Select(x => x.ToDto()).ToList());

    // POST /api/platform/notifications/email/connection-test
    [HttpPost("connection-test")]
    [Authorize(Policy = PlatformPolicies.Owner)]
    public async Task<ActionResult<MailConnectionTestResult>> ConnectionTest(
        [FromBody] PlatformConnectionTestRequest request, CancellationToken ct)
    {
        if (!ModelState.IsValid) return BadRequest(ModelState);

        var stored = storedSettings is null ? null : await storedSettings.ReadAsync(ct);
        var provider = EmailProviderCatalog.Find(request.ProviderKey);
        var preset = provider?.OutboundSmtp ?? provider?.OutboundApi;

        var host = First(request.Host, preset?.Host, stored?.SmtpHost);
        if (string.IsNullOrWhiteSpace(host))
            return BadRequest("No host to test. Supply a host, or choose a provider, or save an SMTP configuration first.");

        var port = request.Port ?? preset?.Port ?? (stored?.SmtpPort is > 0 ? stored.SmtpPort : 587);
        var transport = preset?.Transport ?? MailTransport.Smtp;
        var tls = request.Tls ?? preset?.Tls ?? InferTls(port, stored?.SmtpEnableSsl ?? true);

        // Empty is not "no password" — it is "use the one you already hold". The console renders an
        // empty box because it is never given the value, so treating empty as a deliberate blank
        // would make every port change untestable.
        var secret = string.IsNullOrEmpty(request.Password) ? stored?.SmtpPassword ?? string.Empty : request.Password;
        var username = First(request.Username, stored?.SmtpUsername) ?? string.Empty;

        // "Use the one you already hold" only works when one is held. Before the first save there
        // is no stored row at all — which is exactly the state an operator setting this up for the
        // first time is in — and the probe authenticates unconditionally, so an empty box became
        // AUTH with an empty password and came back as "the mail server rejected the username or
        // password". That sends the operator to re-check a credential they were never asked for.
        // Say what is actually missing instead.
        if (!string.IsNullOrWhiteSpace(username) && string.IsNullOrEmpty(secret))
            return BadRequest(
                "No password to test with. A username is set but nothing is stored yet, so type the " +
                "password into the form before testing the connection.");

        if (!MailEndpointPolicy.IsAllowedEndpoint(host, port))
            return BadRequest("That host is not an address this server may connect to.");

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TestDeadline);

        var result = await tester.TestAsync(
            new MailConnectionTestRequest(
                MailDirection.Outbound, transport, host, port, tls, username, secret, provider?.Key),
            deadline.Token);

        // Audited because it makes this server open a socket to an operator-chosen host. The
        // outcome and the endpoint are recorded; the credential obviously is not, and neither is
        // the username, which for several providers IS the API key.
        await audit.WriteAsync(User, ConnectionTestAction, "PlatformEmailSettings", null,
            new
            {
                host,
                port,
                transport = transport.ToString(),
                tls = tls.ToString(),
                providerKey = result.ProviderKey,
                usedStoredCredential = string.IsNullOrEmpty(request.Password),
                succeeded = result.Succeeded,
                failedStage = result.Steps
                    .FirstOrDefault(x => x.Status == Mailbox.MailboxProbeStatus.Failed)?.Stage.ToString()
            },
            actAsTenantId: null, httpContext: HttpContext, ct: ct);

        // Always 200: a refused connection is a successful diagnosis, not a failed request, and the
        // console renders the staged report either way.
        return Ok(result);
    }

    /// <summary>
    /// The TLS mode a stored configuration means, when the operator did not choose one.
    ///
    /// <para>Mirrors <c>SmtpEmailSender</c>, which derives the mode from the port rather than from a
    /// second setting — 465 is implicit TLS, everything else is STARTTLS. A test that inferred it
    /// differently from the sender would pass while production hangs, which is worse than no
    /// test.</para>
    /// </summary>
    internal static MailTlsMode InferTls(int port, bool enableSsl) =>
        !enableSsl ? MailTlsMode.None
        : port == 465 ? MailTlsMode.Implicit
        : MailTlsMode.StartTls;

    private static string? First(params string?[] candidates) =>
        candidates.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));
}
