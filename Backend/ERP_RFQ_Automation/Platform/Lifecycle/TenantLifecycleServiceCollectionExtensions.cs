namespace ERP_RFQ_Automation.Platform.Lifecycle;

using Microsoft.Extensions.DependencyInjection.Extensions;

/// <summary>
/// Single entry point for the tenant offboarding module. One call from Program.cs, matching the
/// shape <c>AddTenantOnboarding</c> / <c>AddPlatformEntitlements</c> use — creation is registered
/// in one line and so is everything that happens after it.
/// </summary>
public static class TenantLifecycleServiceCollectionExtensions
{
    public static IServiceCollection AddTenantLifecycle(
        this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<TenantLifecycleOptions>(
            configuration.GetSection(TenantLifecycleOptions.SectionName));
        // Production always receives the trusted system clock. Test hosts may register an
        // advanceable TimeProvider before this module is added; TryAdd preserves that isolated
        // replacement without creating a production endpoint or configuration switch.
        services.TryAddSingleton(TimeProvider.System);

        // All scoped: every one of them writes through the request-scoped
        // ErpRfqAutomationContext, which is what lets the bookkeeping join the caller's
        // transaction instead of racing beside it. The purge is the exception that proves the
        // rule — it opens its own OWNER connection because it has to suspend the append-only
        // guards, and TenantOffboardingService is built around that being a separate transaction.
        services.AddScoped<TenantPurgeExecutor>();
        services.AddScoped<TenantPersonalDataEraser>();
        services.AddScoped<TenantDataExportService>();
        services.AddScoped<ITenantOffboardingReadinessService, TenantOffboardingReadinessService>();
        services.AddScoped<TenantOffboardingService>();
        services.AddScoped<TenantLegalHoldService>();

        // The background-path half of suspension enforcement. Scoped because it reads through the
        // scoped ITenantAccessService; the ~60s cache behind that is the process-wide singleton,
        // so a worker sweeping every tenant still issues at most one platform query per tenant
        // per cache window.
        services.AddScoped<ITenantWorkGate, TenantWorkGate>();

        return services;
    }
}
