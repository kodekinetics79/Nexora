using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace ERP_RFQ_Automation.Notifications.Runtime
{
    /// <summary>
    /// The single <see cref="IEmailSender"/> in the container. Every send resolves the transport
    /// that the platform configuration currently selects and delegates to it.
    ///
    /// <para><b>Containment is preserved by type, not by convention.</b>
    /// <see cref="OutboundEmailTransportResolver.ResolveAsync"/> hands back a
    /// <see cref="GuardedEmailSender"/> — the concrete decorator — so this class has no way to
    /// obtain, and therefore no way to leak, a raw transport. The property the old registration
    /// achieved by wiring (the decorator is the only <c>IEmailSender</c>, a future transport is
    /// wrapped by construction) is achieved here by the resolver's signature, which is the stronger
    /// version of the same guarantee: it survives someone editing this file.</para>
    ///
    /// <para>It also records the outcome. <see cref="INotificationService"/> deliberately swallows
    /// send failures so a mail problem cannot break a business transaction, which means the only
    /// place a failure can be observed at all is here, on the way past.</para>
    /// </summary>
    public sealed class RuntimeConfiguredEmailSender : IEmailSender
    {
        private readonly OutboundEmailTransportResolver _resolver;
        private readonly IOutboundEmailHealth _health;
        private readonly ILogger<RuntimeConfiguredEmailSender> _log;
        private readonly TimeProvider _time;
        private readonly IOutboundSenderResolver? _senders;

        /// <param name="senders">The per-tenant sender authority (issue #54). Optional so the
        /// platform-only harnesses keep compiling; in the composed application it is always
        /// registered, and a message with <see cref="EmailMessage.OwningBusinessUnitId"/> set is
        /// sent from that tenant's mailbox when it has one.</param>
        public RuntimeConfiguredEmailSender(
            OutboundEmailTransportResolver resolver,
            IOutboundEmailHealth health,
            ILogger<RuntimeConfiguredEmailSender> log,
            TimeProvider? timeProvider = null,
            IOutboundSenderResolver? senders = null)
        {
            _resolver = resolver;
            _health = health;
            _log = log;
            _time = timeProvider ?? TimeProvider.System;
            _senders = senders;
        }

        public async Task<EmailDeliveryReceipt?> SendAsync(EmailMessage message, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(message);
            var sender = await ResolveSenderAsync(message.OwningBusinessUnitId, ct).ConfigureAwait(false);

            // The resolved identity, per send. This is the line an operator reads when a customer
            // asks "which address did my quote go out from?" — it must name the mailbox, not the
            // provider.
            _log.LogInformation(
                "[Notifications] Sending \"{Subject}\" for BU {BusinessUnitId}: from={From} origin={Origin} provider={Provider} host={Host} mailbox={MailboxId}",
                message.Subject, message.OwningBusinessUnitId?.ToString() ?? "-", sender.FromAddress, sender.Origin,
                sender.Provider, sender.Host ?? "-", sender.MailboxId?.ToString() ?? "-");

            try
            {
                var receipt = await sender.Sender.SendAsync(message, ct).ConfigureAwait(false);

                // Success is recorded only when something was actually transmitted. A console send
                // and a DraftOnly withholding both return null having moved no bytes, and marking
                // either as a healthy send would reproduce the exact defect this module was rebuilt
                // to remove: a green surface over a channel that delivers nothing.
                if (sender.TransmitsMail && sender.GuardMode != OutboundEmailMode.DraftOnly)
                    _health.RecordSuccess(sender.Provider, _time.GetUtcNow());

                return receipt;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                var failure = OutboundEmailFailureClassifier.Classify(exception);
                _health.RecordFailure(failure, _time.GetUtcNow());

                // The exception itself carries the provider's own text; it goes to the log, where
                // an engineer can read it, and never to the API, where an operator would be shown
                // an unbounded string from a third party.
                _log.LogError(exception,
                    "[Notifications] Outbound send failed via {Provider} ({Origin}, from {From}): {Kind}. {Guidance}",
                    sender.Provider, sender.Origin, sender.FromAddress, failure.Kind, failure.Message);

                throw;
            }
        }

        private async Task<ResolvedOutboundSender> ResolveSenderAsync(long? owningBusinessUnitId, CancellationToken ct)
        {
            if (_senders is not null)
                return await _senders.ResolveAsync(owningBusinessUnitId, ct).ConfigureAwait(false);

            // No tenant authority registered: platform-only, exactly the pre-#54 behaviour.
            var transport = await _resolver.ResolveAsync(ct).ConfigureAwait(false);
            return new ResolvedOutboundSender(
                transport.Settings.Origin == OutboundEmailSettingsOrigin.Platform
                    ? OutboundSenderOrigin.Platform
                    : OutboundSenderOrigin.Configuration,
                transport.Sender,
                transport.Settings.NormalizedProvider,
                transport.Settings.TransmitsMail,
                transport.Settings.GuardMode,
                transport.Settings.FromAddress,
                transport.Settings.FromName,
                transport.Settings.NormalizedProvider == "smtp" ? transport.Settings.SmtpHost : null,
                null, null, transport.Settings);
        }
    }
}
