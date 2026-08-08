namespace ERP_RFQ_Automation.Platform.Activation;

public static class TenantActivationServiceCollectionExtensions
{
    public static IServiceCollection AddTenantActivationPolicy(this IServiceCollection services)
    {
        services.AddScoped<ITenantActivationPolicyService, TenantActivationPolicyService>();
        return services;
    }
}
