using System.Security.Claims;
using ERP_RFQ_Automation.Platform.Activation;
using ERP_RFQ_Automation.Platform.Auth;
using ERP_RFQ_Automation.Platform.Models;
using ERP_RFQ_Automation.Platform.Provisioning;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// Choosing what a workspace is FOR at the moment it is created, and the authority that decision
/// carries.
///
/// <para><b>The defect.</b> Every tenant was born PRODUCTION — every activation control a hard
/// gate, nothing deferrable. So an internal demo or a throwaway test workspace was created into
/// the strictest profile the product has, and then had to be walked back through a separate
/// Owner-only endpoint on a screen the operator had not opened. The observable result was a test
/// tenant stuck in Provisioning behind controls about a customer that does not exist: somebody
/// else's storage estate, somebody else's identity provider, a tax authority.</para>
///
/// <para><b>What must not have been traded for it.</b> Provisioning is a
/// <c>Platform.TenantAdmin</c> endpoint and the profile endpoint is <c>Platform.Owner</c>, for a
/// reason written out on that endpoint: the profile is the one lever that switches a tenant on
/// with production prerequisites outstanding. Accepting the field without re-imposing that rule
/// would hand a SupportAdmin, at creation time, exactly the relaxation they are refused forever
/// after. Every test below pins one half of that: the convenience works, and the authority did not
/// move.</para>
/// </summary>
public sealed class TenantProfileAtProvisioningTests
{
    private static ClaimsPrincipal Actor(string role) =>
        new(new ClaimsIdentity([new Claim(PlatformAuthConstants.PlatformRoleClaim, role)], "test"));

    [Fact]
    public void Saying_nothing_is_production()
    {
        var profile = PlatformDeploymentProfileAuthority.Validate(
            null, null, Actor(nameof(PlatformRole.SupportAdmin)), out var refusal, out var forbidden);

        Assert.Equal(TenantDeploymentProfile.Production, profile);
        Assert.Null(refusal);
        Assert.False(forbidden);
    }

    /// <summary>
    /// Asking for the default explicitly asserts nothing and relaxes nothing, so it must not
    /// require an Owner or a reason. A console that sends the field on every submit is not thereby
    /// asking for a privilege.
    /// </summary>
    [Fact]
    public void Asking_for_production_explicitly_needs_no_authority_and_no_reason()
    {
        PlatformDeploymentProfileAuthority.Validate(
            "PRODUCTION", null, Actor(nameof(PlatformRole.SupportAdmin)), out var refusal, out _);

        Assert.Null(refusal);
    }

    [Fact]
    public void A_support_admin_cannot_create_a_tenant_that_defers_production_prerequisites()
    {
        PlatformDeploymentProfileAuthority.Validate(
            "DEMO", "Sales demonstration workspace for the Riyadh pitch",
            Actor(nameof(PlatformRole.SupportAdmin)), out var refusal, out var forbidden);

        Assert.True(forbidden);
        Assert.NotNull(refusal);
        Assert.Contains("Owner", refusal);
    }

    /// <summary>
    /// An Owner may choose it and still may not skip the reason: the reason IS the approval, and
    /// <c>DeploymentProfilePolicy.IsApproved</c> refuses to defer anything without one. Refused as
    /// evidence rather than as authority, so the operator is not sent hunting for a permission they
    /// already hold.
    /// </summary>
    [Fact]
    public void An_owner_without_a_reason_is_refused_for_the_reason_not_the_authority()
    {
        PlatformDeploymentProfileAuthority.Validate(
            "DEMO", "too short", Actor(nameof(PlatformRole.Owner)), out var refusal, out var forbidden);

        Assert.False(forbidden);
        Assert.NotNull(refusal);
        Assert.Contains("15 characters", refusal);
    }

    [Fact]
    public void An_unrecognised_profile_is_refused_rather_than_defaulted()
    {
        var profile = PlatformDeploymentProfileAuthority.Validate(
            "SANDBOX", "A perfectly good reason, over fifteen characters",
            Actor(nameof(PlatformRole.Owner)), out var refusal, out _);

        Assert.NotNull(refusal);
        Assert.Equal(TenantDeploymentProfile.Production, profile);
    }

