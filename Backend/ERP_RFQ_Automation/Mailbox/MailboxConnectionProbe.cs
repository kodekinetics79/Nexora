using System.Diagnostics;
using System.Net.Sockets;
using ERP_RFQ_Automation.Security;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Net.Smtp;
using MailKit.Security;

namespace ERP_RFQ_Automation.Mailbox;

/// <summary>Ordered stages of a mailbox connection attempt. The order is the dependency order:
/// a later stage cannot be evaluated if an earlier one failed.
///
/// <para>Serialised BY NAME. This API registers no global string-enum converter, so without the
/// attribute these leave as 0..5 and every client has to hardcode the ordinal — which silently
/// breaks the moment a stage is inserted.</para></summary>
[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter))]
public enum MailboxProbeStage
{
    Policy,
    Dns,
    Tcp,
    Tls,
    Authentication,
    Mailbox
}

[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter))]
public enum MailboxProbeStatus
{
    Passed,
    Failed,
    /// <summary>Not evaluated, because an earlier stage failed. Distinct from Failed: reporting
    /// six failures when only the first is real sends the operator chasing four phantoms.</summary>
    Skipped,
    /// <summary>Reached the endpoint, but the configuration is unsafe or will not behave the way
    /// the operator expects. The connection works; the setting is still wrong.</summary>
    Warning
}

/// <param name="Remedy">What the operator should actually DO. Null when there is nothing to fix.
/// This is the field that turns a support ticket into a self-service correction.</param>
public sealed record MailboxProbeStep(
    MailboxProbeStage Stage,
    MailboxProbeStatus Status,
    string Detail,
    long ElapsedMs,
    string? Remedy = null);

public sealed record MailboxProbeResult(
    bool Succeeded,
    string Protocol,
    string Host,
    int Port,
    string Summary,
    IReadOnlyList<MailboxProbeStep> Steps,
    string? NegotiatedSecurity = null,
    int? InboxMessageCount = null,
    bool CredentialsSentInClear = false);

/// <summary>What to probe. Deliberately a plain input rather than an <c>EmailConfiguration</c>, so
/// an unsaved form can be tested before anything is written.</summary>
public sealed record MailboxProbeRequest(
    string Protocol,
    string Host,
    int Port,
    string Username,
    string Password,
    bool UseSsl,
    /// <summary>
    /// The exact TLS mode to probe with, when the caller knows it rather than inferring it from
    /// <paramref name="UseSsl" />. Null keeps the historic behaviour: <see cref="UseSsl"/> read
    /// through <c>SecurityFor</c>.
    ///
    /// <para>It exists because <c>UseSsl == false</c> does NOT mean one thing. The tenant outbound
    /// transport reads it as STARTTLS; the platform's own <c>SmtpEmailSender</c> reads it as no
    /// encryption at all. A probe that picks either interpretation lies to one of them — and it was
    /// lying to the platform screen, testing over STARTTLS a channel that would then send in the
    /// clear, and passing. A caller that knows which sender will run states the mode instead of
    /// re-deriving it.</para>
    /// </summary>
    SecureSocketOptions? Security = null);

