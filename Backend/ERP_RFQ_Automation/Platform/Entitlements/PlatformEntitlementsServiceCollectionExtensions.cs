using Microsoft.AspNetCore.Mvc;

namespace ERP_RFQ_Automation.Platform.Entitlements;

/// <summary>
/// DI wiring for tenant-status + plan-entitlement enforcement (WS-A). The Integration
/// Owner calls <see cref="AddPlatformEntitlements"/> from Program.cs (services phase)
/// and <c>app.UseTenantStatusGuard()</c> immediately after <c>app.UseTenantClaimGuard()</c>.
/// </summary>
public static class PlatformEntitlementsServiceCollectionExtensions
{
    public static IServiceCollection AddPlatformEntitlements(this IServiceCollection services)
    {
        services.AddMemoryCache();
        services.AddScoped<ITenantAccessService, TenantAccessService>();
        services.AddScoped<IEntitlementService, EntitlementService>();
        // Render escaped entitlement denials as problem+json instead of a generic 500.
        services.Configure<MvcOptions>(o => o.Filters.Add<EntitlementProblemFilter>());
        return services;
    }
}