    /// <summary>
    /// The whole point, end to end through the real provisioning engine: a demo workspace is born
    /// already deferring, with the approval recorded on it — approver, instant and reason, because
    /// any one of them alone is satisfiable by accident and the policy demands all three.
    /// </summary>
    [Fact]
    public async Task A_demo_tenant_is_born_approved_and_deferring()
    {
        using var harness = new ProvisioningHarness();
        var planId = await ReadyPlanAsync(harness);

        var execution = await harness.ProvisionAsync(ProvisioningHarness.Request(
            "northwind-demo", "demo@northwind.test", planId,
            activation: AdminActivationMethods.Password, password: "Correct-Horse-9!",
            deploymentProfile: "DEMO",
            deploymentProfileReason: "Sales demonstration workspace, no customer data"));

        Assert.Equal(ProvisioningExecutionState.Succeeded, execution.State);

        await using var db = harness.Context();
        var tenant = await db.Set<Tenant>().IgnoreQueryFilters()
            .SingleAsync(x => x.Id == execution.TenantId!.Value);

        Assert.Equal(TenantDeploymentProfile.Demo, tenant.DeploymentProfile);
        Assert.Equal("owner@nexora.app", tenant.DeploymentProfileApprovedBy);
        Assert.NotNull(tenant.DeploymentProfileApprovedOn);
        Assert.Equal("Sales demonstration workspace, no customer data", tenant.DeploymentProfileReason);
        Assert.True(DeploymentProfilePolicy.PermitsDeferral(tenant));
    }

    /// <summary>
    /// The default path is untouched: a tenant nobody said anything about is PRODUCTION, defers
    /// nothing, and carries no approval anybody could mistake for one.
    /// </summary>
    [Fact]
    public async Task A_tenant_created_without_a_profile_defers_nothing()
    {
        using var harness = new ProvisioningHarness();
        var planId = await ReadyPlanAsync(harness);

        var execution = await harness.ProvisionAsync(ProvisioningHarness.Request(
            "northwind-plain", "plain@northwind.test", planId,
            activation: AdminActivationMethods.Password, password: "Correct-Horse-9!"));

        await using var db = harness.Context();
        var tenant = await db.Set<Tenant>().IgnoreQueryFilters()
            .SingleAsync(x => x.Id == execution.TenantId!.Value);

        Assert.Equal(TenantDeploymentProfile.Production, tenant.DeploymentProfile);
        Assert.Null(tenant.DeploymentProfileApprovedBy);
        Assert.Null(tenant.DeploymentProfileReason);
        Assert.False(DeploymentProfilePolicy.PermitsDeferral(tenant));
    }

    /// <summary>
    /// Two submissions differing only by profile are two different requests. An idempotency key
    /// that could not tell them apart would replay the PRODUCTION execution to an operator who
    /// asked for a demo, and hand them a tenant held to gates they deliberately opted out of.
    /// </summary>
    [Fact]
    public void The_profile_is_part_of_the_request_fingerprint()
    {
        var production = ProvisioningHarness.Request("northwind-fp", "fp@northwind.test", 1);
        var demo = ProvisioningHarness.Request("northwind-fp", "fp@northwind.test", 1,
            deploymentProfile: "DEMO", deploymentProfileReason: "Demonstration workspace only");

        Assert.NotEqual(
            ProvisioningRequestCanonicalizer.Fingerprint(production),
            ProvisioningRequestCanonicalizer.Fingerprint(demo));
    }

    private static async Task<long> ReadyPlanAsync(ProvisioningHarness harness)
    {
        await using var db = harness.Context();
        var plan = new Plan
        {
            Code = $"enterprise-{Guid.NewGuid():N}", Name = "Enterprise", IsActive = true,
            MaxSeats = 25, MaxDocsPerMonth = 5000, MaxConcurrentExtractionJobs = 4, Weight = 3,
            MonthlyPriceUsd = 999m
        };
        db.Set<Plan>().Add(plan);
        await db.SaveChangesAsync();
        return plan.Id;
    }
}
