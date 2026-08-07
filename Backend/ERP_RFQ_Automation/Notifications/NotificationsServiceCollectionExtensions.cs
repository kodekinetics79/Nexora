using System;
using ERP_RFQ_Automation.Notifications.Providers;
using ERP_RFQ_Automation.Notifications.Runtime;
using ERP_RFQ_Automation.Notifications.Templating;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ERP_RFQ_Automation.Notifications
{
    /// <summary>
    /// Single entry point that registers the entire Notifications + Transactional
    /// Email module. Call once from Program.cs — see NotificationsWIRING.md.
    /// </summary>
    public static class NotificationsServiceCollectionExtensions
    {
        public static IServiceCollection AddNotifications(this IServiceCollection services, IConfiguration config)
        {
            // Bind + validate options from the "Notifications" section. This remains the FALLBACK
            // configuration: it is what the process uses until a platform operator stores a
            // configuration, and what it falls back to if that row can never be read.
            var section = config.GetSection(NotificationsOptions.SectionName);
            services.Configure<NotificationsOptions>(section);

            var options = new NotificationsOptions();
            section.Bind(options);

            // Emit configuration warnings once at startup (non-fatal).
            using (var sp = services.BuildServiceProvider())
            {
                var logger = sp.GetService<ILoggerFactory>()?.CreateLogger("Notifications");
                foreach (var warning in options.Validate())
                    logger?.LogWarning("[Notifications] {Warning}", warning);
                logger?.LogInformation(
                    "[Notifications] Fallback provider from configuration: {Provider}. A stored platform " +
                    "email configuration, if one exists, overrides this at runtime.",
                    string.IsNullOrWhiteSpace(options.Provider) ? "console" : options.Provider);
            }

            // Templating + orchestration (provider-independent).
            services.TryAddSingleton<IEmailTemplateRenderer, EmailTemplateRenderer>();
            services.TryAddScoped<INotificationService, NotificationService>();

            // Named HttpClient for the SendGrid provider (via IHttpClientFactory).
            services.AddHttpClient(SendGridEmailSender.HttpClientName, client =>
            {
                client.Timeout = TimeSpan.FromSeconds(30);
            });

            // ==== transport selection ================================================================
            //
            // The transport used to be chosen HERE, once, from Notifications:Provider, and registered
            // as a singleton — so the only way to configure outbound mail was to edit appsettings and
            // redeploy, the default was a provider that logs instead of sending, and nothing surfaced
            // either fact. The selection now happens per-resolve inside OutboundEmailTransportResolver,
            // against the persisted platform configuration.
            //
            // CONTAINMENT IS UNCHANGED, and this is the part to preserve if any of this is ever
            // rewritten. Registering the GuardedEmailSender decorator as the only IEmailSender is what
            // made containment unbypassable: a fourth transport added later is wrapped by
            // construction, and no caller can reach a raw sender through DI. A dynamic resolver
            // handing back an unwrapped sender would have silently disabled outbound containment for
            // every message in the product. Two things keep the property intact:
            //
            //   * OutboundEmailTransportResolver.BuildTransport and .ResolveAsync return the CONCRETE
            //     GuardedEmailSender type. There is no code path through them that can produce an
            //     unwrapped transport, whatever a future edit does inside them.
            //   * The concrete providers are deliberately NOT registered in the container any more.
            //     Previously SmtpEmailSender/SendGridEmailSender/ConsoleEmailSender each sat in DI as
            //     themselves so the decorator could be composed around one of them; that also meant a
            //     raw transport was resolvable by anyone who asked for it by type. The resolver
            //     constructs them directly instead, so the only sender obtainable from the container
            //     is the guarded one.
            services.TryAddSingleton<IOutboundEmailHealth, OutboundEmailHealth>();
            services.TryAddSingleton<OutboundEmailTransportResolver>();
            services.TryAddSingleton<OutboundEmailProbe>();
            services.TryAddSingleton<IEmailSender, RuntimeConfiguredEmailSender>();

            // Replaces the options manager for this one options type, so every existing consumer of
            // IOptions<NotificationsOptions> — the invitation service's activation-link builder above
            // all — sees the stored From address, Reply-To and AppBaseUrl rather than the ones bound
            // at startup. Registered AFTER services.Configure so it wins the closed-generic lookup;
            // the configuration binding it wraps is still the source of the fallback values.
            services.AddSingleton<IOptions<NotificationsOptions>>(sp =>
                new EffectiveNotificationsOptions(sp.GetRequiredService<OutboundEmailTransportResolver>()));

            // Registered here rather than beside the other checks in Program.cs so that the module
            // stays a one-line wiring. Degraded (not Unhealthy) by design — see OutboundEmailHealthCheck.
            services.AddHealthChecks()
                .AddCheck<OutboundEmailHealthCheck>("outbound-email", tags: new[] { "ready" });

            return services;
        }
    }
}
