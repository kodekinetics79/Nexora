using System.Reflection;
using System.Security.Claims;
using ERP_RFQ_Automation.Platform.Auth;
using ERP_RFQ_Automation.Platform.Models;
using ERP_RFQ_Automation.Platform.Support;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// Who may do what on the support desk and the audit explorer, proved two ways: by evaluating the
/// REAL platform policies (the ones <c>AddPlatformPolicies</c> registers, not a re-statement of
/// them) against principals for each tier, and by reflecting over the controllers so a future
/// endpoint cannot quietly ship without a gate.
/// </summary>
public sealed class PlatformSupportAuthorizationTests
{
    private static readonly Type[] SupportControllers =
    [
        typeof(PlatformSupportTicketsController),
        typeof(PlatformAuditExplorerController),
        typeof(TenantOperationsController)
    ];

    // ---- the real policies -------------------------------------------------

    [Theory]
    [InlineData(PlatformRole.Owner, true)]
    [InlineData(PlatformRole.SupportAdmin, true)]
    [InlineData(PlatformRole.BillingAdmin, false)]
    [InlineData(PlatformRole.ReadOnlyOps, false)]
    public async Task Only_owner_and_support_admin_satisfy_the_policy_that_gates_every_ticket_mutation(
        PlatformRole role, bool expected)
    {
        var authorization = BuildAuthorizationService();
        var result = await authorization.AuthorizeAsync(
            Principal(role), null, PlatformPolicies.TenantAdmin);

        Assert.Equal(expected, result.Succeeded);
    }

    [Theory]
    [InlineData(PlatformRole.Owner)]
    [InlineData(PlatformRole.SupportAdmin)]
    [InlineData(PlatformRole.BillingAdmin)]
    [InlineData(PlatformRole.ReadOnlyOps)]
    public async Task Every_tier_can_read_the_desk_and_the_audit_log(PlatformRole role)
    {
        // ReadOnlyOps is defined as cross-tenant read-only observability, and an observability role
        // that cannot read the audit log is not one. Every read endpoint in this module sits behind
        // PlatformScope alone, and none of them mutates.
        var authorization = BuildAuthorizationService();
        var result = await authorization.AuthorizeAsync(
            Principal(role), null, PlatformPolicies.PlatformScope);

        Assert.True(result.Succeeded);
    }

    [Fact]
    public async Task A_read_only_operator_is_refused_by_the_policy_on_every_mutating_endpoint()
    {
        var authorization = BuildAuthorizationService();
        var readOnly = Principal(PlatformRole.ReadOnlyOps);

        var mutations = MutatingActions(typeof(PlatformSupportTicketsController)).ToList();
        Assert.NotEmpty(mutations);

        foreach (var action in mutations)
        {
            var policy = Assert.Single(action.GetCustomAttributes<AuthorizeAttribute>()).Policy;
            var result = await authorization.AuthorizeAsync(readOnly, null, policy!);
            Assert.False(result.Succeeded,
                $"{action.Name} is reachable by ReadOnlyOps through policy {policy}.");
        }
    }

    [Fact]
    public async Task Support_ticketing_does_not_hand_support_admin_a_billing_capability()
    {
        // The separation of duties recorded at TenantsController.ChangePlan ("Sec9: plan assignment
        // is a BILLING operation ... SupportAdmin must not be able to change what a customer is
        // charged") has to survive a module that makes SupportAdmin genuinely powerful. It does,
        // because nothing in this module is gated on — or reads anything gated on — Platform.Billing.
        var authorization = BuildAuthorizationService();
        Assert.False((await authorization.AuthorizeAsync(
            Principal(PlatformRole.SupportAdmin), null, PlatformPolicies.Billing)).Succeeded);

        var policies = SupportControllers
            .SelectMany(controller => controller
                .GetCustomAttributes<AuthorizeAttribute>()
                .Concat(controller.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                    .SelectMany(m => m.GetCustomAttributes<AuthorizeAttribute>())))
            .Select(a => a.Policy)
            .Distinct()
            .ToArray();

        Assert.DoesNotContain(PlatformPolicies.Billing, policies);
        Assert.All(policies, policy => Assert.Contains(policy,
            new[] { PlatformPolicies.PlatformScope, PlatformPolicies.TenantAdmin }));
    }

