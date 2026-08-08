using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using ERP_RFQ_Automation.Billing.Accounting;
using ERP_RFQ_Automation.Billing.Metering;

namespace ERP_RFQ_Automation.Billing;

/// <summary>
/// One-line Program.cs registration for the SaaS billing plane (WS-C):
/// <c>builder.Services.AddPlatformBilling(builder.Configuration);</c>. The controller is
/// discovered by the existing AddControllers() scan; the auth policy
/// (PlatformPolicies.Billing) and PlatformAuditService are registered by the
/// existing platform wiring.
/// </summary>
public static class BillingServiceExtensions
{
    /// <summary>
    /// Registers the statement service AND the scheduled run. They are deliberately one
    /// call: a deployment that wired up the ability to compute statements but not the
    /// thing that computes them is precisely the state this platform shipped in, and the
    /// only way to make that state unreachable is to remove the choice.
    /// </summary>
    public static IServiceCollection AddPlatformBilling(
        this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddScoped<IBillingStatementService, BillingStatementService>();
        services.AddScoped<SubscriptionInvoiceService>();
        services.AddScoped<UsageMeteringService>();
        services.AddScoped<AccountingOutboxService>();
        services.AddOptions<BillingRunOptions>()
            .Bind(configuration.GetSection(BillingRunOptions.SectionName))
            .ValidateOnStart();
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IValidateOptions<BillingRunOptions>, BillingRunOptionsValidator>());
        services.AddHostedService<BillingRunWorker>();
        return services;
    }
}
