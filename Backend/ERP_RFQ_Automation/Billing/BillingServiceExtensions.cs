namespace ERP_RFQ_Automation.Billing;

/// <summary>
/// One-line Program.cs registration for the SaaS billing plane (WS-C):
/// <c>builder.Services.AddPlatformBilling();</c>. The controller is discovered
/// by the existing AddControllers() scan; the auth policy
/// (PlatformPolicies.Billing) and PlatformAuditService are registered by the
/// existing platform wiring.
/// </summary>
public static class BillingServiceExtensions
{
    public static IServiceCollection AddPlatformBilling(this IServiceCollection services)
    {
        services.AddScoped<IBillingStatementService, BillingStatementService>();
        return services;
    }
}
