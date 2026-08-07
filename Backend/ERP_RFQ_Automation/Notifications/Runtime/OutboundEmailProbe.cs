using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace ERP_RFQ_Automation.Notifications.Runtime
{
    /// <summary>Outcome of one verification send, in terms the console can render without
    /// interpreting anything.</summary>
    /// <param name="Transmitted">False when nothing left the process — a console provider, or the
    /// outbound guard in DraftOnly. Separate from <paramref name="Succeeded"/> on purpose: both are
    /// "no error", and only one of them means mail works.</param>
    /// <param name="EffectiveRecipient">Where the message actually went. Differs from the requested
    /// address when the outbound guard is redirecting, and saying so is the difference between an
    /// operator who understands why their inbox is empty and one who concludes the test lied.</param>
    public sealed record OutboundEmailProbeResult(
        bool Succeeded,
        bool Transmitted,
        OutboundEmailFailureKind Kind,
        string Message,
        string? ProviderStatus,
        string Provider,
        string OutboundGuardMode,
        string IntendedRecipient,
        string EffectiveRecipient,
        string? AcceptanceReference,
        DateTimeOffset AttemptedAtUtc,
        long ElapsedMs);

    /// <summary>
    /// Sends one real message with a given configuration and reports what happened.
    ///
    /// <para><b>The most valuable endpoint in this module.</b> Configuring SMTP blind is how an
    /// operator discovers it is broken at the worst possible moment — a customer has been
    /// provisioned, the founding administrator is waiting for an activation link, and the only
    /// signal is <c>emailSent: false</c>. A verification send against the CANDIDATE settings, before
    /// they are trusted, converts that into a thirty-second check with a named cause.</para>
    ///
    /// <para><b>Candidate, not saved.</b> The settings under test are passed in, so an operator can
    /// verify what they are typing before committing it. A save-then-test flow would mean the only
    /// way to test a password is to first make it the live one — which is how a working
    /// configuration gets replaced by a broken one.</para>
    /// </summary>
    public sealed class OutboundEmailProbe
    {
        /// <summary>Ceiling on one verification attempt. The providers have their own timeouts, but
        /// an operator-supplied host can be anything, including an address that black-holes packets,
        /// and an HTTP request must not hang on it.</summary>
        private static readonly TimeSpan MaximumAttempt = TimeSpan.FromSeconds(60);

        private readonly OutboundEmailTransportResolver _resolver;
        private readonly ILogger<OutboundEmailProbe> _log;
        private readonly TimeProvider _time;

        public OutboundEmailProbe(
            OutboundEmailTransportResolver resolver,
            ILogger<OutboundEmailProbe> log,
            TimeProvider? timeProvider = null)
        {
            _resolver = resolver;
            _log = log;
            _time = timeProvider ?? TimeProvider.System;
        }

        public async Task<OutboundEmailProbeResult> SendAsync(
            OutboundEmailSettingsSnapshot candidate,
            string recipient,
            string? actorLabel,
            CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(candidate);
            ArgumentException.ThrowIfNullOrWhiteSpace(recipient);

            var intended = recipient.Trim();
            var startedAt = _time.GetUtcNow();
            var stopwatch = Stopwatch.StartNew();

            var message = Compose(candidate, intended, actorLabel, startedAt);
            var sender = _resolver.BuildTransport(candidate);

            using var attemptTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            attemptTimeout.CancelAfter(MaximumAttempt);

            try
            {
                var receipt = await sender.SendAsync(message, attemptTimeout.Token).ConfigureAwait(false);
                stopwatch.Stop();

                var transmitted = candidate.TransmitsMail &&
                                  candidate.GuardMode != OutboundEmailMode.DraftOnly;

                return new OutboundEmailProbeResult(
                    Succeeded: true,
                    Transmitted: transmitted,
                    Kind: OutboundEmailFailureKind.None,
                    Message: DescribeSuccess(candidate, intended, EffectiveRecipient(message, intended)),
                    ProviderStatus: null,
                    Provider: candidate.NormalizedProvider,
                    OutboundGuardMode: candidate.GuardMode.ToString(),
                    IntendedRecipient: intended,
                    EffectiveRecipient: EffectiveRecipient(message, intended),
                    AcceptanceReference: receipt?.AcceptanceReference,
                    AttemptedAtUtc: startedAt,
                    ElapsedMs: stopwatch.ElapsedMilliseconds);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                stopwatch.Stop();
                var failure = new OutboundEmailFailure(
                    OutboundEmailFailureKind.Timeout,
                    OutboundEmailFailureClassifier.Describe(OutboundEmailFailureKind.Timeout),
                    null);
                return Failed(candidate, intended, message, failure, startedAt, stopwatch.ElapsedMilliseconds);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                stopwatch.Stop();
                var failure = OutboundEmailFailureClassifier.Classify(exception);

                // The provider's own text is logged and never returned. It is unbounded, differs by
                // server, and can echo the submitted envelope; the operator gets our classification
                // and the action that fixes it.
                _log.LogWarning(exception,
                    "[Notifications] Verification send failed via {Provider} to {Recipient}: {Kind}.",
                    candidate.NormalizedProvider, intended, failure.Kind);

                return Failed(candidate, intended, message, failure, startedAt, stopwatch.ElapsedMilliseconds);
            }
        }

        private static OutboundEmailProbeResult Failed(
            OutboundEmailSettingsSnapshot candidate,
            string intended,
            EmailMessage message,
            OutboundEmailFailure failure,
            DateTimeOffset startedAt,
            long elapsedMs) =>
            new(
                Succeeded: false,
                Transmitted: false,
                Kind: failure.Kind,
                Message: failure.Message,
                ProviderStatus: failure.ProviderStatus,
                Provider: candidate.NormalizedProvider,
                OutboundGuardMode: candidate.GuardMode.ToString(),
                IntendedRecipient: intended,
                EffectiveRecipient: EffectiveRecipient(message, intended),
                AcceptanceReference: null,
                AttemptedAtUtc: startedAt,
                ElapsedMs: elapsedMs);

        /// <summary>The guard rewrites <c>To</c> in place when it redirects, so the message itself is
        /// the record of where the send was actually aimed.</summary>
        private static string EffectiveRecipient(EmailMessage message, string intended) =>
            message.To.FirstOrDefault()?.Address ?? intended;

        private static string DescribeSuccess(
            OutboundEmailSettingsSnapshot candidate, string intended, string effective)
        {
            if (!candidate.TransmitsMail)
                return "The console provider logged this message instead of sending it. NOTHING was " +
                       "transmitted — choose SMTP or SendGrid before relying on email.";

            if (candidate.GuardMode == OutboundEmailMode.DraftOnly)
                return "The outbound guard is in DraftOnly mode, so the message was logged and discarded. " +
                       "The provider configuration was not exercised.";

            if (!string.Equals(effective, intended, StringComparison.OrdinalIgnoreCase))
                return $"Accepted by the provider, but the outbound guard redirected it to {effective} " +
                       $"instead of {intended}. That is the guard working as configured — real mail is " +
                       "being contained the same way.";

            return $"Accepted by the provider for delivery to {effective}. If it does not arrive, the " +
                   "problem is downstream of us: check the recipient's spam folder and the sending " +
                   "domain's SPF/DKIM/DMARC records.";
        }

        private static EmailMessage Compose(
            OutboundEmailSettingsSnapshot candidate, string recipient, string? actorLabel, DateTimeOffset when)
        {
            var actor = string.IsNullOrWhiteSpace(actorLabel) ? "a platform operator" : actorLabel!.Trim();
            var stamp = when.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss 'UTC'");

            // Deliberately not rendered through the branded template set. This message exists to
            // prove a channel works, so it must depend on as little as possible beyond the channel:
            // no template lookup, no CTA link, no attachment.
            var message = new EmailMessage
            {
                Subject = "Nexora outbound email test",
                TextBody =
                    "This is a test message from Nexora.\r\n\r\n" +
                    $"It was sent by {actor} at {stamp} to verify that outbound email delivery is working.\r\n" +
                    $"Provider: {candidate.NormalizedProvider}. From: {candidate.FromAddress}.\r\n\r\n" +
                    "If you received this, transactional email — activation links, quotes, order " +
                    "confirmations — will reach this address.\r\n\r\n" +
                    "No action is required.",
                HtmlBody =
                    "<p>This is a test message from Nexora.</p>" +
                    $"<p>It was sent by {System.Net.WebUtility.HtmlEncode(actor)} at {stamp} to verify that " +
                    "outbound email delivery is working.<br>" +
                    $"Provider: <strong>{System.Net.WebUtility.HtmlEncode(candidate.NormalizedProvider)}</strong>. " +
                    $"From: {System.Net.WebUtility.HtmlEncode(candidate.FromAddress)}.</p>" +
                    "<p>If you received this, transactional email &mdash; activation links, quotes, order " +
                    "confirmations &mdash; will reach this address.</p>" +
                    "<p>No action is required.</p>"
            };
            message.AddTo(recipient);
            return message;
        }
    }
}
