using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ERP_RFQ_Automation.Security;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;
using MimeKit.Text;
using MimeKit.Utils;

namespace ERP_RFQ_Automation.Notifications.Providers
{
    /// <summary>
    /// SMTP sender built on MailKit, sharing the connection policy the tenant mailbox path uses.
    ///
    /// <para><b>Why not <c>System.Net.Mail.SmtpClient</c>, which this used to be.</b> That client
    /// treats <c>EnableSsl</c> as "negotiate STARTTLS on a connection that starts in the clear" and
    /// has no way to express implicit TLS — the mode where the socket is wrapped in TLS before a
    /// single byte of SMTP is spoken. Microsoft documents it as not supporting it and marks the
    /// type as not recommended for new development. Port 465 is implicit TLS, and 465 is what a
    /// great many hosts publish as their recommended setting — GoDaddy's own instructions say
    /// "Port 465, SSL/TLS". Configured exactly as the provider told the operator to, the old client
    /// opened a plaintext socket, waited for a greeting that a TLS listener will never send, and
    /// hung until the timeout. The operator sees "email not sent" and has no reason to suspect the
    /// settings they copied verbatim.</para>
    ///
    /// <para><b>The TLS mode is derived from the port, not from a second setting.</b> 465 means
    /// implicit TLS and everything else means STARTTLS — the same inference every mail client
    /// makes, and the reason no user of a mail client has ever been asked which one to pick. A
    /// separate toggle would only create a state where the port and the mode disagree, which is
    /// precisely the failure this replaces.</para>
    ///
    /// <para><b>Egress goes through <see cref="MailEndpointPolicy"/></b>, the same guard the
    /// tenant-mailbox transport uses. An operator-supplied hostname is attacker-influenced input in
    /// the SSRF sense: without it, "SMTP host" is a box in an admin screen that will happily open a
    /// socket to 169.254.169.254 or to something on the loopback interface. The old sender had no
    /// such check, which mattered far less when the host could only come from a deployment config
    /// file and matters a great deal now that it is editable in the console.</para>
    /// </summary>
    public sealed class SmtpEmailSender : IEmailSender
    {
        /// <summary>
        /// The one port that means implicit TLS. 587 and 25 are STARTTLS ports; 2525 is a common
        /// alternate for hosts that block 587, and is also STARTTLS.
        /// </summary>
        private const int ImplicitTlsPort = 465;

        private readonly NotificationsOptions _options;
        private readonly ILogger<SmtpEmailSender> _logger;

        public SmtpEmailSender(IOptions<NotificationsOptions> options, ILogger<SmtpEmailSender> logger)
        {
            _options = options.Value;
            _logger = logger;
        }

        public async Task<EmailDeliveryReceipt?> SendAsync(EmailMessage message, CancellationToken ct = default)
        {
            var smtp = _options.Smtp;
            if (string.IsNullOrWhiteSpace(smtp.Host))
                throw new OutboundEmailTransportException(
                    OutboundEmailFailureKind.NotConfigured,
                    "SMTP host is not configured. Cannot send email.");

            var port = smtp.Port > 0 ? smtp.Port : 587;
            var messageId = MimeUtils.GenerateMessageId();
            using var mime = BuildMimeMessage(message, messageId);

            // Implicit TLS when the port says so; STARTTLS otherwise, and REQUIRED rather than
            // optional when TLS is asked for at all — StartTlsWhenAvailable would silently send
            // credentials in the clear against a server that fails to advertise STARTTLS.
            var socketOptions = !smtp.EnableSsl
                ? SecureSocketOptions.None
                : port == ImplicitTlsPort
                    ? SecureSocketOptions.SslOnConnect
                    : SecureSocketOptions.StartTls;

            using var client = new MailKit.Net.Smtp.SmtpClient
            {
                Timeout = smtp.TimeoutMs > 0 ? smtp.TimeoutMs : 30000
            };

            _logger.LogInformation(
                "[SmtpEmailSender] Sending via {Host}:{Port} ({Tls}) Tenant={TenantId} BU={BusinessUnitId} Subject=\"{Subject}\"",
                smtp.Host, port, socketOptions, message.TenantId ?? "-", message.BusinessUnitId ?? "-",
                message.Subject);

            using var socket = await MailEndpointPolicy.ConnectAsync(smtp.Host, port, ct).ConfigureAwait(false);
            await client.ConnectAsync(socket, smtp.Host, port, socketOptions, ct).ConfigureAwait(false);

            // Anonymous relay is legitimate on an internal smarthost, so an absent username is not
            // an error — but a username with no password is a half-filled form, and letting it
            // through produces an authentication failure that reads like a wrong password.
            if (!string.IsNullOrWhiteSpace(smtp.Username))
                await client.AuthenticateAsync(smtp.Username, smtp.Password ?? string.Empty, ct)
                    .ConfigureAwait(false);

            await client.SendAsync(mime, ct).ConfigureAwait(false);
            await client.DisconnectAsync(quit: true, ct).ConfigureAwait(false);

            _logger.LogInformation(
                "[SmtpEmailSender] Email sent Tenant={TenantId} Subject=\"{Subject}\"",
                message.TenantId ?? "-", message.Subject);
            return new EmailDeliveryReceipt("smtp", messageId, DateTimeOffset.UtcNow);
        }

        private MimeMessage BuildMimeMessage(EmailMessage message, string messageId)
        {
            var from = message.From ?? new EmailAddress(_options.FromAddress, _options.FromName ?? string.Empty);
            var defaultReplyTo = string.IsNullOrWhiteSpace(_options.ReplyToAddress)
                ? null
                : new EmailAddress(_options.ReplyToAddress!);
            return MimeMessageComposer.Compose(message, messageId, from, defaultReplyTo);
        }
    }
}
