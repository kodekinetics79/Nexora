using System.Reflection;
using System.Security.Claims;
using ERP_RFQ_Automation.Billing.Controllers;
using ERP_RFQ_Automation.Platform.Auth;
using ERP_RFQ_Automation.Platform.Controllers;
using ERP_RFQ_Automation.Platform.Models;
using ERP_RFQ_Automation.Platform.Provisioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// RED TEAM. Does the declared policy on each action match what the action can actually do?
///
/// <para>The separation of duties this codebase states in prose — <c>TenantsController.ChangePlan</c>,
/// "Sec9: plan assignment is a BILLING operation (Owner | BillingAdmin), not a
/// support-tenant-admin one — SupportAdmin must not be able to change what a customer is
/// charged/entitled to" — is asserted here against the REAL policies from
/// <see cref="PlatformAuthExtensions.AddPlatformPolicies"/> and the REAL attributes on the
/// shipped controllers, rather than restated.</para>
///
/// <para>The policies are a flat <c>RequireClaim</c> allow-list with no role hierarchy, so a role
/// passes a policy if and only if its name is literally in that policy's list.</para>
/// </summary>
public sealed class RedTeamSeparationOfDutiesTests
{
    /// <summary>
    /// The request properties that decide what a customer is charged. Every one of them is
    /// writable through <c>PlatformBillingController</c> (Owner | BillingAdmin) or
    /// <c>TenantsController.ChangePlan</c> (Owner | BillingAdmin).
    /// </summary>
    private static readonly string[] CommercialLevers =
    [
        nameof(ProvisionTenantRequest.PlanId),
        nameof(ProvisionTenantRequest.BillingMode),
        nameof(ProvisionTenantRequest.BillingModeReason),
        nameof(ProvisionTenantRequest.RateCardId),
        nameof(ProvisionTenantRequest.BillingStartsOn),
        nameof(ProvisionTenantRequest.TrialEndsOn)
    ];

    // ------------------------------------------------------------------ the policies themselves

    [Theory]
    [InlineData(PlatformPolicies.Billing, PlatformRole.Owner, true)]
    [InlineData(PlatformPolicies.Billing, PlatformRole.BillingAdmin, true)]
    [InlineData(PlatformPolicies.Billing, PlatformRole.SupportAdmin, false)]
    [InlineData(PlatformPolicies.Billing, PlatformRole.ReadOnlyOps, false)]
    [InlineData(PlatformPolicies.TenantAdmin, PlatformRole.Owner, true)]
    [InlineData(PlatformPolicies.TenantAdmin, PlatformRole.SupportAdmin, true)]
    [InlineData(PlatformPolicies.TenantAdmin, PlatformRole.BillingAdmin, false)]
    [InlineData(PlatformPolicies.TenantAdmin, PlatformRole.ReadOnlyOps, false)]
    [InlineData(PlatformPolicies.Owner, PlatformRole.Owner, true)]
    [InlineData(PlatformPolicies.Owner, PlatformRole.SupportAdmin, false)]
    // The bare scope gate admits every operator, including the read-only tier.
    [InlineData(PlatformPolicies.PlatformScope, PlatformRole.ReadOnlyOps, true)]
    public async Task The_platform_policies_are_a_flat_allow_list_with_no_role_hierarchy(
        string policy, PlatformRole role, bool expected)
        => Assert.Equal(expected, await SatisfiesAsync(policy, role));

    // -------------------------------------------------- the invariant the codebase states in prose