public interface IMailboxConnectionProbe
{
    Task<MailboxProbeResult> ProbeAsync(MailboxProbeRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// Answers "why won't my mailbox connect?" with the stage that actually failed.
///
/// <para><b>Why staged.</b> A single pass/fail on a mail connection is close to useless: DNS
/// typos, blocked ports, TLS-mode mismatches, MFA-rejected passwords and IMAP-disabled mailboxes
/// all present as "could not connect", and separating them otherwise means reading server logs.
/// Each stage is timed and carries an operator-readable remedy, so the person configuring the
/// mailbox can fix it without escalating.</para>
///
/// <para><b>It replicates the RUNTIME's security semantics, not a tidied-up version.</b> This is
/// the point of the whole class. <c>UseSsl</c> is interpreted THREE different ways in this
/// codebase — IMAP polling reads it as <c>SslOnConnect : None</c>, the outbound SMTP transport as
/// <c>SslOnConnect : StartTls</c>, and <c>EmailService</c> as <c>Auto : StartTls</c>. A probe that
/// picked its own sensible interpretation could pass while the poller fails, which is worse than
/// no probe at all. <see cref="SecurityFor"/> mirrors the call sites exactly and is the one place
/// to update if they are ever unified.</para>
///
/// <para><b>It never sends mail.</b> The SMTP probe authenticates and disconnects. Nothing is
/// transmitted to any recipient, which is what makes it safe to run against a production mailbox
/// while real customer correspondence is in it.</para>
///
/// <para><b>It cannot be used to scan the internal network.</b> Every connection goes through
/// <see cref="MailEndpointPolicy"/>, which resolves the name, rejects any non-public address, and
/// connects to the validated address rather than re-resolving the hostname. Without that, this
/// endpoint would be a tenant-operable SSRF probe with timing and error detail helpfully reported
/// back in the response body.</para>
/// </summary>
public sealed class MailboxConnectionProbe(ILogger<MailboxConnectionProbe>? logger = null) : IMailboxConnectionProbe
{
    /// <summary>
    /// The operator sees a clean, remedy-bearing sentence; support needs the actual exception.
    /// Without this the server keeps no record of WHY a probe failed, so a report of "the TLS step
    /// fails" cannot be diagnosed after the fact.
    /// </summary>
    private void LogStageFailure(MailboxProbeStage stage, string host, int port, Exception exception) =>
        logger?.LogWarning(exception,
            "Mailbox probe failed at {Stage} for {Host}:{Port}.", stage, host, port);

    public const string Imap = "IMAP";
    public const string Smtp = "SMTP";

    /// <summary>Per-stage ceiling. The caller also imposes an overall deadline; this stops one
    /// black-holed stage from consuming the entire budget and reporting nothing.</summary>
    private static readonly TimeSpan StageTimeout = TimeSpan.FromSeconds(12);

    /// <summary>
    /// The runtime's interpretation of <c>UseSsl</c>, per protocol. Mirrors
    /// <c>EmailService.FetchEmails</c> (IMAP) and <c>MailKitOutboundSmtpTransport.SendAsync</c>
    /// (SMTP). Changing either call site without changing this makes the probe lie.
    /// </summary>
    internal static SecureSocketOptions SecurityFor(string protocol, bool useSsl) =>
        IsImap(protocol)
            ? useSsl ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.None
            : useSsl ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTls;

    internal static bool IsImap(string protocol) =>
        string.Equals(protocol?.Trim(), Imap, StringComparison.OrdinalIgnoreCase);

    public async Task<MailboxProbeResult> ProbeAsync(
        MailboxProbeRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var steps = new List<MailboxProbeStep>();
        var protocol = IsImap(request.Protocol) ? Imap : Smtp;
        var host = string.IsNullOrWhiteSpace(request.Host) ? string.Empty : MailEndpointPolicy.Normalize(request.Host);
        var security = request.Security ?? SecurityFor(protocol, request.UseSsl);
        var clear = security == SecureSocketOptions.None;

        MailboxProbeResult Abort(string summary) => new(
            false, protocol, host, request.Port, summary, Fill(steps), CredentialsSentInClear: clear);

        // ---- Stage 1: policy ------------------------------------------------------------
        var watch = Stopwatch.StartNew();
        if (!MailEndpointPolicy.IsAllowedEndpoint(host, request.Port))
        {
            steps.Add(new MailboxProbeStep(MailboxProbeStage.Policy, MailboxProbeStatus.Failed,
                $"'{host}:{request.Port}' is not an address this server may connect to.", watch.ElapsedMilliseconds,
                "Enter the mail provider's public hostname (for example outlook.office365.com). " +
                "Local, private and link-local addresses are refused on every environment because a " +
                "mail server is not permitted to dial into internal infrastructure."));
            return Abort("The server is not permitted to connect to that address.");
        }
        steps.Add(new MailboxProbeStep(MailboxProbeStage.Policy, MailboxProbeStatus.Passed,
            "The endpoint is permitted.", watch.ElapsedMilliseconds));

        // A working connection with a misleading setting still deserves to be flagged.
        var mismatch = EncryptionAdvice(protocol, request.Port, request.UseSsl);

        // ---- Stage 2/3: DNS, then TCP ---------------------------------------------------
        Socket socket;
        watch.Restart();
        try
        {
            var addresses = await WithTimeout(
                ct => MailEndpointPolicy.ResolveAsync(host, ct), cancellationToken);
            steps.Add(new MailboxProbeStep(MailboxProbeStage.Dns, MailboxProbeStatus.Passed,
                $"Resolved to {string.Join(", ", addresses.Take(3))}.", watch.ElapsedMilliseconds));
        }
        catch (Exception exception)
        {
            LogStageFailure(MailboxProbeStage.Dns, host, request.Port, exception);
            steps.Add(new MailboxProbeStep(MailboxProbeStage.Dns, MailboxProbeStatus.Failed,
                Describe(exception), watch.ElapsedMilliseconds, DnsRemedy(exception)));
            return Abort("The mail server's hostname could not be resolved.");
        }

        watch.Restart();
        try
        {
            socket = await WithTimeout(
                ct => MailEndpointPolicy.ConnectAsync(host, request.Port, ct), cancellationToken);
            steps.Add(new MailboxProbeStep(MailboxProbeStage.Tcp, MailboxProbeStatus.Passed,
                $"Port {request.Port} accepted the connection.", watch.ElapsedMilliseconds));
        }
        catch (Exception exception)
        {
            LogStageFailure(MailboxProbeStage.Tcp, host, request.Port, exception);
            steps.Add(new MailboxProbeStep(MailboxProbeStage.Tcp, MailboxProbeStatus.Failed,
                Describe(exception), watch.ElapsedMilliseconds,
                $"Nothing answered on port {request.Port}. Confirm the port is correct for {protocol} " +
                "and that outbound traffic to it is not blocked by a firewall."));
            return Abort($"Could not reach {host} on port {request.Port}.");
        }

        // ---- Stages 4-6: protocol conversation ------------------------------------------
        using (socket)
        {
            return IsImap(protocol)
                ? await ProbeImapAsync(request, host, security, socket, steps, mismatch, clear, cancellationToken)
                : await ProbeSmtpAsync(request, host, security, socket, steps, mismatch, clear, cancellationToken);
        }
    }

    private async Task<MailboxProbeResult> ProbeImapAsync(
        MailboxProbeRequest request, string host, SecureSocketOptions security, Socket socket,
        List<MailboxProbeStep> steps, MailboxProbeStep? mismatch, bool clear, CancellationToken cancellationToken)
    {
        using var client = new ImapClient();
        var watch = Stopwatch.StartNew();

        try
        {
            await WithTimeout(ct => client.ConnectAsync(socket, host, request.Port, security, ct), cancellationToken);
            steps.Add(TlsStep(client.SslProtocol.ToString(), security, watch.ElapsedMilliseconds, mismatch));
        }
        catch (Exception exception)
        {
            LogStageFailure(MailboxProbeStage.Tls, host, request.Port, exception);
            steps.Add(new MailboxProbeStep(MailboxProbeStage.Tls, MailboxProbeStatus.Failed,
                Describe(exception), watch.ElapsedMilliseconds, TlsRemedy(request.Port, request.UseSsl, exception)));
            return new MailboxProbeResult(false, Imap, host, request.Port,
                "The secure connection could not be established.", Fill(steps), CredentialsSentInClear: clear);
        }

        watch.Restart();
        try
        {
            await WithTimeout(ct => client.AuthenticateAsync(request.Username, request.Password, ct), cancellationToken);
            steps.Add(new MailboxProbeStep(MailboxProbeStage.Authentication, MailboxProbeStatus.Passed,
                $"Signed in as {request.Username}.", watch.ElapsedMilliseconds));
        }
        catch (Exception exception)
        {
            LogStageFailure(MailboxProbeStage.Authentication, host, request.Port, exception);
            steps.Add(new MailboxProbeStep(MailboxProbeStage.Authentication, MailboxProbeStatus.Failed,
                Describe(exception), watch.ElapsedMilliseconds, AuthenticationRemedy(host, Imap)));
            return new MailboxProbeResult(false, Imap, host, request.Port,
                "The mailbox rejected the username or password.", Fill(steps), CredentialsSentInClear: clear);
        }

        watch.Restart();
        try
        {
            // Not merely a null-check to satisfy the compiler: a server that exposes no personal
            // namespace really does hand back a null Inbox, and the poller would then fail on
            // every cycle with a NullReferenceException that says nothing about the cause.
            var inbox = client.Inbox
                ?? throw new InvalidOperationException(
                    "The server did not expose an INBOX for this account.");

            // Read-only: the probe must not touch \Seen flags on a mailbox holding live customer
            // mail. The poller opens ReadWrite; opening ReadOnly here still proves selectability.
            await WithTimeout(ct => inbox.OpenAsync(FolderAccess.ReadOnly, ct), cancellationToken);
            var count = inbox.Count;
            steps.Add(new MailboxProbeStep(MailboxProbeStage.Mailbox, MailboxProbeStatus.Passed,
                $"INBOX opened. {count:N0} message(s) present.", watch.ElapsedMilliseconds));
            await SafeDisconnectAsync(client, cancellationToken);

            return new MailboxProbeResult(true, Imap, host, request.Port,
                $"Connected to {host} and read INBOX as {request.Username}.",
                Fill(steps), client.SslProtocol.ToString(), count, clear);
        }
        catch (Exception exception)
        {
            LogStageFailure(MailboxProbeStage.Mailbox, host, request.Port, exception);
            steps.Add(new MailboxProbeStep(MailboxProbeStage.Mailbox, MailboxProbeStatus.Failed,
                Describe(exception), watch.ElapsedMilliseconds,
                "Signed in, but INBOX could not be opened. The account may be a shared mailbox " +
                "without delegated access, or IMAP may be disabled for it."));
            await SafeDisconnectAsync(client, cancellationToken);
            return new MailboxProbeResult(false, Imap, host, request.Port,
                "Signed in, but the INBOX could not be opened.", Fill(steps), CredentialsSentInClear: clear);
        }
    }

    private async Task<MailboxProbeResult> ProbeSmtpAsync(
        MailboxProbeRequest request, string host, SecureSocketOptions security, Socket socket,
        List<MailboxProbeStep> steps, MailboxProbeStep? mismatch, bool clear, CancellationToken cancellationToken)
    {
        using var client = new SmtpClient();
        var watch = Stopwatch.StartNew();

        try
        {
            await WithTimeout(ct => client.ConnectAsync(socket, host, request.Port, security, ct), cancellationToken);
            steps.Add(TlsStep(client.SslProtocol.ToString(), security, watch.ElapsedMilliseconds, mismatch));
        }
        catch (Exception exception)
        {
            LogStageFailure(MailboxProbeStage.Tls, host, request.Port, exception);
            steps.Add(new MailboxProbeStep(MailboxProbeStage.Tls, MailboxProbeStatus.Failed,
                Describe(exception), watch.ElapsedMilliseconds, TlsRemedy(request.Port, request.UseSsl, exception)));
            return new MailboxProbeResult(false, Smtp, host, request.Port,
                "The secure connection could not be established.", Fill(steps), CredentialsSentInClear: clear);
        }

        watch.Restart();
        try
        {
            await WithTimeout(ct => client.AuthenticateAsync(request.Username, request.Password, ct), cancellationToken);
            steps.Add(new MailboxProbeStep(MailboxProbeStage.Authentication, MailboxProbeStatus.Passed,
                $"Signed in as {request.Username}.", watch.ElapsedMilliseconds));
        }
        catch (Exception exception)
        {
            LogStageFailure(MailboxProbeStage.Authentication, host, request.Port, exception);
            steps.Add(new MailboxProbeStep(MailboxProbeStage.Authentication, MailboxProbeStatus.Failed,
                Describe(exception), watch.ElapsedMilliseconds, AuthenticationRemedy(host, Smtp)));
            return new MailboxProbeResult(false, Smtp, host, request.Port,
                "The mail server rejected the username or password.", Fill(steps), CredentialsSentInClear: clear);
        }

        // NO MESSAGE IS SENT. Reaching an authenticated session is the whole proof; transmitting a
        // test mail from a tenant's production mailbox is not something a settings screen may do.
        steps.Add(new MailboxProbeStep(MailboxProbeStage.Mailbox, MailboxProbeStatus.Passed,
            $"Ready to send (max message size {(client.MaxSize > 0 ? $"{client.MaxSize / 1024 / 1024} MB" : "unpublished")}). " +
            "No test email was sent.", 0));
        await SafeDisconnectAsync(client, cancellationToken);

        return new MailboxProbeResult(true, Smtp, host, request.Port,
            $"Authenticated to {host} as {request.Username}. No mail was sent.",
            Fill(steps), client.SslProtocol.ToString(), CredentialsSentInClear: clear);
    }

    // ---- reporting helpers --------------------------------------------------------------

    private static MailboxProbeStep TlsStep(
        string negotiated, SecureSocketOptions security, long elapsed, MailboxProbeStep? mismatch)
    {
        if (security == SecureSocketOptions.None)
            return new MailboxProbeStep(MailboxProbeStage.Tls, MailboxProbeStatus.Warning,
                "Connected WITHOUT encryption. The mailbox password crosses the network in clear text.",
                elapsed,
                "Turn on 'Use a secure connection'. Any network between this server and the mail " +
                "provider can read the password as configured.");

        return mismatch is not null
            ? mismatch with { ElapsedMs = elapsed }
            : new MailboxProbeStep(MailboxProbeStage.Tls, MailboxProbeStatus.Passed,
                $"Encrypted with {negotiated}.", elapsed);
    }

    /// <summary>
    /// Port and encryption setting must agree. They can disagree and still connect — MailKit will
    /// often negotiate something workable — but the operator has then configured something other
    /// than what they believe, which resurfaces later as an intermittent failure.
    /// </summary>
    internal static MailboxProbeStep? EncryptionAdvice(string protocol, int port, bool useSsl)
    {
        var implicitTlsPort = port is 993 or 465;
        var startTlsPort = port is 143 or 587 or 25;

        if (implicitTlsPort && !useSsl)
            return new MailboxProbeStep(MailboxProbeStage.Tls, MailboxProbeStatus.Warning,
                $"Port {port} expects an encrypted connection, but 'use a secure connection' is off.", 0,
                IsImap(protocol) && port == 993
                    ? "Turn on 'Use a secure connection' for port 993, or switch to port 143."
                    : $"Turn on 'Use a secure connection' for port {port}.");

        if (startTlsPort && useSsl)
            return new MailboxProbeStep(MailboxProbeStage.Tls, MailboxProbeStatus.Warning,
                $"Port {port} normally upgrades to encryption after connecting, but this mailbox is " +
                "set to connect encrypted from the first byte.", 0,
                IsImap(protocol)
                    ? $"Use port 993 with a secure connection, or port {port} without it."
                    : $"Use port 465 with a secure connection, or port {port} without it.");

        return null;
    }

    private static string AuthenticationRemedy(string host, string protocol)
    {
        var provider = host.Contains("office365", StringComparison.OrdinalIgnoreCase) ||
                       host.Contains("outlook", StringComparison.OrdinalIgnoreCase)
            ? "Microsoft 365 rejects basic authentication for accounts with MFA enabled, and disables " +
              $"{protocol} per-mailbox by default. Create an app password, or ask your administrator to " +
              $"enable {protocol} for this mailbox in the Exchange admin centre."
            : host.Contains("gmail", StringComparison.OrdinalIgnoreCase) ||
              host.Contains("google", StringComparison.OrdinalIgnoreCase)
                ? "Google requires an App Password when 2-Step Verification is on — your normal account " +
                  "password will always be rejected here."
                : "Check the username and password. Many providers require the full email address as " +
                  "the username, and a dedicated app password rather than the account password.";

        return provider + " The connection itself succeeded, so the host and port are correct.";
    }

    private static string TlsRemedy(int port, bool useSsl, Exception exception)
    {
        // A certificate failure and a wrong-encryption-mode failure both surface as
        // SslHandshakeException, but their fixes are opposites. Handing the mode-mismatch advice
        // to someone whose certificate could not be validated tells them to TURN OFF ENCRYPTION to
        // fix a trust problem — the single worst instruction this screen could give. The most
        // common form in practice is a revocation (OCSP/CRL) check that cannot complete because
        // egress to the CA is blocked, which has nothing to do with the port or the TLS mode.
        var message = exception.ToString();
        if (message.Contains("certificate", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("revocation", StringComparison.OrdinalIgnoreCase))
            return "The connection was encrypted, but the mail server's certificate could not be " +
                   "validated. Do NOT turn off the secure connection to work around this. If the " +
                   "message mentions a revocation check, this server cannot reach the certificate " +
                   "authority — usually a firewall or proxy blocking outbound OCSP/CRL traffic. " +
                   "Otherwise the certificate may be expired, self-signed, or issued for a " +
                   "different hostname than the one entered.";

        return TlsModeRemedy(port, useSsl);
    }

    private static string TlsModeRemedy(int port, bool useSsl) => useSsl
        ? $"The server did not offer an encrypted connection on port {port} from the first byte. " +
          "Try turning off 'use a secure connection' (the connection will still upgrade to TLS where " +
          "the server supports it), or use port 993 for IMAP / 465 for SMTP."
        : $"The server on port {port} would not upgrade the connection to encryption. " +
          "Try turning on 'use a secure connection', or use port 993 for IMAP / 465 for SMTP.";

    private static string DnsRemedy(Exception exception) =>
        exception is InvalidOperationException
            ? "The hostname resolves to a private or local address, which this server will not dial. " +
              "Use the mail provider's public hostname."
            : "The hostname could not be found. Check it for typos — a mail host usually looks like " +
              "outlook.office365.com, imap.gmail.com, or mail.yourcompany.com.";

    /// <summary>Operator-facing, and deliberately not <c>exception.ToString()</c>: stack traces and
    /// internal type names in an admin screen are noise at best and disclosure at worst.</summary>
    private static string Describe(Exception exception) => exception switch
    {
        OperationCanceledException or TimeoutException => "Timed out waiting for the mail server.",
        // ServiceNotAuthenticatedException derives from AuthenticationException, so it has to be
        // matched first or it can never be reached.
        ServiceNotAuthenticatedException => "The mail server requires authentication that was not accepted.",
        AuthenticationException => "The mail server rejected these credentials.",
        SslHandshakeException => "The secure handshake failed.",
        SocketException socket => $"Network error: {socket.SocketErrorCode}.",
        InvalidOperationException invalid => invalid.Message,
        ImapProtocolException or SmtpProtocolException => "The server sent an unexpected protocol response.",
        _ => "The mail server could not be contacted."
    };

    /// <summary>Marks every stage after a failure as Skipped, so the report shows six rows in a
    /// fixed order rather than a ragged list the operator has to interpret.</summary>
    private static IReadOnlyList<MailboxProbeStep> Fill(List<MailboxProbeStep> steps)
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

    /// <summary>A failed QUIT must never turn a successful probe into a reported failure.</summary>
    private static async Task SafeDisconnectAsync(IMailService client, CancellationToken cancellationToken)
    {
        try { await client.DisconnectAsync(true, cancellationToken); }
        catch { /* the result is already determined */ }
    }
}
