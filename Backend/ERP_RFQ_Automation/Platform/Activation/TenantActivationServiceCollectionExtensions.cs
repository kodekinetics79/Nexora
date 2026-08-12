namespace ERP_RFQ_Automation.Platform.Activation;

public static class TenantActivationServiceCollectionExtensions
{
    public static IServiceCollection AddTenantActivationPolicy(this IServiceCollection services)
    {
        services.AddScoped<ITenantActivationPolicyService, TenantActivationPolicyService>();

        // Singleton: the scanner selection is computed from configuration that does not change
        // within a process lifetime, and it is exactly what Program.cs already resolved at startup.
        services.AddSingleton<IPlatformDeploymentPosture, PlatformDeploymentPosture>();

        // Singleton for the same reason: the only per-tenant integration configuration this product
        // has is a configuration section, read the same way the procurement callback endpoint reads
        // it. Registered here rather than left optional because an unwired inventory silently keeps
        // integrations.mandatory demanding evidence from tenants that have no integration — the
        // exact defect it exists to close.
        services.AddSingleton<ITenantMandatoryIntegrationInventory, ConfiguredMandatoryIntegrationInventory>();
        return services;
    }
}
