using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Notifications;
using ERP_RFQ_Automation.Notifications.Providers;
using ERP_RFQ_Automation.Platform.Onboarding;
using ERP_RFQ_Automation.Platform.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ERP_RFQ_Automation.Tests.Support;

/// <summary>
/// The collaborators <c>TenantsController.Provision</c> requires, built over a test context.
///
/// <para>Both are REQUIRED constructor dependencies rather than optional ones, because a
/// provision that quietly skips either produces exactly the defects the endpoint exists to
/// prevent — an empty workspace, or a founding administrator whose credential the operator
/// holds. Centralising their construction here keeps that requirement from being re-litigated
/// in every fixture.</para>
/// </summary>
public static class ProvisioningFixture
{
    public static ITenantBaselineSeeder Baseline(ErpRfqAutomationContext context) =>
        new TenantBaselineSeeder(context, NullLogger<TenantBaselineSeeder>.Instance);

    /// <summary>
    /// Wired to the CONSOLE email sender, which logs instead of sending — the same provider a
    /// pilot deployment defaults to. Invitations are therefore issued and hashed for real while
    /// no message leaves the machine, so a test can assert the activation contract without
    /// asserting anything about delivery.
    /// </summary>
    public static ITenantAdminInvitationService Invitations(ErpRfqAutomationContext context) =>
        new TenantAdminInvitationService(
            context,
            new ConsoleEmailSender(NullLogger<ConsoleEmailSender>.Instance),
            Options.Create(new NotificationsOptions { AppBaseUrl = "https://app.nexora.test" }),
            Options.Create(new TenantOnboardingOptions()),
            NullLogger<TenantAdminInvitationService>.Instance);
}
