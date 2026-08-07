namespace ERP_RFQ_Automation.Platform.Onboarding;

/// <summary>
/// Single entry point for the founding-administrator activation module. One call from
/// Program.cs, matching the shape <c>AddNotifications</c> / <c>AddLoginThrottling</c> use.
/// </summary>
public static class TenantOnboardingServiceCollectionExtensions
{
    public static IServiceCollection AddTenantOnboarding(
        this IServiceCollection services, IConfiguration configuration)
    {
        var section = configuration.GetSection(TenantOnboardingOptions.SectionName);
        services.Configure<TenantOnboardingOptions>(section);

        // Warn once at startup rather than at the first provision. A lifetime typo discovered
        // when a customer cannot activate is discovered in front of the customer.
        var options = new TenantOnboardingOptions();
        section.Bind(options);
        using (var provider = services.BuildServiceProvider())
        {
            var logger = provider.GetService<ILoggerFactory>()?.CreateLogger("TenantOnboarding");
            foreach (var warning in options.Validate())
                logger?.LogWarning("[TenantOnboarding] {Warning}", warning);
        }

        // Scoped: it writes through the request-scoped ErpRfqAutomationContext, which is what
        // lets IssueAsync join the provisioning transaction instead of racing beside it.
        services.AddScoped<ITenantAdminInvitationService, TenantAdminInvitationService>();
        return services;
    }
}