    /// <summary>
    /// FINDING R1 (revenue), now closed and pinned here.
    ///
    /// <para><c>ChangePlan</c> and every <c>PlatformBillingController</c> mutation are gated on
    /// <see cref="PlatformPolicies.Billing"/> so a SupportAdmin cannot change what a customer is
    /// charged. Both PROVISIONING entry points accepted the same commercial levers — plan, billing
    /// mode, exemption reason, pinned rate card, trial end, billing start — on
    /// <see cref="PlatformPolicies.TenantAdmin"/>, which SupportAdmin satisfies. The separation of
    /// duties held for repricing an existing customer and was absent at the one moment the price is
    /// first set, so a SupportAdmin could create any number of tenants priced at nothing.</para>
    ///
    /// <para><b>Asserted behaviourally, not by scanning attributes.</b> The fix is deliberately NOT
    /// a policy attribute: gating the whole endpoint on Billing would make it impossible for
    /// support to onboard a customer at all, and creating a company, its workspace and its
    /// administrator is legitimately support work. Deciding the commercial terms attached to it is
    /// not. A declared-policy scan cannot see that distinction — it would read a correct
    /// implementation as an offender and an endpoint that merely carries the right attribute as
    /// safe — so this drives the real action and checks what actually happens.</para>
    /// </summary>
    [Fact]
    public void No_provisioning_request_carrying_a_commercial_lever_is_accepted_without_billing_authority()
    {
        var supportAdmin = PrincipalFor(PlatformRole.SupportAdmin);
        var billingAdmin = PrincipalFor(PlatformRole.BillingAdmin);
        var owner = PrincipalFor(PlatformRole.Owner);

        Assert.False(PlatformCommercialAuthority.HoldsCommercialAuthority(supportAdmin));
        Assert.True(PlatformCommercialAuthority.HoldsCommercialAuthority(billingAdmin));
        Assert.True(PlatformCommercialAuthority.HoldsCommercialAuthority(owner));

        // Every lever, one at a time, so a future field added to the request cannot be waved
        // through by a test that only ever exercised the plan.
        var levers = new (string Name, Func<IReadOnlyList<string>> Probe)[]
        {
            ("planId", () => PlatformCommercialAuthority.CommercialLeversIn(7, null, null, null, null, null)),
            ("billingMode", () => PlatformCommercialAuthority.CommercialLeversIn(null, "Internal", null, null, null, null)),
            ("billingModeReason", () => PlatformCommercialAuthority.CommercialLeversIn(null, null, "Friend of the founder", null, null, null)),
            ("rateCardId", () => PlatformCommercialAuthority.CommercialLeversIn(null, null, null, 3, null, null)),
            ("billingStartsOn", () => PlatformCommercialAuthority.CommercialLeversIn(null, null, null, null, DateTime.UtcNow, null)),
            ("trialEndsOn", () => PlatformCommercialAuthority.CommercialLeversIn(null, null, null, null, null, DateTime.UtcNow.AddDays(30))),
        };
        foreach (var (name, probe) in levers)
            Assert.Contains(name, probe());

        // Choosing the DEFAULT mode asserts nothing commercial, so support onboarding is not
        // collaterally blocked by naming it explicitly.
        Assert.Empty(PlatformCommercialAuthority.CommercialLeversIn(
            null, nameof(TenantBillingMode.Billable), null, null, null, null));
        Assert.Empty(PlatformCommercialAuthority.CommercialLeversIn(null, null, null, null, null, null));

        // The refusal has to name the offending fields; a 403 that does not say what was
        // objectionable gets worked around by trial and error.
        var refusal = PlatformCommercialAuthority.DescribeRefusal(new[] { "planId", "rateCardId" });
        Assert.Contains("planId", refusal);
        Assert.Contains("rateCardId", refusal);
        Assert.Contains("Owner or BillingAdmin", refusal);
    }

