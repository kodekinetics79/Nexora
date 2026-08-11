using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Platform.Services;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Platform.Auth;

/// <summary>
/// Single entry point for server-authoritative platform MFA enforcement. One call from Program.cs,
/// matching the shape <c>AddPlatformEmailSettings</c> / <c>AddTenantOnboarding</c> use.
///
/// <para>Must be registered BEFORE the platform auth service resolves, because
/// <c>PlatformAuthService</c> takes the policy provider and the browser-trust service as optional
/// dependencies and treats their absence as "enforce".</para>
/// </summary>
public static class PlatformMfaPolicyServiceCollectionExtensions
{
    public static IServiceCollection AddPlatformMfaPolicy(
        this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        // Reading (and validating) the configuration HERE, at registration time, is what makes an
        // invalid production policy a failed startup rather than a runtime surprise: the throw
        // happens on the Program.cs line, before the host is built, in the same style as
        // SecretProtection.CreateFromConfiguration and UnifiedDocumentIngestionGuard.Enforce.
        var options = PlatformMfaPolicyOptions.CreateFromConfiguration(configuration, environment);
        services.AddSingleton(options);

        // Scoped, because both read through the request-scoped DbContext. They take DbContext rather
        // than the concrete context for the same reason the email settings services do: they touch
        // two self-contained platform tables, and the looser dependency is what lets the tests
        // exercise the real projections against a model built from ApplyPlatformMfaPolicyModel alone.
        services.AddScoped<IPlatformMfaPolicyService>(sp =>
            new PlatformMfaPolicyService(
                sp.GetRequiredService<ErpRfqAutomationContext>(),
                options,
                sp.GetRequiredService<IPlatformAuditService>(),
                sp.GetRequiredService<ILogger<PlatformMfaPolicyService>>()));

        // The authorization layer resolves the PROVIDER, not the service: the policy pipeline must be
        // able to ask whether a second factor is enforced without acquiring the ability to change the
        // answer. Forwarded to the same scoped instance so one request reads one consistent policy.
        services.AddScoped<IPlatformMfaPolicyProvider>(sp => sp.GetRequiredService<IPlatformMfaPolicyService>());

        services.AddScoped<IPlatformBrowserTrustService>(sp =>
            new PlatformBrowserTrustService(
                sp.GetRequiredService<ErpRfqAutomationContext>(),
                options,
                sp.GetRequiredService<IPlatformAuditService>(),
                sp.GetRequiredService<ILogger<PlatformBrowserTrustService>>()));

        // The second half of fail-fast: configuration can be valid while the STORED policy is not —
        // a production database restored from a staging snapshot is the ordinary way that happens.
        services.AddHostedService<PlatformMfaPolicyStartupGuard>();

        return services;
    }
}

/// <summary>
/// Refuses to let a production host finish starting while the persisted policy says MFA is not
/// enforced.
///
/// <para><b>Why a hosted service and not a background service.</b> <c>HostOptions</c> in this
/// application sets <c>BackgroundServiceExceptionBehavior.Ignore</c>, so a <c>BackgroundService</c>
/// that threw here would be swallowed and the API would serve traffic with enforcement off and a
/// stack trace in the log. An <see cref="IHostedService"/>'s <c>StartAsync</c> propagates and stops
/// the host, which is the required behaviour.</para>
///
/// <para><b>Why it is not the only guard.</b> The read path coerces a non-permitted stored mode back
/// to REQUIRED on every request, so even if this check were removed the platform would still enforce
/// MFA. This exists so the misconfiguration is discovered by the deploy, by the person who caused
/// it, instead of silently corrected forever afterwards.</para>
///
/// <para>Outside production it only reports, because a developer machine whose row says
/// DISABLED_TEST_ONLY is working as designed.</para>
/// </summary>
public sealed class PlatformMfaPolicyStartupGuard : IHostedService
{
    private readonly IServiceProvider _services;
    private readonly PlatformMfaPolicyOptions _options;
    private readonly ILogger<PlatformMfaPolicyStartupGuard> _log;

    public PlatformMfaPolicyStartupGuard(
        IServiceProvider services,
        PlatformMfaPolicyOptions options,
        ILogger<PlatformMfaPolicyStartupGuard> log)
    {
        _services = services;
        _options = options;
        _log = log;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        PlatformMfaPolicy? row;
        try
        {
            using var scope = _services.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<ErpRfqAutomationContext>();
            if (context.Model.FindEntityType(typeof(PlatformMfaPolicy)) is null) return;

            row = await context.Set<PlatformMfaPolicy>().AsNoTracking()
                .FirstOrDefaultAsync(policy => policy.Id == PlatformMfaPolicy.SingletonId, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // A table that cannot be read at boot — because the migration has not been applied yet,
            // typically — must not stop the boot. The effective policy is REQUIRED in that state, so
            // failing closed has already happened; taking the deployment down as well would turn a
            // fail-safe into an outage.
            _log.LogWarning(exception,
                "[PlatformMfa] Could not read the MFA enforcement policy at startup. Enforcement is REQUIRED " +
                "until it can be read.");
            return;
        }

        var declared = row is null
            ? PlatformMfaMode.REQUIRED
            : Enum.TryParse<PlatformMfaMode>(row.Mode, ignoreCase: true, out var parsed)
                ? parsed
                : PlatformMfaMode.REQUIRED;

        if (declared != PlatformMfaMode.REQUIRED && !_options.Permits(declared))
        {
            var message =
                $"The stored platform MFA policy is {declared}, which {_options.EnvironmentName} " +
                $"({_options.EnvironmentClass}) does not permit. This deployment will not start with MFA " +
                "enforcement relaxed. Set the policy back to REQUIRED " +
                $"(UPDATE platform.\"PlatformMfaPolicies\" SET \"Mode\" = 'REQUIRED' WHERE \"Id\" = " +
                $"{PlatformMfaPolicy.SingletonId}) or correct ASPNETCORE_ENVIRONMENT.";
            _log.LogCritical("[PlatformMfa] {Message}", message);
            throw new InvalidOperationException(message);
        }

        if (declared != PlatformMfaMode.REQUIRED)
            _log.LogWarning(
                "[PlatformMfa] MFA enforcement is {Mode} in {Environment} until {Expiry:O}, set by {Actor}. " +
                "Reason: {Reason}",
                declared, _options.EnvironmentName, row?.ExpiresAtUtc, row?.ChangedBy, row?.ChangeReason);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
