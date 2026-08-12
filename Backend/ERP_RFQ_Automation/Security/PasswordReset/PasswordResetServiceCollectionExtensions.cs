namespace ERP_RFQ_Automation.Security.PasswordReset;

/// <summary>
/// Single entry point for self-service password recovery. One call from Program.cs, matching the
/// shape <c>AddTenantOnboarding</c> / <c>AddLoginThrottling</c> use.
///
/// <para>No options binding of its own: the two knobs this module needs
/// (<c>TenantOnboarding:ResetLifetimeMinutes</c> and <c>TenantOnboarding:ResetPasswordPath</c>)
/// live beside <c>ActivationPath</c> in <c>TenantOnboardingOptions</c>, because the reset flow
/// already reuses that section's password floor and a second section would let the two credential
/// paths drift apart on the one rule they must share. <c>AddTenantOnboarding</c> binds and
/// validates it; this call must therefore come after it.</para>
/// </summary>
public static class PasswordResetServiceCollectionExtensions
{
    public static IServiceCollection AddTenantPasswordReset(this IServiceCollection services)
    {
        // Scoped, like the invitation service and for the same reason: it writes through the
        // request-scoped ErpRfqAutomationContext, which is what lets the supersede-then-mint pair
        // run inside one transaction on the caller's connection.
        services.AddScoped<IPasswordResetService, PasswordResetService>();
        return services;
    }
}
