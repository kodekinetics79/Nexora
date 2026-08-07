using ERP_RFQ_Automation.Notifications.Runtime;

namespace ERP_RFQ_Automation.Platform.Notifications;

/// <summary>
/// Loads the stored outbound configuration once during startup, and says out loud when the result
/// is a deployment that cannot send mail.
///
/// <para><b>Why a warm-up and not lazy loading.</b> The effective-options view
/// (<c>EffectiveNotificationsOptions</c>) is read synchronously from constructors on the request
/// path and must never block on I/O, so before the first load it reports the appsettings values.
/// Without this, the very first activation email after a restart could be composed with the
/// configuration <c>AppBaseUrl</c> while every later one used the stored one — a difference nobody
/// would reproduce and everybody would disbelieve. Loading here, before the host serves traffic,
/// removes the window instead of documenting it.</para>
///
/// <para><b>Why the warning is here rather than in AddNotifications.</b> A startup warning about
/// the provider is only true if it names the EFFECTIVE provider, and that is not knowable until the
/// row has been read. Logging "provider: console" from the configuration while a stored SendGrid
/// configuration is in force would train operators to ignore the line.</para>
///
/// <para>Runs with no HTTP context, so the command interceptor routes it to
/// <c>nexora_pipeline_app</c> — the platform plane's role, which holds the full grant on the
/// settings table.</para>
/// </summary>
public sealed class OutboundEmailSettingsWarmup : IHostedService
{
    private readonly OutboundEmailTransportResolver _resolver;
    private readonly IHostEnvironment _environment;
    private readonly ILogger<OutboundEmailSettingsWarmup> _log;

    public OutboundEmailSettingsWarmup(
        OutboundEmailTransportResolver resolver,
        IHostEnvironment environment,
        ILogger<OutboundEmailSettingsWarmup> log)
    {
        _resolver = resolver;
        _environment = environment;
        _log = log;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            var settings = (await _resolver.ResolveAsync(cancellationToken)).Settings;

            if (!settings.TransmitsMail && !_environment.IsDevelopment())
                // Error, not Warning. This is the exact state in which a provisioned tenant's
                // founding administrator never receives an activation link, every invoice notice is
                // discarded, and no other surface reports anything wrong. It has to be the loudest
                // line in the startup log of an environment where mail is supposed to work.
                _log.LogError(
                    "[Notifications] OUTBOUND EMAIL IS DISABLED in {Environment}: the effective provider is " +
                    "'console', which writes messages to this log and discards them. Activation links, quote " +
                    "deliveries and invoice notices are NOT being delivered. Configure SMTP or SendGrid at " +
                    "PUT /api/platform/notifications/email/settings and verify it with the test-send endpoint.",
                    _environment.EnvironmentName);
            else if (settings.TransmitsMail && !settings.HasProviderCredentials)
                _log.LogError(
                    "[Notifications] Outbound email provider is '{Provider}' but its credentials are missing. " +
                    "Every send will fail.", settings.NormalizedProvider);
            else
                _log.LogInformation(
                    "[Notifications] Outbound email ready: provider={Provider} origin={Origin} guard={Guard} from={From}.",
                    settings.NormalizedProvider, settings.Origin, settings.GuardMode, settings.FromAddress);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // A settings table that cannot be read at boot must not stop the boot: the resolver
            // falls back to configuration and the product runs. Loud, and not fatal.
            _log.LogError(exception,
                "[Notifications] Could not read the platform email settings at startup. Falling back to the " +
                "Notifications configuration section.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
