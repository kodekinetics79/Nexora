using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Notifications.Runtime;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Platform.Notifications;

/// <summary>
/// Single entry point for platform-operator-configurable outbound email. One call from Program.cs,
/// matching the shape <c>AddNotifications</c> / <c>AddTenantOnboarding</c> use.
///
/// <para>Must be registered AFTER <c>AddNotifications</c>: it supplies the persistence source that
/// the transport resolver registered there reads through, and the warm-up drives that resolver.</para>
/// </summary>
public static class PlatformEmailServiceCollectionExtensions
{
    public static IServiceCollection AddPlatformEmailSettings(this IServiceCollection services)
    {
        // Scoped, because it reads through the request-scoped DbContext. The resolver creates its
        // own scope per read, so a settings load never enlists in — and can never be rolled back
        // by — the business transaction that happened to trigger a send.
        //
        // Both services take DbContext rather than the concrete context: they touch exactly one
        // self-contained platform table, and the looser dependency is what lets the tests exercise
        // the real projections and the real encryption against a model built from
        // ApplyPlatformEmailModel alone, before the context splice lands.
        services.AddScoped<IOutboundEmailSettingsSource>(sp =>
            new PlatformEmailSettingsStore(sp.GetRequiredService<ErpRfqAutomationContext>()));

        services.AddScoped<IPlatformEmailSettingsService>(sp =>
            new PlatformEmailSettingsService(
                sp.GetRequiredService<ErpRfqAutomationContext>(),
                sp.GetRequiredService<OutboundEmailTransportResolver>(),
                sp.GetRequiredService<OutboundEmailProbe>(),
                sp.GetRequiredService<ERP_RFQ_Automation.Notifications.IOutboundEmailHealth>(),
                sp.GetRequiredService<Services.IPlatformAuditService>(),
                sp.GetRequiredService<ILogger<PlatformEmailSettingsService>>()));

        services.AddHostedService<OutboundEmailSettingsWarmup>();

        return services;
    }
}
