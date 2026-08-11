namespace ERP_RFQ_Automation.Platform.Activation;

public static class TenantActivationServiceCollectionExtensions
{
    public static IServiceCollection AddTenantActivationPolicy(this IServiceCollection services)
    {
        services.AddScoped<ITenantActivationPolicyService, TenantActivationPolicyService>();

        // Singleton: the scanner selection is computed from configuration that does not change
        // within a process lifetime, and it is exactly what Program.cs already resolved at startup.
        services.AddSingleton<IPlatformDeploymentPosture, PlatformDeploymentPosture>();
        return services;
    }
}
