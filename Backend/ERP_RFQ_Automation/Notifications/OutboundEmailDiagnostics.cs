using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Mail;
using System.Net.Sockets;
using System.Security.Authentication;

namespace ERP_RFQ_Automation.Notifications
{
    /// <summary>
    /// What went wrong with an outbound send, in the terms an operator can act on.
    ///
    /// <para><b>Why an enum rather than the exception text.</b> The provider libraries answer a
    /// misconfigured mail server with sentences like "The SMTP server requires a secure connection
    /// or the client was not authenticated. The server response was: 5.7.0 Must issue a STARTTLS
    /// command first" — accurate, and useless to the person who has to decide whether they typed
    /// the password wrong, chose the wrong port, or never verified the sending domain. Each value
    /// here maps to exactly one next action, and the console can render that action.</para>
    /// </summary>
    public enum OutboundEmailFailureKind
    {
        None = 0,

        /// <summary>The selected provider has no usable credentials at all (SMTP with no host,
        /// SendGrid with no API key). Reached before a single packet leaves the process.</summary>
        NotConfigured = 1,

        /// <summary>The server accepted the connection and refused the identity. Wrong username,
        /// wrong password, or an API key that has been rotated or revoked.</summary>
        AuthenticationFailed = 2,

        /// <summary>The host name did not resolve, or nothing answered on that port. Almost always
        /// a typo in the host, the wrong port, or an egress firewall.</summary>
        HostUnreachable = 3,

        /// <summary>The TLS handshake failed, or the server demanded STARTTLS and did not get it.
        /// The usual cause is an EnableSsl/port mismatch (465 implicit vs 587 STARTTLS).</summary>
        TlsFailure = 4,

        /// <summary>Authenticated, and still refused permission to send to that recipient — the
        /// classic "relay access denied". The account may not be allowed to send from this From
        /// address, or the domain is not verified with the provider.</summary>
        RelayDenied = 5,

        /// <summary>The server took the message and rejected this particular recipient: no such
        /// mailbox, or a mailbox that refuses us.</summary>
        RecipientRejected = 6,

        /// <summary>Nothing answered inside the configured timeout.</summary>
        Timeout = 7,

        /// <summary>The provider is throttling. Not a configuration fault; retrying later works.</summary>
        RateLimited = 8,

        /// <summary>The provider understood the request and rejected the message — an unverified
        /// sender identity is by far the most common reason.</summary>
        ProviderRejected = 9,

        /// <summary>Nothing was transmitted because the outbound guard refused it. Not a provider
        /// fault at all: containment did its job and the operator has to widen it deliberately.</summary>
        BlockedByOutboundGuard = 10,

        /// <summary>Classification failed. The full exception is in the server log; the operator
        /// gets a truthful "we do not know" rather than a confident wrong answer.</summary>
        Unknown = 99
    }

    /// <summary>
    /// A transport failure that already knows what it was. Providers raise it so the classifier
    /// does not have to re-derive from a string what the provider knew exactly.
    ///
    /// <para>Derives from <see cref="InvalidOperationException"/> because that is what the SendGrid
    /// provider threw before this type existed, and every caller upstream (the outbox worker, the
    /// resilient wrapping in <see cref="INotificationService"/>) catches broadly. Narrowing the
    /// base type would have been a silent behaviour change in code this class does not own.</para>
    /// </summary>
    public sealed class OutboundEmailTransportException : InvalidOperationException
    {
        public OutboundEmailTransportException(
            OutboundEmailFailureKind kind, string message, string? providerStatus = null, Exception? inner = null)
            : base(message, inner)
        {
            Kind = kind;
            ProviderStatus = providerStatus;
        }

        public OutboundEmailFailureKind Kind { get; }

        /// <summary>The provider's own status, e.g. "401" or "SMTP 550". Safe to show: it is a
        /// protocol code, never a credential.</summary>
        public string? ProviderStatus { get; }
    }

    /// <summary>Classified outcome of one send attempt. <see cref="Message"/> is written for the
    /// operator and is deliberately OURS — no provider text is passed through, because provider
    /// text is unbounded, occasionally echoes the submitted envelope, and is not something a
    /// console can be trusted to render safely.</summary>
    public sealed record OutboundEmailFailure(
        OutboundEmailFailureKind Kind,
        string Message,
        string? ProviderStatus);

