namespace ERP_RFQ_Automation.Platform.DataAssets;

public static class TenantDataAssetServiceCollectionExtensions
{
    public static IServiceCollection AddTenantDataAssetRegistry(this IServiceCollection services)
    {
        services.AddScoped<TenantDataAssetRegistryService>();
        services.AddScoped<TenantDataRecoveryService>();
        return services;
    }
}
