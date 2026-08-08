namespace ERP_RFQ_Automation.Platform.DataAssets;

public static class TenantDataAssetServiceCollectionExtensions
{
    public static IServiceCollection AddTenantDataAssetRegistry(this IServiceCollection services)
    {
        services.AddScoped<TenantDataAssetRegistryService>();
        return services;
    }
}