    /// <summary>
    /// Turns whatever a transport threw into one <see cref="OutboundEmailFailureKind"/>.
    ///
    /// <para><b>Why the message is inspected at all.</b> <c>System.Net.Mail.SmtpException</c>
    /// carries a <see cref="SmtpStatusCode"/>, but the framework only maps the codes present in
    /// that enum — 535 (bad credentials), the single most common real failure, collapses to
    /// <see cref="SmtpStatusCode.GeneralFailure"/>. The response text is then the only remaining
    /// signal, so it is matched for the enhanced status codes (RFC 3463) rather than for English
    /// prose, which differs per server.</para>
    /// </summary>
    public static class OutboundEmailFailureClassifier
    {
        public static OutboundEmailFailure Classify(Exception exception)
        {
            ArgumentNullException.ThrowIfNull(exception);

            switch (exception)
            {
                case OutboundEmailTransportException typed:
                    return new OutboundEmailFailure(typed.Kind, Describe(typed.Kind), typed.ProviderStatus);

                case OutboundEmailBlockedException blocked:
                    return new OutboundEmailFailure(
                        OutboundEmailFailureKind.BlockedByOutboundGuard,
                        // The guard's own message names the address and the setting to change, and
                        // it is ours, so it is the one provider-adjacent string worth surfacing.
                        blocked.Message,
                        null);

                case SmtpFailedRecipientsException recipients:
                    return new OutboundEmailFailure(
                        OutboundEmailFailureKind.RecipientRejected,
                        Describe(OutboundEmailFailureKind.RecipientRejected),
                        StatusOf(recipients.InnerExceptions is { Length: > 0 }
                            ? recipients.InnerExceptions[0].StatusCode
                            : recipients.StatusCode));

                case SmtpFailedRecipientException recipient:
                    return new OutboundEmailFailure(
                        OutboundEmailFailureKind.RecipientRejected,
                        Describe(OutboundEmailFailureKind.RecipientRejected),
                        StatusOf(recipient.StatusCode));

                case SmtpException smtp:
                    return ClassifySmtp(smtp);

                case SocketException socket:
                    return new OutboundEmailFailure(
                        socket.SocketErrorCode == SocketError.TimedOut
                            ? OutboundEmailFailureKind.Timeout
                            : OutboundEmailFailureKind.HostUnreachable,
                        socket.SocketErrorCode == SocketError.TimedOut
                            ? Describe(OutboundEmailFailureKind.Timeout)
                            : Describe(OutboundEmailFailureKind.HostUnreachable),
                        socket.SocketErrorCode.ToString());

                case AuthenticationException:
                    return new OutboundEmailFailure(
                        OutboundEmailFailureKind.TlsFailure, Describe(OutboundEmailFailureKind.TlsFailure), null);

                case OperationCanceledException:
                case TimeoutException:
                    return new OutboundEmailFailure(
                        OutboundEmailFailureKind.Timeout, Describe(OutboundEmailFailureKind.Timeout), null);

                case HttpRequestException http:
                    return new OutboundEmailFailure(
                        OutboundEmailFailureKind.HostUnreachable,
                        Describe(OutboundEmailFailureKind.HostUnreachable),
                        http.StatusCode is { } status ? ((int)status).ToString() : null);

                case IOException io when io.InnerException is AuthenticationException:
                    return new OutboundEmailFailure(
                        OutboundEmailFailureKind.TlsFailure, Describe(OutboundEmailFailureKind.TlsFailure), null);

                case InvalidOperationException invalid when LooksUnconfigured(invalid.Message):
                    return new OutboundEmailFailure(
                        OutboundEmailFailureKind.NotConfigured,
                        Describe(OutboundEmailFailureKind.NotConfigured),
                        null);
            }

            // An exception can nest the informative one (SmtpException wrapping SocketException is
            // the routine case), so one unwrap is worth more than a longer switch.
            if (exception.InnerException is { } inner)
            {
                var nested = Classify(inner);
                if (nested.Kind != OutboundEmailFailureKind.Unknown) return nested;
            }

            return new OutboundEmailFailure(
                OutboundEmailFailureKind.Unknown, Describe(OutboundEmailFailureKind.Unknown), null);
        }

        /// <summary>Maps an HTTP response from a REST mail API. Separate from
        /// <see cref="Classify"/> because a status code is a fact the provider states directly and
        /// should never be re-derived from an exception message.</summary>
        public static OutboundEmailFailure ClassifyHttpStatus(HttpStatusCode status)
        {
            var kind = status switch
            {
                HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => OutboundEmailFailureKind.AuthenticationFailed,
                HttpStatusCode.TooManyRequests => OutboundEmailFailureKind.RateLimited,
                HttpStatusCode.RequestTimeout or HttpStatusCode.GatewayTimeout => OutboundEmailFailureKind.Timeout,
                HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable => OutboundEmailFailureKind.HostUnreachable,
                _ => OutboundEmailFailureKind.ProviderRejected
            };
            return new OutboundEmailFailure(kind, Describe(kind), ((int)status).ToString());
        }

