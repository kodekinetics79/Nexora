using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ERP_RFQ_Automation.Notifications.Runtime;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;

namespace ERP_RFQ_Automation.Notifications
{
    /// <param name="LastSuccessUtc">When a message was last handed to a real transport and accepted.
    /// Null means "never" — including the case where every send so far went to the console
    /// provider, which is not a success and must never be recorded as one.</param>
    public sealed record OutboundEmailChannelStatus(
        DateTimeOffset? LastSuccessUtc,
        string? LastSuccessProvider,
        DateTimeOffset? LastFailureUtc,
        OutboundEmailFailureKind LastFailureKind,
        string? LastFailureMessage,
        int ConsecutiveFailures);

    /// <summary>
    /// CHANNEL health for outbound mail, in the same shape and for the same reason as
    /// <c>IEmailPollerHealth</c> does for the inbound mailbox.
    ///
    /// <para>The inbound equivalent exists because a poll loop beat its heartbeat 1.5 ms after an
    /// authentication failure and every surface stayed green while the door was shut. Outbound has
    /// the identical shape and one extra trap: <see cref="INotificationService"/> catches and
    /// swallows send failures on purpose, so that a mail problem can never break a business
    /// transaction. Correct — and it means a broken provider produces no exception anywhere a human
    /// looks. This ledger is where that gets recorded.</para>
    ///
    /// <para>Process-local, like every other heartbeat in the codebase, and therefore reset by a
    /// restart. The durable half of the picture is the verification stamp on the settings row
    /// (written by the test-send endpoint), which a restart cannot launder. The two together are
    /// what the status readout renders.</para>
    /// </summary>
    public interface IOutboundEmailHealth
    {
        /// <summary>Records a message actually accepted by a transmitting provider. Deliberately
        /// NOT called for console sends or for messages the outbound guard withheld: claiming a
        /// success there is exactly the lie this ledger exists to stop telling.</summary>
        void RecordSuccess(string provider, DateTimeOffset whenUtc);

        void RecordFailure(OutboundEmailFailure failure, DateTimeOffset whenUtc);

        OutboundEmailChannelStatus Snapshot();
    }

    public sealed class OutboundEmailHealth : IOutboundEmailHealth
    {
        private readonly object _gate = new();
        private DateTimeOffset? _lastSuccess;
        private string? _lastSuccessProvider;
        private DateTimeOffset? _lastFailure;
        private OutboundEmailFailureKind _lastFailureKind = OutboundEmailFailureKind.None;
        private string? _lastFailureMessage;
        private int _consecutiveFailures;

        public void RecordSuccess(string provider, DateTimeOffset whenUtc)
        {
            lock (_gate)
            {
                _lastSuccess = whenUtc;
                _lastSuccessProvider = provider;
                _consecutiveFailures = 0;
            }
        }

        public void RecordFailure(OutboundEmailFailure failure, DateTimeOffset whenUtc)
        {
            ArgumentNullException.ThrowIfNull(failure);
            lock (_gate)
            {
                _lastFailure = whenUtc;
                _lastFailureKind = failure.Kind;
                _lastFailureMessage = failure.Message;
                _consecutiveFailures++;
            }
        }

        public OutboundEmailChannelStatus Snapshot()
        {
            lock (_gate)
                return new OutboundEmailChannelStatus(
                    _lastSuccess, _lastSuccessProvider, _lastFailure,
                    _lastFailureKind, _lastFailureMessage, _consecutiveFailures);
        }
    }

    /// <summary>
    /// Makes the silence audible.
    ///
    /// <para><b>Degraded, never Unhealthy.</b> A deployment whose effective provider is
    /// <c>console</c> outside Development is sending nothing, and that must be visible on a surface
    /// somebody already watches — but it is not a reason to drain traffic. <c>/ready</c> returning
    /// 503 would pull the instance out of rotation and take the whole product down over a mail
    /// misconfiguration, which trades a silent failure for a loud outage. Degraded shows in the
    /// <c>/ready</c> payload and leaves the HTTP status at 200, which is the correct weight: the
    /// product works, its mail does not.</para>
    ///
    /// <para>Development is exempt because console IS the right provider there — a developer
    /// machine sending real mail from a half-written template is the failure mode that exemption
    /// prevents.</para>
    /// </summary>
    public sealed class OutboundEmailHealthCheck : IHealthCheck
    {
        private readonly OutboundEmailTransportResolver _resolver;
        private readonly IOutboundEmailHealth _health;
        private readonly IHostEnvironment _environment;

        public OutboundEmailHealthCheck(
            OutboundEmailTransportResolver resolver,
            IOutboundEmailHealth health,
            IHostEnvironment environment)
        {
            _resolver = resolver;
            _health = health;
            _environment = environment;
        }

        public async Task<HealthCheckResult> CheckHealthAsync(
            HealthCheckContext context, CancellationToken cancellationToken = default)
        {
            var settings = (await _resolver.ResolveAsync(cancellationToken).ConfigureAwait(false)).Settings;
            var channel = _health.Snapshot();

            var data = new Dictionary<string, object>
            {
                ["provider"] = settings.NormalizedProvider,
                ["origin"] = settings.Origin.ToString(),
                ["transmits"] = settings.TransmitsMail,
                ["outboundGuard"] = settings.GuardMode.ToString(),
                ["lastSuccessUtc"] = channel.LastSuccessUtc?.ToString("O") ?? "never",
                ["consecutiveFailures"] = channel.ConsecutiveFailures
            };
            if (channel.LastFailureUtc is { } failedAt)
            {
                data["lastFailureUtc"] = failedAt.ToString("O");
                data["lastFailureKind"] = channel.LastFailureKind.ToString();
            }

            if (!settings.TransmitsMail && !_environment.IsDevelopment())
                return HealthCheckResult.Degraded(
                    "Outbound email provider is 'console': messages are logged, not sent. Activation links, " +
                    "quotes and invoice notices are being discarded. Configure SMTP or SendGrid in the " +
                    "platform console (Notifications → Email).", data: data);

            if (settings.TransmitsMail && !settings.HasProviderCredentials)
                return HealthCheckResult.Degraded(
                    $"Outbound email provider is '{settings.NormalizedProvider}' but its credentials are not set. " +
                    "Every send will fail.", data: data);

            if (channel.ConsecutiveFailures > 0)
                return HealthCheckResult.Degraded(
                    $"Outbound email has failed {channel.ConsecutiveFailures} time(s) in a row " +
                    $"({channel.LastFailureKind}). {channel.LastFailureMessage}", data: data);

            return HealthCheckResult.Healthy(
                settings.TransmitsMail
                    ? $"Outbound email is sending via {settings.NormalizedProvider}."
                    : "Outbound email is in console mode (Development).",
                data);
        }
    }
}
