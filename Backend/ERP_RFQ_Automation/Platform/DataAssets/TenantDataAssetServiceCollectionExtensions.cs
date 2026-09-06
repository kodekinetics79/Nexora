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
        // The configuration half stays a singleton — it is process configuration read once. What
        // the rest of the system resolves through is the SCOPED overlay below, which puts the
        // console's own answer in front of it. Registering the configuration manifest by its
        // concrete type keeps that layering explicit: nothing can accidentally take a dependency
        // on the configuration-only view and miss what an operator recorded.
        services.AddSingleton<PlatformDataBoundaryManifest>();
        services.AddScoped<IPlatformDataBoundaryManifest, ResolvedPlatformDataBoundaryManifest>();

        // The platform reading its own address, so an operator never has to know a Neon endpoint id.
        services.AddScoped<IDatabaseSelfObserver, DatabaseSelfObserver>();

        // Stateless; scoped only to sit alongside the context it is handed.
        services.AddScoped<ITenantPostgreSqlScopeProbe, TenantPostgreSqlScopeProbe>();
        services.AddScoped<IPlatformDataBoundaryProvisioner, PlatformDataBoundaryProvisioner>();

        // The on-demand path into the same automation, for a tenant that was provisioned before
        // this deployment declared its estate.
        services.AddScoped<IPlatformDataBoundaryApplier, PlatformDataBoundaryApplier>();
        return services;
    }
}
