using System;
using System.Threading;
using System.Threading.Tasks;
using ERP_RFQ_Automation.Notifications.Runtime;
using ERP_RFQ_Automation.Security;
using Microsoft.Extensions.Logging;
using MimeKit.Utils;

namespace ERP_RFQ_Automation.Notifications.Providers
{
    /// <summary>
    /// Sends through a TENANT's own SMTP mailbox (issue #54).
    ///
    /// <para>The From header is the tenant's verified mailbox address — the one the transport
    /// authenticates as — never a value taken from the message. A caller-supplied From would let
    /// one tenant's profile text become another domain's sender, which is the SPF/DMARC failure
    /// this class exists to end; a message that set one is sent with the mailbox identity and the
    /// requested address preserved as Reply-To when no Reply-To was given.</para>
    ///
    /// <para>Delivery goes through <see cref="IOutboundSmtpTransport"/>: the same egress policy,
    /// the same TLS interpretation of the row, and the same code the tenant's "Test connection"
    /// button exercised. Only <see cref="GuardedEmailSender"/> may hold an instance — see
    /// <see cref="OutboundSenderResolver.ForMailbox"/>.</para>
    /// </summary>
    public sealed class TenantSmtpEmailSender : IEmailSender
    {
        private readonly TenantOutboundSender _mailbox;
        private readonly IOutboundSmtpTransport _transport;
        private readonly ILogger<TenantSmtpEmailSender> _logger;

        public TenantSmtpEmailSender(
            TenantOutboundSender mailbox, IOutboundSmtpTransport transport, ILogger<TenantSmtpEmailSender> logger)
        {
            _mailbox = mailbox ?? throw new ArgumentNullException(nameof(mailbox));
            _transport = transport ?? throw new ArgumentNullException(nameof(transport));
            _logger = logger;
        }

        public async Task<EmailDeliveryReceipt?> SendAsync(EmailMessage message, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(message);

            if (message.OwningBusinessUnitId is { } owner && owner != _mailbox.BusinessUnitId)
                // Unreachable through the resolver, which builds this sender FOR the owning unit;
                // kept because it is the one invariant that must survive any future wiring.
                throw new InvalidOperationException(
                    $"Refusing to send a message owned by BU {owner} through the mailbox of BU {_mailbox.BusinessUnitId}.");

            var from = new EmailAddress(_mailbox.FromAddress, _mailbox.FromName);
            var replyTo = message.ReplyTo
                ?? (message.From is not null &&
                    !string.Equals(message.From.Address, _mailbox.FromAddress, StringComparison.OrdinalIgnoreCase)
                    ? message.From
                    : null);

            var messageId = MimeUtils.GenerateMessageId();
            using var mime = MimeMessageComposer.Compose(message, messageId, from, replyTo);

            _logger.LogInformation(
                "[TenantSmtpEmailSender] Sending via {Host}:{Port} mailbox={MailboxId} BU={BusinessUnitId} Subject=\"{Subject}\"",
                _mailbox.Host, _mailbox.Port, _mailbox.MailboxId, _mailbox.BusinessUnitId, message.Subject);

            await _transport.SendAsync(_mailbox.Configuration, mime, ct).ConfigureAwait(false);

            return new EmailDeliveryReceipt("smtp", messageId, DateTimeOffset.UtcNow);
        }
    }
}
