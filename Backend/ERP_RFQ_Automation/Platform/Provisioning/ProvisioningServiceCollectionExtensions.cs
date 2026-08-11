using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace ERP_RFQ_Automation.Platform.Provisioning;

/// <summary>
/// One-line Program.cs registration for durable tenant provisioning:
/// <c>builder.Services.AddDurableTenantProvisioning(builder.Configuration);</c>
///
/// <para>Registered AFTER <c>AddTenantOnboarding</c> and after the baseline seeder, because the
/// step executor composes both. The controller is discovered by the existing
/// <c>AddControllers()</c> scan, and the auth policies and <c>IPlatformAuditService</c> come from
/// the existing platform wiring.</para>
/// </summary>
public static class ProvisioningServiceCollectionExtensions
{
    /// <summary>
    /// Registers the engine AND the worker that runs it, deliberately as one call. A deployment
    /// that wired up the ability to ACCEPT provisioning work but not the thing that performs it
    /// would queue tenants forever while reporting every submit as a success — the same failure
    /// shape <c>AddPlatformBilling</c> removes the choice about, for the same reason.
    /// </summary>
    public static IServiceCollection AddDurableTenantProvisioning(
        this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<ProvisioningOptions>()
            .Bind(configuration.GetSection(ProvisioningOptions.SectionName))
            .ValidateOnStart();
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IValidateOptions<ProvisioningOptions>, ProvisioningOptionsValidator>());

        // Singleton: the signal is a process-local latency optimisation over a durable queue, so
        // it has no state worth scoping and losing it on restart costs one poll interval.
        services.TryAddSingleton<ProvisioningRunSignal>();

        // Scoped, over the request-scoped ErpRfqAutomationContext — which is what lets the step
        // executor's IssueAsync join the step's transaction instead of racing beside it.
        services.TryAddScoped<IProvisioningStepExecutor, ProvisioningStepExecutor>();
        services.TryAddScoped<ITenantProvisioningService, TenantProvisioningService>();
        services.TryAddScoped<IProvisioningDraftService, ProvisioningDraftService>();

        // Scoped for the same reason the service is: the recovery writes the ownership change and
        // its audit record on ONE context so they share a transaction. Registered here rather than
        // left to the caller because a deployment that can accept provisioning work but cannot
        // recover an execution abandoned by a dead node has no way out of a stuck tenant except a
        // hand-written UPDATE — which is the exact thing the database trigger now refuses.
        services.TryAddScoped<IProvisioningLeaseRecovery, ProvisioningLeaseRecovery>();

        // Stateless probes; scoped only to sit alongside the context they are handed.
        services.TryAddScoped<IProvisioningStepReconciler, ProvisioningStepReconciler>();

        // Read-only, and registered here rather than with the activation policy because it reads
        // the provisioning journal first and the policy second.
        services.TryAddScoped<IProvisioningDiagnosticsService, ProvisioningDiagnosticsService>();

        // The runner creates its OWN scopes per step and per journal write, so it must not be
        // scoped itself: it outlives any single request and is resolved by both the worker and
        // the synchronous compatibility path.
        services.TryAddSingleton<ITenantProvisioningRunner, TenantProvisioningRunner>();

        services.AddHostedService<ProvisioningRunWorker>();
        return services;
    }
}
