using System;
using ERP_RFQ_Automation.Notifications.Providers;
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
            // Bind + validate options from the "Notifications" section.
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
                    "[Notifications] Configured provider: {Provider}.", string.IsNullOrWhiteSpace(options.Provider) ? "console" : options.Provider);
            }

            // Templating + orchestration (provider-independent).
            services.TryAddSingleton<IEmailTemplateRenderer, EmailTemplateRenderer>();
            services.TryAddScoped<INotificationService, NotificationService>();

            // Named HttpClient for the SendGrid provider (via IHttpClientFactory).
            services.AddHttpClient(SendGridEmailSender.HttpClientName, client =>
            {
                client.Timeout = TimeSpan.FromSeconds(30);
            });

            // Select the transport by configuration. "console" is the safe default
            // for any unset / unrecognized value.
            // The transport is registered as its CONCRETE type, then IEmailSender resolves to the
            // containment decorator wrapping it. Registering the decorator as the only
            // IEmailSender is what makes containment unbypassable: a fourth transport added later
            // is wrapped by construction, and no caller can reach a raw sender through DI.
            switch ((options.Provider ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "smtp":
                    services.TryAddSingleton<SmtpEmailSender>();
                    services.TryAddSingleton<IEmailSender>(sp => new GuardedEmailSender(
                        sp.GetRequiredService<SmtpEmailSender>(),
                        sp.GetRequiredService<IOptions<NotificationsOptions>>(),
                        sp.GetRequiredService<ILogger<GuardedEmailSender>>()));
                    break;
                case "sendgrid":
                    services.TryAddSingleton<SendGridEmailSender>();
                    services.TryAddSingleton<IEmailSender>(sp => new GuardedEmailSender(
                        sp.GetRequiredService<SendGridEmailSender>(),
                        sp.GetRequiredService<IOptions<NotificationsOptions>>(),
                        sp.GetRequiredService<ILogger<GuardedEmailSender>>()));
                    break;
                case "console":
                default:
                    // Console never leaves the process, but it is wrapped anyway so that the
                    // DraftOnly/Redirect/AllowListOnly semantics a test asserts are identical
                    // whichever transport is configured.
                    services.TryAddSingleton<ConsoleEmailSender>();
                    services.TryAddSingleton<IEmailSender>(sp => new GuardedEmailSender(
                        sp.GetRequiredService<ConsoleEmailSender>(),
                        sp.GetRequiredService<IOptions<NotificationsOptions>>(),
                        sp.GetRequiredService<ILogger<GuardedEmailSender>>()));
                    break;
            }

            return services;
        }
    }
}
