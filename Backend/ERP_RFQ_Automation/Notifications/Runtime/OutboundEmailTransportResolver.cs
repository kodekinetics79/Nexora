using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using ERP_RFQ_Automation.Notifications.Providers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ERP_RFQ_Automation.Notifications.Runtime
{
    /// <summary>
    /// A transport that is ready to send, together with the settings it was built from.
    ///
    /// <para><b>The sender is typed as <see cref="GuardedEmailSender"/>, not as
    /// <see cref="IEmailSender"/>, and that is load-bearing.</b> The containment decorator was the
    /// only <c>IEmailSender</c> in the container precisely so that no caller could reach a raw
    /// transport and so that a transport added later would be wrapped by construction. Selecting
    /// the transport at runtime instead of at startup would have quietly dissolved that guarantee:
    /// a resolver returning <c>IEmailSender</c> can return an unwrapped provider and nothing —
    /// not the compiler, not a review — would notice until rehearsal mail reached a real supplier.
    /// Naming the concrete decorator in the signature makes an unwrapped return impossible to
    /// write.</para>
    /// </summary>
    public sealed record ResolvedOutboundTransport(
        GuardedEmailSender Sender,
        OutboundEmailSettingsSnapshot Settings);

    /// <summary>
    /// Resolves the active outbound transport from the persisted platform configuration, falling
    /// back to <c>IConfiguration</c> when nothing has been stored.
    ///
    /// <para><b>The failure this exists to end.</b> The transport used to be chosen once, in
    /// <c>AddNotifications</c>, from <c>Notifications:Provider</c>, and registered as a singleton.
    /// Changing it meant editing appsettings and redeploying; the default was <c>console</c>, which
    /// logs instead of sending; and nothing anywhere said so. A pilot deployment silently swallowed
    /// every activation link — the founding administrator of a freshly provisioned tenant simply
    /// never received their email, and the only trace was <c>emailSent: false</c> in a response
    /// body with no reason attached.</para>
    ///
    /// <para><b>Caching, and why it is not per-send.</b> Building a transport is cheap; reading the
    /// row is not free, and doing it on every send would put a database round trip on the path of
    /// every quote, RFQ and invoice notice. The built transport is cached and revalidated against
    /// the row's <see cref="OutboundEmailSettingsSnapshot.Version"/>, which is a two-column read.
    /// A save in this process invalidates immediately through <see cref="Invalidate"/>; another
    /// instance notices within <see cref="RevalidationInterval"/>. That bound is the honest cost of
    /// not holding a distributed cache invalidation channel for a table that changes twice a year.</para>
    ///
    /// <para><b>A read failure never stops mail.</b> If the source throws — the database is down,
    /// the role lost a grant — the last known good transport keeps sending, and a cold process
    /// falls back to configuration. The alternative (fail the send) would turn a settings-table
    /// problem into a total outbound outage, which is a strictly worse failure than the one this
    /// class was written to fix.</para>
    /// </summary>
    public sealed class OutboundEmailTransportResolver
    {
        /// <summary>How long a resolved transport is trusted before its version is rechecked. Sized
        /// for the operator experience: after saving, another instance starts using the new
        /// settings within this window, so "I fixed it and it is still broken" has a short and
        /// stateable end.</summary>
        public TimeSpan RevalidationInterval { get; set; } = TimeSpan.FromSeconds(30);

        private readonly IServiceScopeFactory _scopeFactory;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ILoggerFactory _loggerFactory;
        private readonly ILogger<OutboundEmailTransportResolver> _log;
        private readonly TimeProvider _time;
        private readonly NotificationsOptions _configured;
        private readonly SemaphoreSlim _rebuildGate = new(1, 1);

        private ResolvedOutboundTransport? _cached;
        private DateTimeOffset _validUntil;
        private bool _sourceFailureLogged;

        public OutboundEmailTransportResolver(
            IServiceScopeFactory scopeFactory,
            IHttpClientFactory httpClientFactory,
            IOptionsFactory<NotificationsOptions> optionsFactory,
            ILoggerFactory loggerFactory,
            TimeProvider? timeProvider = null)
        {
            _scopeFactory = scopeFactory;
            _httpClientFactory = httpClientFactory;
            _loggerFactory = loggerFactory;
            _log = loggerFactory.CreateLogger<OutboundEmailTransportResolver>();
            _time = timeProvider ?? TimeProvider.System;

            // IOptionsFactory rather than IOptions<NotificationsOptions>: this type is one of the
            // inputs to the effective-options view that REPLACES IOptions in the container, so
            // depending on IOptions here would close a cycle at the first resolution.
            _configured = optionsFactory.Create(Options.DefaultName);
        }

        /// <summary>
        /// The settings currently in force, with no I/O. Used by the effective-options view on the
        /// synchronous path, where blocking on a database read would be a deadlock waiting for a
        /// busy thread pool. Before the first successful load this reports configuration — which is
        /// exactly what the process is in fact using at that moment.
        /// </summary>
        public OutboundEmailSettingsSnapshot Current =>
            Volatile.Read(ref _cached)?.Settings ?? OutboundEmailSettingsSnapshot.FromOptions(_configured);

        /// <summary>Drops the cached transport so the next resolve rebuilds. Called after a save in
        /// this process; other instances converge within <see cref="RevalidationInterval"/>.</summary>
        public void Invalidate()
        {
            Volatile.Write(ref _cached, null);
            _validUntil = default;
        }

        public async Task<ResolvedOutboundTransport> ResolveAsync(CancellationToken ct = default)
        {
            var cached = Volatile.Read(ref _cached);
            if (cached is not null && _time.GetUtcNow() < _validUntil)
                return cached;

            await _rebuildGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                // Re-read under the gate: a burst of concurrent sends after an expiry must produce
                // one database read, not one per send.
                cached = Volatile.Read(ref _cached);
                var now = _time.GetUtcNow();
                if (cached is not null && now < _validUntil)
                    return cached;

                var stored = await LoadAsync(cached?.Settings, ct).ConfigureAwait(false);
                _validUntil = _time.GetUtcNow() + RevalidationInterval;

                if (cached is not null && stored.Version == cached.Settings.Version &&
                    stored.Origin == cached.Settings.Origin)
                    return cached;

                var resolved = new ResolvedOutboundTransport(BuildTransport(stored), stored);
                Volatile.Write(ref _cached, resolved);

                _log.LogInformation(
                    "[Notifications] Outbound transport resolved: provider={Provider} origin={Origin} version={Version} transmits={Transmits} guard={Guard}.",
                    stored.NormalizedProvider, stored.Origin, stored.Version, stored.TransmitsMail, stored.GuardMode);

                return resolved;
            }
            finally
            {
                _rebuildGate.Release();
            }
        }

        /// <summary>
        /// Builds a transport for arbitrary settings — the stored ones, or a candidate an operator
        /// is still editing in the test-send dialog.
        ///
        /// <para>The return type is the decorator, so containment applies to a candidate exactly as
        /// it applies to production traffic. A test send in Redirect mode goes to the sink and the
        /// result says so; a test send in AllowListOnly to an address nobody allowed is refused.
        /// Both are the truth about what this configuration would do with a real message, which is
        /// the only thing a verification is for.</para>
        /// </summary>
        public GuardedEmailSender BuildTransport(OutboundEmailSettingsSnapshot settings)
        {
            ArgumentNullException.ThrowIfNull(settings);
            var options = Options.Create(settings.ToNotificationsOptions());

            IEmailSender inner = settings.NormalizedProvider switch
            {
                "smtp" => new SmtpEmailSender(options, _loggerFactory.CreateLogger<SmtpEmailSender>()),
                "sendgrid" => new SendGridEmailSender(
                    _httpClientFactory, options, _loggerFactory.CreateLogger<SendGridEmailSender>()),
                _ => new ConsoleEmailSender(_loggerFactory.CreateLogger<ConsoleEmailSender>())
            };

            return new GuardedEmailSender(inner, options, _loggerFactory.CreateLogger<GuardedEmailSender>());
        }

        private async Task<OutboundEmailSettingsSnapshot> LoadAsync(
            OutboundEmailSettingsSnapshot? lastKnownGood, CancellationToken ct)
        {
            var fallback = lastKnownGood ?? OutboundEmailSettingsSnapshot.FromOptions(_configured);

            // A scope per read, because the source is scoped over the request-lifetime DbContext.
            // The scope also means a settings read cannot enlist in — or be rolled back by — the
            // caller's business transaction.
            using var scope = _scopeFactory.CreateScope();
            var source = scope.ServiceProvider.GetService<IOutboundEmailSettingsSource>();
            if (source is null)
                return OutboundEmailSettingsSnapshot.FromOptions(_configured);

            try
            {
                // Version first. When it is unchanged the credential columns are never selected at
                // all, which is what lets the least-privileged roles hold a narrower grant.
                if (lastKnownGood is { Origin: OutboundEmailSettingsOrigin.Platform })
                {
                    var version = await source.ReadVersionAsync(ct).ConfigureAwait(false);
                    if (version == lastKnownGood.Version) return lastKnownGood;
                }

                var stored = await source.ReadAsync(ct).ConfigureAwait(false);
                _sourceFailureLogged = false;
                return stored ?? OutboundEmailSettingsSnapshot.FromOptions(_configured);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // Once per outage, not once per send: this runs on the path of every notification
                // and a log line per message would bury the failure it is reporting.
                if (!_sourceFailureLogged)
                {
                    _sourceFailureLogged = true;
                    _log.LogError(exception,
                        "[Notifications] Could not read the platform email settings. Continuing with the " +
                        "{Origin} configuration (provider {Provider}). Outbound mail is unaffected; the " +
                        "operator console will not reflect edits until this clears.",
                        fallback.Origin, fallback.NormalizedProvider);
                }
                return fallback;
            }
        }
    }
}
