namespace ERP_RFQ_Automation.Platform.DataAssets;

public static class TenantDataAssetServiceCollectionExtensions
{
    public static IServiceCollection AddTenantDataAssetRegistry(this IServiceCollection services)
    {
        services.AddScoped<TenantDataAssetRegistryService>();
        services.AddScoped<TenantDataRecoveryService>();

        // Singleton: the manifest describes this deployment's own estate, read once from
        // configuration that does not change within a process lifetime. Registered
        // unconditionally — a deployment with no Platform:DataBoundaries section gets a manifest
        // that reports itself unconfigured, which is what keeps the manual registration path
        // working rather than a missing service that would throw at provisioning time.
        services.AddSingleton<IPlatformDataBoundaryManifest, PlatformDataBoundaryManifest>();

        // Stateless; scoped only to sit alongside the context it is handed.
        services.AddScoped<ITenantPostgreSqlScopeProbe, TenantPostgreSqlScopeProbe>();
        services.AddScoped<IPlatformDataBoundaryProvisioner, PlatformDataBoundaryProvisioner>();

        // The on-demand path into the same automation, for a tenant that was provisioned before
        // this deployment declared its estate.
        services.AddScoped<IPlatformDataBoundaryApplier, PlatformDataBoundaryApplier>();
        return services;
    }
}