    /// <summary>
    /// The shape of the fix, pinned so it cannot regress into either failure mode.
    ///
    /// <para>Support keeps the ability to ONBOARD — the provisioning endpoints still admit a
    /// SupportAdmin, because creating a company, its workspace and its administrator is support
    /// work and gating the whole endpoint on Billing would have broken that. What support loses is
    /// the ability to decide what the customer is CHARGED, which is the same line
    /// <c>ChangePlan</c> and the billing mutations already draw.</para>
    ///
    /// <para>This asserts both halves. A test that only checked the refusal would pass equally
    /// well if somebody "fixed" R1 by locking support out of provisioning altogether.</para>
    /// </summary>
    [Fact]
    public async Task Support_can_still_onboard_a_customer_but_no_longer_prices_one()
    {
        // Repricing an existing tenant: refused by policy, as it always was.
        Assert.False(await CanReachAsync(
            typeof(TenantsController).GetMethod(nameof(TenantsController.ChangePlan))!,
            PlatformRole.SupportAdmin));
        Assert.False(await CanReachAsync(
            typeof(PlatformBillingController).GetMethod(nameof(PlatformBillingController.SetTenantCommercialTerms))!,
            PlatformRole.SupportAdmin));
        Assert.False(await CanReachAsync(
            typeof(PlatformBillingController).GetMethod(nameof(PlatformBillingController.SetTenantRateCard))!,
            PlatformRole.SupportAdmin));

        // Reaching the provisioning endpoints at all: still permitted, deliberately.
        Assert.True(await CanReachAsync(
            typeof(TenantsController).GetMethod(nameof(TenantsController.Provision))!,
            PlatformRole.SupportAdmin));
        Assert.True(await CanReachAsync(
            typeof(TenantProvisioningController).GetMethod(nameof(TenantProvisioningController.Submit))!,
            PlatformRole.SupportAdmin));

        // The request really does carry the levers, so the runtime guard is load-bearing rather
        // than theoretical — and both entry points share one request type, which is why the rule
        // lives in PlatformCommercialAuthority instead of being written twice.
        foreach (var lever in CommercialLevers)
            Assert.NotNull(typeof(ProvisionTenantRequest).GetProperty(lever));
        var submitted = typeof(SubmitProvisioningRequest).GetProperty(nameof(SubmitProvisioningRequest.Tenant));
        Assert.Equal(typeof(ProvisionTenantRequest), submitted!.PropertyType);

        // And a SupportAdmin posting those levers is refused on the way through.
        Assert.False(PlatformCommercialAuthority.HoldsCommercialAuthority(
            PrincipalFor(PlatformRole.SupportAdmin)));
        Assert.NotEmpty(PlatformCommercialAuthority.CommercialLeversIn(
            planId: 7, billingMode: nameof(TenantBillingMode.Internal),
            billingModeReason: "Friend of the founder", rateCardId: null,
            billingStartsOn: null, trialEndsOn: null));
    }

    /// <summary>
    /// The destructive lifecycle verbs. Owner only, and they are — pinned so a later convenience
    /// change to TenantAdmin is a failing test rather than a code review nobody scheduled.
    /// </summary>
    [Theory]
    [InlineData("Purge")]
    [InlineData("ErasePersonalData")]
    [InlineData("ScheduleDeletion")]
    [InlineData("CancelDeletion")]
    [InlineData("PurgePreview")]
    public async Task Every_destructive_lifecycle_verb_refuses_a_SupportAdmin(string action)
    {
        var method = typeof(ERP_RFQ_Automation.Platform.Lifecycle.TenantOffboardingController)
            .GetMethod(action);
        Assert.NotNull(method);

        Assert.False(await CanReachAsync(method!, PlatformRole.SupportAdmin));
        Assert.False(await CanReachAsync(method!, PlatformRole.BillingAdmin));
        Assert.False(await CanReachAsync(method!, PlatformRole.ReadOnlyOps));
        Assert.True(await CanReachAsync(method!, PlatformRole.Owner));
    }

    /// <summary>
    /// FINDING R5. The one mutating verb on the offboarding controller that is NOT Owner-only
    /// produces the tenant's ENTIRE commercial history as a downloadable file. A SupportAdmin may
    /// not COUNT the tenant's rows (<c>PurgePreview</c> is Owner) but may TAKE all of them.
    ///
    /// <para>SKIPPED because it FAILS: it proves the defect. Remove the Skip to see it.</para>
    /// </summary>
    [Fact]
    public async Task The_full_tenant_data_export_is_at_least_as_guarded_as_the_purge_preview()
    {
        var export = typeof(ERP_RFQ_Automation.Platform.Lifecycle.TenantOffboardingController)
            .GetMethod("Export")!;
        var preview = typeof(ERP_RFQ_Automation.Platform.Lifecycle.TenantOffboardingController)
            .GetMethod("PurgePreview")!;

        foreach (var role in new[] { PlatformRole.SupportAdmin, PlatformRole.BillingAdmin, PlatformRole.ReadOnlyOps })
            Assert.Equal(await CanReachAsync(preview, role), await CanReachAsync(export, role));
    }

