using System.Diagnostics;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using ERP_RFQ_Automation.Mailbox;
using ERP_RFQ_Automation.Security;

namespace ERP_RFQ_Automation.Email;

/// <summary>
/// What to test. Deliberately not an entity and not tied to a scope: the same value describes an
/// operator testing the platform's SMTP relay and a tenant administrator testing an unsaved mailbox
/// form, which is the point — one service answers both.
/// </summary>
/// <param name="Secret">The password or API key. Inbound only, never echoed: no field on
/// <see cref="MailConnectionTestResult"/> can carry it, and the probe's own report is asserted not
/// to contain it.</param>
/// <param name="ProviderKey">Which catalogue entry this is, when the caller knows. When null it is
/// inferred from the host, so a mailbox saved years before the catalogue existed still gets the
/// provider-specific remedy.</param>
public sealed record MailConnectionTestRequest(
    MailDirection Direction,
    MailTransport Transport,
    string Host,
    int Port,
    MailTlsMode Tls,
    string Username,
    string Secret,
    string? ProviderKey = null);

/// <summary>
/// The staged verdict, in the shape the mailbox screen already renders.
///
/// <para><see cref="Steps"/> is the existing <c>MailboxProbeStep</c> list, unchanged and in the
/// same fixed six-row order, because that report is already correct and already pinned by tests —
/// a second, differently-shaped diagnosis for the outbound direction would give an operator two
/// qualities of answer to the same question.</para>
/// </summary>
public sealed record MailConnectionTestResult(
    bool Succeeded,
    string Summary,
    MailDirection Direction,
    MailTransport Transport,
    string Host,
    int Port,
    MailTlsMode Tls,
    IReadOnlyList<MailboxProbeStep> Steps,
    string? NegotiatedSecurity,
    int? InboxMessageCount,
    bool CredentialsSentInClear,
    string? ProviderKey,
    string? ProviderDisplayName,
    IReadOnlyList<string> ProviderNotes)
{
    /// <summary>
    /// The transport under the name <c>MailboxProbeResult</c> published it.
    ///
    /// <para>Kept so this result is a superset of the one the mailbox screen already consumes:
    /// <c>/api/Mailbox/test</c> now answers from this service, and dropping a field that a
    /// deployed client reads is a breaking change dressed up as a rename.</para>
    /// </summary>
    public string Protocol => Transport switch
    {
        MailTransport.Smtp => MailboxConnectionProbe.Smtp,
        MailTransport.HttpApi => "HTTPS",
        MailTransport.Pop3 => "POP3",
        _ => MailboxConnectionProbe.Imap
    };
}

