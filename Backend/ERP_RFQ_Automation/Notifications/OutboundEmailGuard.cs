using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ERP_RFQ_Automation.Notifications
{
    /// <summary>
    /// How outbound mail is constrained before it reaches a transport.
    /// </summary>
    public enum OutboundEmailMode
    {
        /// <summary>No constraint. Mail goes wherever the message says. Production behaviour.</summary>
        Live = 0,

        /// <summary>Only recipients on the allow-list are delivered. Anything else is REFUSED,
        /// loudly, so the caller sees a failure rather than believing a send succeeded.</summary>
        AllowListOnly = 1,

        /// <summary>Every recipient is rewritten to the sink address. Nothing can reach a real
        /// counterparty even by mistake. This is the correct mode for rehearsal against real
        /// supplier data.</summary>
        Redirect = 2,

        /// <summary>Nothing is transmitted at all; the message is logged and discarded.</summary>
        DraftOnly = 3
    }

    /// <summary>
    /// Non-production containment for outbound email.
    ///
    /// <para><b>Why this exists.</b> The dispatch pipeline (durable outbox → leased worker →
    /// SMTP/SendGrid) is production-grade, but it had no notion of a permitted recipient. The
    /// only thing standing between a rehearsal run and a real supplier's inbox was whichever
    /// address happened to be on the supplier record. A supplier RFQ sent by accident is not a
    /// bug you can roll back — it is a commercial communication from Nexora to a third party.</para>
    ///
    /// <para><b>Default is <see cref="OutboundEmailMode.Live"/></b> so that binding this section
    /// changes nothing for an existing deployment. Containment is opt-in and explicit; a mode
    /// that silently swallowed production mail would be its own outage.</para>
    /// </summary>
    public sealed class OutboundEmailGuardOptions
    {
        public string Mode { get; set; } = nameof(OutboundEmailMode.Live);

        /// <summary>Exact addresses that may receive mail. Case-insensitive.</summary>
        public List<string> AllowedRecipients { get; set; } = new();

        /// <summary>Whole domains that may receive mail, with or without a leading '@'.</summary>
        public List<string> AllowedDomains { get; set; } = new();

        /// <summary>Sink address used by <see cref="OutboundEmailMode.Redirect"/>.</summary>
        public string? RedirectTo { get; set; }

        /// <summary>Prefix stamped onto the subject in any non-Live mode, so a message that does
        /// land in a human inbox is unmistakably a rehearsal.</summary>
        public string SubjectTag { get; set; } = "[NEXORA TEST]";

        public OutboundEmailMode ResolvedMode =>
            Enum.TryParse<OutboundEmailMode>((Mode ?? string.Empty).Trim(), ignoreCase: true, out var parsed)
                ? parsed
                : OutboundEmailMode.Live;

        public bool IsAllowed(string? address)
        {
            if (string.IsNullOrWhiteSpace(address)) return false;
            var value = address.Trim();

            if (AllowedRecipients.Any(a => string.Equals(a?.Trim(), value, StringComparison.OrdinalIgnoreCase)))
                return true;

            var at = value.LastIndexOf('@');
            if (at < 0 || at == value.Length - 1) return false;
            var domain = value[(at + 1)..];

            return AllowedDomains.Any(d =>
                !string.IsNullOrWhiteSpace(d) &&
                string.Equals(d.Trim().TrimStart('@'), domain, StringComparison.OrdinalIgnoreCase));
        }

        public IReadOnlyList<string> Validate()
        {
            var warnings = new List<string>();
            if (!Enum.TryParse<OutboundEmailMode>((Mode ?? string.Empty).Trim(), true, out _))
                warnings.Add($"Notifications:OutboundGuard:Mode '{Mode}' is not recognised; falling back to Live (no containment).");
            if (ResolvedMode == OutboundEmailMode.Redirect && string.IsNullOrWhiteSpace(RedirectTo))
                warnings.Add("Notifications:OutboundGuard:Mode is 'Redirect' but RedirectTo is empty. Every send will be refused.");
            if (ResolvedMode == OutboundEmailMode.AllowListOnly && AllowedRecipients.Count == 0 && AllowedDomains.Count == 0)
                warnings.Add("Notifications:OutboundGuard:Mode is 'AllowListOnly' but the allow-list is empty. Every send will be refused.");
            return warnings;
        }
    }

    /// <summary>Raised when containment refuses a send. Deliberately a hard failure: the outbox
    /// records it as an error and a human sees it, rather than the run reporting success while
    /// nothing was delivered.</summary>
    public sealed class OutboundEmailBlockedException : InvalidOperationException
    {
        public OutboundEmailBlockedException(string message) : base(message) { }
    }

    /// <summary>
    /// Decorates whichever <see cref="IEmailSender"/> the configuration selected. A decorator
    /// rather than a change to each provider, so containment cannot be bypassed by adding a
    /// fourth transport later, and so the durable outbox, the leased worker and the retry
    /// semantics are all untouched.
    /// </summary>
    public sealed class GuardedEmailSender : IEmailSender
    {
        private readonly IEmailSender _inner;
        private readonly OutboundEmailGuardOptions _guard;
        private readonly ILogger<GuardedEmailSender> _log;

        public GuardedEmailSender(
            IEmailSender inner,
            IOptions<NotificationsOptions> options,
            ILogger<GuardedEmailSender> log)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            _guard = (options?.Value?.OutboundGuard) ?? new OutboundEmailGuardOptions();
            _log = log;
        }

        public Task<EmailDeliveryReceipt?> SendAsync(EmailMessage message, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(message);
            var mode = _guard.ResolvedMode;
            if (mode == OutboundEmailMode.Live) return _inner.SendAsync(message, ct);

            var originals = message.To.Concat(message.Cc).Concat(message.Bcc)
                .Select(a => a?.Address).Where(a => !string.IsNullOrWhiteSpace(a)).ToArray();

            if (mode == OutboundEmailMode.DraftOnly)
            {
                _log.LogWarning(
                    "[OutboundGuard] DraftOnly: message '{Subject}' NOT transmitted. Intended recipients: {Recipients}.",
                    message.Subject, string.Join(", ", originals));
                return Task.FromResult<EmailDeliveryReceipt?>(null);
            }

            Tag(message, originals);

            if (mode == OutboundEmailMode.Redirect)
            {
                if (string.IsNullOrWhiteSpace(_guard.RedirectTo))
                    throw new OutboundEmailBlockedException(
                        "Outbound guard is in Redirect mode but Notifications:OutboundGuard:RedirectTo is not configured. Refusing to send.");

                // Cc/Bcc are cleared, not redirected: copying a sink on a message it already
                // receives adds nothing, and a surviving Bcc is exactly how a real address slips
                // through a rehearsal unnoticed.
                message.To.Clear();
                message.Cc.Clear();
                message.Bcc.Clear();
                message.AddTo(_guard.RedirectTo!.Trim(), "Nexora Test Sink");

                _log.LogWarning(
                    "[OutboundGuard] Redirect: '{Subject}' rerouted to {Sink}. Suppressed recipients: {Recipients}.",
                    message.Subject, _guard.RedirectTo, string.Join(", ", originals));
                return _inner.SendAsync(message, ct);
            }

            // AllowListOnly — fail closed on the first recipient that is not permitted.
            var blocked = originals.Where(a => !_guard.IsAllowed(a)).ToArray();
            if (blocked.Length > 0)
                throw new OutboundEmailBlockedException(
                    $"Outbound guard refused delivery to {string.Join(", ", blocked)}. " +
                    "Add the address or its domain to Notifications:OutboundGuard, or use Redirect mode.");

            if (originals.Length == 0)
                throw new OutboundEmailBlockedException("Outbound guard refused a message with no recipient.");

            _log.LogInformation(
                "[OutboundGuard] AllowListOnly: '{Subject}' permitted to {Recipients}.",
                message.Subject, string.Join(", ", originals));
            return _inner.SendAsync(message, ct);
        }

        private void Tag(EmailMessage message, IReadOnlyCollection<string?> originals)
        {
            var tag = _guard.SubjectTag?.Trim();
            if (!string.IsNullOrEmpty(tag) &&
                (message.Subject is null || !message.Subject.StartsWith(tag, StringComparison.OrdinalIgnoreCase)))
            {
                message.Subject = $"{tag} {message.Subject}".Trim();
            }

            // Deliberately no custom headers: EmailMessage carries none today and no provider
            // transmits them, so adding the field would be dead surface. The suppressed
            // recipients are recorded in the Warning logged by each mode above, which is the
            // evidence a pilot review actually reads.
            _ = originals;
        }
    }
}