    [Fact]
    public async Task A_tenant_token_never_satisfies_any_policy_this_module_uses()
    {
        // An impersonation token is a TENANT token: right scheme name is not enough, the scope claim
        // is the gate. This is what keeps the whole operator plane out of reach of a token minted
        // for a customer's own session.
        var authorization = BuildAuthorizationService();
        var tenantPrincipal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim("sub", "42"),
            new Claim(PlatformAuthConstants.ScopeClaim, PlatformAuthConstants.TenantScopeValue),
            new Claim(PlatformAuthConstants.ImpersonatedClaim, "true")
        ], PlatformAuthConstants.Scheme));

        foreach (var policy in new[] { PlatformPolicies.PlatformScope, PlatformPolicies.TenantAdmin })
            Assert.False((await authorization.AuthorizeAsync(tenantPrincipal, null, policy)).Succeeded);
    }

    // ---- the controllers ---------------------------------------------------

    [Fact]
    public void Every_endpoint_in_the_module_sits_behind_the_default_deny_platform_gate()
    {
        foreach (var controller in SupportControllers)
        {
            var classPolicy = Assert.Single(controller.GetCustomAttributes<AuthorizeAttribute>()).Policy;
            Assert.Equal(PlatformPolicies.PlatformScope, classPolicy);

            foreach (var action in Actions(controller))
            {
                Assert.Empty(action.GetCustomAttributes<AllowAnonymousAttribute>());
                var overriding = action.GetCustomAttributes<AuthorizeAttribute>().ToList();
                Assert.True(overriding.Count == 0 || overriding.All(a => a.Policy == PlatformPolicies.TenantAdmin),
                    $"{controller.Name}.{action.Name} weakens the class gate.");
            }
        }
    }

    [Fact]
    public void Every_ticket_mutation_requires_the_tenant_admin_policy()
    {
        var mutations = MutatingActions(typeof(PlatformSupportTicketsController)).ToList();

        // Create, note, transition, assign, severity, link, unlink.
        Assert.Equal(7, mutations.Count);
        foreach (var action in mutations)
            Assert.Equal(PlatformPolicies.TenantAdmin,
                Assert.Single(action.GetCustomAttributes<AuthorizeAttribute>()).Policy);
    }

    [Fact]
    public void The_audit_explorer_and_the_tenant_summary_expose_no_way_to_write_anything()
    {
        // The audit log is append-only and that must not regress through a feature that reads it.
        // The guarantee is enforced by REVOKE UPDATE, DELETE, TRUNCATE on the table from
        // nexora_pipeline_app; this is the application-side half — there is no verb here that
        // could ever attempt the write in the first place.
        foreach (var controller in new[] { typeof(PlatformAuditExplorerController), typeof(TenantOperationsController) })
        {
            Assert.Empty(MutatingActions(controller));
            Assert.All(Actions(controller), action =>
                Assert.All(action.GetCustomAttributes<HttpMethodAttribute>(), http =>
                    Assert.Equal(["GET"], http.HttpMethods)));
        }
    }

    // ---- helpers -----------------------------------------------------------

    /// <summary>The production policy registration, not a copy of it. See <see cref="PlatformSupportFixture.Authorization"/>.</summary>
    private static IAuthorizationService BuildAuthorizationService()
        => PlatformSupportFixture.Authorization();

    private static ClaimsPrincipal Principal(PlatformRole role)
        => PlatformSupportFixture.Actor(role: role);

    private static IEnumerable<MethodInfo> Actions(Type controller)
        => controller.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => m.GetCustomAttributes<HttpMethodAttribute>().Any());

    private static IEnumerable<MethodInfo> MutatingActions(Type controller)
        => Actions(controller).Where(m => m.GetCustomAttributes<HttpMethodAttribute>()
            .SelectMany(a => a.HttpMethods)
            .Any(verb => verb != "GET" && verb != "HEAD"));
}