public interface IMailConnectionTester
{
    Task<MailConnectionTestResult> TestAsync(MailConnectionTestRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// One connection test, for both directions and both scopes.
///
/// <para><b>Why this is a thin layer over <see cref="IMailboxConnectionProbe"/> and not a rewrite.</b>
/// The probe already stages a mail connection correctly — policy, DNS, TCP, TLS, AUTH, mailbox,
/// each timed, each carrying a remedy, and each mirroring the RUNTIME's interpretation of the
/// <c>UseSsl</c> flag rather than a tidier one. Reimplementing that for the outbound path would
/// produce a second interpretation, and the whole value of the probe is that there is only one.
/// What was missing was not the diagnosis; it was that the diagnosis was reachable only from the
/// tenant mailbox controller. A platform operator testing the product's own SMTP relay got a
/// pass/fail from a test SEND, with no way to tell a blocked port from a rejected password.</para>
///
/// <para><b>What it adds.</b> The provider dimension. A failed AUTH against
/// <c>smtp.office365.com</c> is not a wrong password nine times out of ten — it is SMTP submission
/// being disabled per mailbox by default, or MFA refusing a non-app password. The probe cannot know
/// that without a provider catalogue; with one, the answer names the provider's own setting.</para>
///
/// <para><b>The HTTPS submission APIs are tested differently, and say so.</b> SendGrid and Postmark
/// have no AUTH stage a socket can exercise: the key is validated by the first real request. What
/// CAN be proved is reachability and TLS, which is the failure that actually happens in a locked
/// down VPC, and the remaining stages are reported as not-checked rather than as passes.</para>
///
/// <para><b>Every connection goes through <see cref="MailEndpointPolicy"/>.</b> The host is typed
/// by an operator or a tenant administrator, which makes it SSRF input in the strict sense — this
/// endpoint exists to open a socket to it and report what happened, including timings. The probe
/// enforces the policy for the mail protocols; the API path calls it directly.</para>
/// </summary>
public sealed class MailConnectionTester(
    IMailboxConnectionProbe probe,
    ILogger<MailConnectionTester>? logger = null) : IMailConnectionTester
{
    /// <summary>Per-stage ceiling for the HTTPS reachability path, matching the probe's own.</summary>
    private static readonly TimeSpan StageTimeout = TimeSpan.FromSeconds(12);

    public async Task<MailConnectionTestResult> TestAsync(
        MailConnectionTestRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var host = string.IsNullOrWhiteSpace(request.Host)
            ? string.Empty
            : MailEndpointPolicy.Normalize(request.Host);

        var providerKey = request.ProviderKey is { Length: > 0 } && EmailProviderCatalog.Find(request.ProviderKey) is not null
            ? EmailProviderCatalog.Find(request.ProviderKey)!.Key
            : EmailProviderCatalog.InferKeyFromHost(host);
        var provider = EmailProviderCatalog.Find(providerKey);

        var result = request.Transport == MailTransport.HttpApi
            ? await TestHttpsEndpointAsync(request, host, cancellationToken)
            : await TestMailProtocolAsync(request, host, cancellationToken);

        return result with
        {
            ProviderKey = provider?.Key,
            ProviderDisplayName = provider?.DisplayName,
            ProviderNotes = Notes(provider, request.Direction, result)
        };
    }

    /// <summary>
    /// IMAP, POP3 and SMTP all go through the existing probe. The only translation is the TLS mode:
    /// the probe speaks the runtime's <c>UseSsl</c> boolean, and this is the single place that
    /// converts a mode into it — the conversion is asymmetric, and doing it at each call site is how
    /// a 587 row ends up demanding implicit TLS.
    /// </summary>
    private async Task<MailConnectionTestResult> TestMailProtocolAsync(
        MailConnectionTestRequest request, string host, CancellationToken cancellationToken)
    {
        var protocol = request.Transport == MailTransport.Smtp
            ? MailboxConnectionProbe.Smtp
            : MailboxConnectionProbe.Imap;

        var probeResult = await probe.ProbeAsync(
            new MailboxProbeRequest(
                protocol, host, request.Port, request.Username, request.Secret, UseSslFor(request.Transport, request.Tls)),
            cancellationToken);

        return new MailConnectionTestResult(
            probeResult.Succeeded,
            probeResult.Summary,
            request.Direction,
            request.Transport,
            probeResult.Host,
            probeResult.Port,
            request.Tls,
            probeResult.Steps,
            probeResult.NegotiatedSecurity,
            probeResult.InboxMessageCount,
            probeResult.CredentialsSentInClear,
            ProviderKey: null,
            ProviderDisplayName: null,
            ProviderNotes: []);
    }

    /// <summary>
    /// The <c>UseSsl</c> flag that makes the runtime negotiate <paramref name="tls"/>.
    ///
    /// <para>Asymmetric, and the asymmetry is the bug this module was written to remove. For SMTP
    /// the runtime reads <c>false</c> as STARTTLS. For IMAP it reads the identical <c>false</c> as
    /// NO ENCRYPTION — so asking for STARTTLS on an IMAP row does not get STARTTLS, it gets a
    /// cleartext session with the mailbox password in it. Rather than quietly do that, this returns
    /// the encrypted interpretation and lets the probe's own TLS-mode warning explain the mismatch
    /// against the port.</para>
    /// </summary>
    internal static bool UseSslFor(MailTransport transport, MailTlsMode tls) => transport switch
    {
        MailTransport.Smtp => tls == MailTlsMode.Implicit,
        _ => tls != MailTlsMode.None
    };

    /// <summary>
    /// Reachability and TLS for an HTTPS submission API. There is no AUTH stage to run: an API key
    /// is validated by the first real request, which is what the platform's test-send is for.
    /// </summary>
    private async Task<MailConnectionTestResult> TestHttpsEndpointAsync(
        MailConnectionTestRequest request, string host, CancellationToken cancellationToken)
    {
        var steps = new List<MailboxProbeStep>();
        var watch = Stopwatch.StartNew();

        MailConnectionTestResult Build(bool succeeded, string summary, string? negotiated = null) =>
            new(succeeded, summary, request.Direction, request.Transport, host, request.Port, request.Tls,
                FillSkipped(steps), negotiated, InboxMessageCount: null, CredentialsSentInClear: false,
                ProviderKey: null, ProviderDisplayName: null, ProviderNotes: []);

        if (!MailEndpointPolicy.IsAllowedEndpoint(host, request.Port))
        {
            steps.Add(new MailboxProbeStep(MailboxProbeStage.Policy, MailboxProbeStatus.Failed,
                $"'{host}:{request.Port}' is not an address this server may connect to.", watch.ElapsedMilliseconds,
                "Enter the provider's published API hostname (for example api.sendgrid.com). Local " +
                "and private addresses are refused on every environment."));
            return Build(false, "The server is not permitted to connect to that address.");
        }
        steps.Add(new MailboxProbeStep(MailboxProbeStage.Policy, MailboxProbeStatus.Passed,
            "The endpoint is permitted.", watch.ElapsedMilliseconds));

        Socket socket;
        watch.Restart();
        try
        {
            var addresses = await WithTimeout(ct => MailEndpointPolicy.ResolveAsync(host, ct), cancellationToken);
            steps.Add(new MailboxProbeStep(MailboxProbeStage.Dns, MailboxProbeStatus.Passed,
                $"Resolved to {string.Join(", ", addresses.Take(3))}.", watch.ElapsedMilliseconds));
        }
        catch (Exception exception)
        {
            logger?.LogWarning(exception, "API endpoint probe failed at DNS for {Host}:{Port}.", host, request.Port);
            steps.Add(new MailboxProbeStep(MailboxProbeStage.Dns, MailboxProbeStatus.Failed,
                "The hostname could not be resolved.", watch.ElapsedMilliseconds,
                "Check the hostname for typos. A regional endpoint (api.eu.mailgun.net) is a " +
                "different name from the default one."));
            return Build(false, "The provider's API hostname could not be resolved.");
        }

        watch.Restart();
        try
        {
            socket = await WithTimeout(ct => MailEndpointPolicy.ConnectAsync(host, request.Port, ct), cancellationToken);
            steps.Add(new MailboxProbeStep(MailboxProbeStage.Tcp, MailboxProbeStatus.Passed,
                $"Port {request.Port} accepted the connection.", watch.ElapsedMilliseconds));
        }
        catch (Exception exception)
        {
            logger?.LogWarning(exception, "API endpoint probe failed at TCP for {Host}:{Port}.", host, request.Port);
            steps.Add(new MailboxProbeStep(MailboxProbeStage.Tcp, MailboxProbeStatus.Failed,
                "Nothing answered on that port.", watch.ElapsedMilliseconds,
                $"Outbound HTTPS to {host}:{request.Port} appears to be blocked. This is usually an " +
                "egress firewall rule rather than anything wrong with the provider account."));
            return Build(false, $"Could not reach {host} on port {request.Port}.");
        }

        watch.Restart();
        using (socket)
        {
            await using var network = new NetworkStream(socket, ownsSocket: false);
            await using var tls = new SslStream(network, leaveInnerStreamOpen: true);
            try
            {
                await WithTimeout(ct => tls.AuthenticateAsClientAsync(
                    new SslClientAuthenticationOptions { TargetHost = host }, ct), cancellationToken);
                steps.Add(new MailboxProbeStep(MailboxProbeStage.Tls, MailboxProbeStatus.Passed,
                    $"Encrypted with {tls.SslProtocol}.", watch.ElapsedMilliseconds));
            }
            catch (Exception exception)
            {
                logger?.LogWarning(exception, "API endpoint probe failed at TLS for {Host}:{Port}.", host, request.Port);
                steps.Add(new MailboxProbeStep(MailboxProbeStage.Tls, MailboxProbeStatus.Failed,
                    exception is AuthenticationException
                        ? "The secure handshake failed."
                        : "The connection was interrupted before it was secured.",
                    watch.ElapsedMilliseconds,
                    "The provider's certificate could not be validated. Do NOT work around this by " +
                    "disabling verification. If the message mentions a revocation check, this server " +
                    "cannot reach the certificate authority — usually a proxy blocking outbound " +
                    "OCSP/CRL traffic."));
                return Build(false, "The secure connection to the provider's API could not be established.");
            }

            // Deliberately not "Passed": nothing about the key was verified, and a green tick here
            // would be read as proof that the credential works.
            steps.Add(new MailboxProbeStep(MailboxProbeStage.Authentication, MailboxProbeStatus.Skipped,
                "Not checked — an API key is only validated by a real submission.", 0,
                "Run a test send to verify the key itself."));
            steps.Add(new MailboxProbeStep(MailboxProbeStage.Mailbox, MailboxProbeStatus.Skipped,
                "Not applicable — a submission API has no mailbox to open.", 0));

            return Build(true,
                $"Reached {host} on port {request.Port} over TLS. The API key was not verified; run a test send for that.",
                tls.SslProtocol.ToString());
        }
    }

    /// <summary>
    /// Provider-specific sentences, chosen by what actually failed.
    ///
    /// <para>Attached to the outcome rather than to the provider entry wholesale: telling somebody
    /// whose connection succeeded that Microsoft disables SMTP AUTH is noise, and telling somebody
    /// whose AUTH was refused exactly that is the answer.</para>
    /// </summary>
    private static IReadOnlyList<string> Notes(
        EmailProviderDefinition? provider, MailDirection direction, MailConnectionTestResult result)
    {
        if (provider is null) return [];

        var notes = new List<string>();
        var authFailed = result.Steps.Any(x =>
            x.Stage == MailboxProbeStage.Authentication && x.Status == MailboxProbeStatus.Failed);
        var mailboxFailed = result.Steps.Any(x =>
            x.Stage == MailboxProbeStage.Mailbox && x.Status == MailboxProbeStatus.Failed);

        if (authFailed)
        {
            if (provider.RequiresAppPassword)
                notes.Add($"{provider.DisplayName} refuses the account's own password once multi-factor " +
                          "authentication is on. Generate an app-specific password and use that.");

            if (provider.SmtpAuthDisabledByDefault && direction == MailDirection.Outbound)
                notes.Add($"{provider.DisplayName} disables SMTP submission per mailbox by default. " +
                          "Correct credentials will keep being refused until an administrator enables " +
                          "authenticated SMTP for this specific account.");
        }

        if ((authFailed || mailboxFailed) && direction == MailDirection.Inbound &&
            provider.InboundEnablementNote is { Length: > 0 } enablement)
            notes.Add(enablement);

        // Volume ceilings are surfaced on SUCCESS, because that is when the operator is about to
        // start relying on the mailbox and the ceiling is the failure that arrives weeks later.
        if (result.Succeeded && direction == MailDirection.Outbound &&
            provider.SendingLimit is { Length: > 0 } limit)
            notes.Add(limit);

        return notes;
    }

    /// <summary>Marks the stages that were never reached, so an API result reads as the same fixed
    /// six-row report the mail-protocol result does.</summary>
    private static IReadOnlyList<MailboxProbeStep> FillSkipped(List<MailboxProbeStep> steps)
    {
        var reported = steps.Select(x => x.Stage).ToHashSet();
        var complete = new List<MailboxProbeStep>(steps);
        foreach (var stage in Enum.GetValues<MailboxProbeStage>())
            if (!reported.Contains(stage))
                complete.Add(new MailboxProbeStep(stage, MailboxProbeStatus.Skipped,
                    "Not checked — an earlier step failed.", 0));

        return complete.OrderBy(x => x.Stage).ToList();
    }

    private static async Task<T> WithTimeout<T>(
        Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(StageTimeout);
        return await operation(deadline.Token);
    }

    private static async Task WithTimeout(
        Func<CancellationToken, Task> operation, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(StageTimeout);
        await operation(deadline.Token);
    }
}
