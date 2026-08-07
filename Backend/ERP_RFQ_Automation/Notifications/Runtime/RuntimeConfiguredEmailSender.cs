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

        public RuntimeConfiguredEmailSender(
            OutboundEmailTransportResolver resolver,
            IOutboundEmailHealth health,
            ILogger<RuntimeConfiguredEmailSender> log,
            TimeProvider? timeProvider = null)
        {
            _resolver = resolver;
            _health = health;
            _log = log;
            _time = timeProvider ?? TimeProvider.System;
        }

        public async Task<EmailDeliveryReceipt?> SendAsync(EmailMessage message, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(message);
            var transport = await _resolver.ResolveAsync(ct).ConfigureAwait(false);

            try
            {
                var receipt = await transport.Sender.SendAsync(message, ct).ConfigureAwait(false);

                // Success is recorded only when something was actually transmitted. A console send
                // and a DraftOnly withholding both return null having moved no bytes, and marking
                // either as a healthy send would reproduce the exact defect this module was rebuilt
                // to remove: a green surface over a channel that delivers nothing.
                if (transport.Settings.TransmitsMail && transport.Settings.GuardMode != OutboundEmailMode.DraftOnly)
                    _health.RecordSuccess(transport.Settings.NormalizedProvider, _time.GetUtcNow());

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
                    "[Notifications] Outbound send failed via {Provider}: {Kind}. {Guidance}",
                    transport.Settings.NormalizedProvider, failure.Kind, failure.Message);

                throw;
            }
        }
    }
}