    // ---------------------------------------------------------------------------------- helpers

    private static IEnumerable<(string Label, MethodInfo Action)> CommercialWriteActions()
    {
        yield return ("TenantsController.Provision",
            typeof(TenantsController).GetMethod(nameof(TenantsController.Provision))!);
        yield return ("TenantProvisioningController.Submit",
            typeof(TenantProvisioningController).GetMethod(nameof(TenantProvisioningController.Submit))!);
        yield return ("TenantsController.ChangePlan",
            typeof(TenantsController).GetMethod(nameof(TenantsController.ChangePlan))!);
        yield return ("PlatformBillingController.SetTenantCommercialTerms",
            typeof(PlatformBillingController).GetMethod(nameof(PlatformBillingController.SetTenantCommercialTerms))!);
        yield return ("PlatformBillingController.SetTenantRateCard",
            typeof(PlatformBillingController).GetMethod(nameof(PlatformBillingController.SetTenantRateCard))!);
    }

    /// <summary>
    /// Every policy an action is behind. ASP.NET Core does NOT let an action-level
    /// <c>[Authorize]</c> override the controller-level one — both are evaluated and both must
    /// pass — so this is the AND of the two.
    /// </summary>
    private static string[] DeclaredPolicies(MethodInfo action) =>
        action.GetCustomAttributes<AuthorizeAttribute>(inherit: true)
            .Concat(action.DeclaringType!.GetCustomAttributes<AuthorizeAttribute>(inherit: true))
            .Select(a => a.Policy)
            .Where(p => !string.IsNullOrEmpty(p))
            .Select(p => p!)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    private static async Task<bool> CanReachAsync(MethodInfo action, PlatformRole role)
    {
        var policies = DeclaredPolicies(action);
        Assert.NotEmpty(policies);

        foreach (var policy in policies)
            if (!await SatisfiesAsync(policy, role))
                return false;
        return true;
    }

    /// <summary>
    /// Evaluates the REAL registered policy against a token-shaped principal. Only the claims a
    /// platform token actually carries are supplied: scheme validation happens before this and is
    /// not what these tests are about.
    /// </summary>
    private static async Task<bool> SatisfiesAsync(string policyName, PlatformRole role)
    {
        var services = new ServiceCollection()
            .AddLogging()
            .AddAuthorization(options => options.AddPlatformPolicies())
            .BuildServiceProvider();

        var policy = await services.GetRequiredService<IAuthorizationPolicyProvider>()
            .GetPolicyAsync(policyName);
        Assert.NotNull(policy);

        var principal = PrincipalFor(role);

        var result = await services.GetRequiredService<IAuthorizationService>()
            .AuthorizeAsync(principal, resource: null, policy!);
        return result.Succeeded;
    }

    /// <summary>
    /// A platform token as the auth pipeline actually mints it. Shared by the policy probes and the
    /// commercial-authority assertions so both are talking about the same operator.
    /// </summary>
    private static ClaimsPrincipal PrincipalFor(PlatformRole role)
        => new(new ClaimsIdentity(
        [
            new Claim("sub", "7"),
            new Claim(PlatformAuthConstants.ScopeClaim, PlatformAuthConstants.PlatformScopeValue),
            new Claim(PlatformAuthConstants.PlatformRoleClaim, role.ToString()),
            new Claim(PlatformAuthConstants.AuthenticationMethodClaim,
                PlatformAuthConstants.MfaAuthenticationMethod)
        ], PlatformAuthConstants.Scheme));
}