        private static OutboundEmailFailure ClassifySmtp(SmtpException smtp)
        {
            var text = smtp.Message ?? string.Empty;
            var status = StatusOf(smtp.StatusCode);

            // A recognised protocol code outranks any reading of the prose. 530 in particular
            // arrives with the framework's own sentence "requires a secure connection or the client
            // was not authenticated", which mentions authentication and is NOT an authentication
            // failure — the server is refusing to talk before STARTTLS.
            if (smtp.StatusCode == SmtpStatusCode.MustIssueStartTlsFirst)
                return new OutboundEmailFailure(OutboundEmailFailureKind.TlsFailure, Describe(OutboundEmailFailureKind.TlsFailure), status);

            // Then the enhanced status codes (RFC 3463) inside the response text: machine-defined
            // and server-independent, unlike the human sentence beside them. Relay before auth,
            // because a 550 relay refusal is a permission problem with the From address rather than
            // a bad password, and sending an operator to re-check credentials wastes the afternoon.
            if (text.Contains("5.7.1", StringComparison.Ordinal) ||
                text.Contains("relay", StringComparison.OrdinalIgnoreCase))
                return new OutboundEmailFailure(OutboundEmailFailureKind.RelayDenied, Describe(OutboundEmailFailureKind.RelayDenied), status);

            if (text.Contains("5.7.0", StringComparison.Ordinal) ||
                text.Contains("535", StringComparison.Ordinal) ||
                text.Contains("authenticat", StringComparison.OrdinalIgnoreCase))
                return new OutboundEmailFailure(OutboundEmailFailureKind.AuthenticationFailed, Describe(OutboundEmailFailureKind.AuthenticationFailed), status);

            if (text.Contains("STARTTLS", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("TLS", StringComparison.Ordinal))
                return new OutboundEmailFailure(OutboundEmailFailureKind.TlsFailure, Describe(OutboundEmailFailureKind.TlsFailure), status);

            var kind = smtp.StatusCode switch
            {
                SmtpStatusCode.ClientNotPermitted => OutboundEmailFailureKind.AuthenticationFailed,
                SmtpStatusCode.MailboxUnavailable or SmtpStatusCode.MailboxNameNotAllowed
                    => OutboundEmailFailureKind.RecipientRejected,
                SmtpStatusCode.MailboxBusy or SmtpStatusCode.InsufficientStorage
                    or SmtpStatusCode.ExceededStorageAllocation => OutboundEmailFailureKind.RateLimited,
                SmtpStatusCode.ServiceNotAvailable => OutboundEmailFailureKind.HostUnreachable,
                SmtpStatusCode.TransactionFailed => OutboundEmailFailureKind.ProviderRejected,
                _ => OutboundEmailFailureKind.Unknown
            };

            if (kind == OutboundEmailFailureKind.Unknown && smtp.InnerException is SocketException socket)
                return Classify(socket);

            return new OutboundEmailFailure(kind, Describe(kind), status);
        }

        private static bool LooksUnconfigured(string? message) =>
            message is not null && message.Contains("is not configured", StringComparison.OrdinalIgnoreCase);

        private static string? StatusOf(SmtpStatusCode code) =>
            code == SmtpStatusCode.GeneralFailure ? null : $"SMTP {(int)code}";

        /// <summary>The sentence the operator reads. Every one names the setting to change, because
        /// "authentication failed" without "check Username/Password" is a status, not help.</summary>
        public static string Describe(OutboundEmailFailureKind kind) => kind switch
        {
            OutboundEmailFailureKind.None =>
                "The message was accepted by the provider.",
            OutboundEmailFailureKind.NotConfigured =>
                "The selected provider has no credentials. Set the SMTP host (or the SendGrid API key) and save before testing again.",
            OutboundEmailFailureKind.AuthenticationFailed =>
                "The mail server rejected the credentials. Check the username and password (for SendGrid SMTP the username is literally 'apikey'), or issue a new API key.",
            OutboundEmailFailureKind.HostUnreachable =>
                "Nothing answered at that host and port. Check the host name and port, and that outbound SMTP is not blocked by a firewall.",
            OutboundEmailFailureKind.TlsFailure =>
                "The encrypted connection could not be established. Port 587 expects STARTTLS (TLS enabled); port 465 expects implicit TLS. Check that the port and the TLS setting agree.",
            OutboundEmailFailureKind.RelayDenied =>
                "The server authenticated us and then refused to relay the message. The From address is usually not one this account is permitted to send from — verify the sending domain with the provider.",
            OutboundEmailFailureKind.RecipientRejected =>
                "The server rejected the recipient address. Check the address you entered.",
            OutboundEmailFailureKind.Timeout =>
                "The server did not respond within the configured timeout. Check the host and port, or raise the timeout.",
            OutboundEmailFailureKind.RateLimited =>
                "The provider is currently throttling this account. The settings look correct; try again shortly.",
            OutboundEmailFailureKind.ProviderRejected =>
                "The provider rejected the message. The most common cause is a From address whose domain has not been verified with the provider.",
            OutboundEmailFailureKind.BlockedByOutboundGuard =>
                "The outbound guard refused the message before it reached the provider. Change the guard mode or add the recipient to the allow-list.",
            _ =>
                "The send failed for a reason we could not classify. The full error is in the application log."
        };
    }
}
