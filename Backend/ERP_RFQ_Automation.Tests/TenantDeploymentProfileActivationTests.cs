using System.Security.Claims;
using System.Text.Json;
using ERP_RFQ_Automation.Billing;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Platform.Activation;
using ERP_RFQ_Automation.Platform.Entitlements;
using ERP_RFQ_Automation.Platform.Models;
using ERP_RFQ_Automation.Platform.Provisioning;
using ERP_RFQ_Automation.Platform.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// The deployment profile, and the one property that makes it safe: it changes what an unsatisfied
/// control MEANS, never whether the control passed and never what PRODUCTION does with it.
///
/// <para><b>The state this pins.</b> A workspace on a laptop cannot register the customer's storage
/// account, connect their ERP, federate their identity provider or obtain a tax-authority
/// certification, so four activation controls could never pass there. With one bit per control the
/// only two available outcomes were a local environment that could never be brought up, or somebody
/// quietly relaxing the production gate to make it come up. Each test below asserts one half of the
/// arrangement that replaced that: the deferral is real on LOCAL_TEST, and it is unavailable,
/// invisible and impossible on PRODUCTION.</para>
/// </summary>
public sealed class TenantDeploymentProfileActivationTests
{
    /// <summary>
    /// The controls that the catalogued prerequisites explain AND that still gate activation. Every
    /// one of them is a real third-party or infrastructure dependency; none is a defect in the
    /// tenant record.
    /// </summary>
    private static readonly string[] DeferrableControls =
    [
        "billing.currency-tax",     // tax.production-certification
        "data.residency-isolation", // storage.client-supplied + backup.production-pitr
        "integrations.mandatory"    // integration.erp-accounting
    ];

    /// <summary>
    /// The control that is catalogued, still owed to production, and is NOT an activation gate on
    /// any profile — including PRODUCTION.
    ///
    /// <para>It moved because it could never be a gate honestly: the tenant identity plane persists
    /// no MFA assurance and the policy service refuses to infer it from a password or from
    /// platform-operator MFA, so every activation was gated on an Owner attesting to a capability
    /// that does not exist. The tests below pin the half that must NOT have moved with it — it
    /// still blocks production, and certification is still impossible without it.</para>
    /// </summary>
    private static readonly string[] CertificationOnlyControls =
    [
        "security.privileged-mfa-policy" // identity.enterprise-sso-scim
    ];

    /// <summary>Everything production is still owed, whichever gate it stands at.</summary>
    private static readonly string[] ProductionOwedControls =
        [.. DeferrableControls, .. CertificationOnlyControls];

    [Fact]
    public async Task A_deferred_external_dependency_activates_a_local_test_tenant()
    {
        using var harness = new ProvisioningHarness();
        var tenantId = await ReadyTenantAsync(harness, "northwind-local", "ada@northwind.test");
        await SetProfileAsync(harness, tenantId, TenantDeploymentProfile.LocalTest,
            "Laptop workspace for reproducing the RFQ intake defect.");

        var decision = await EvaluateAsync(harness, tenantId);

        // Nothing that is actually a defect is outstanding, so the only unsatisfied controls are
        // the four the catalogue explains — and every one of them is deferred rather than blocking.
        Assert.Empty(decision.BlockingControls);
        Assert.True(decision.Ready);
        Assert.Equal(TenantDeploymentProfiles.LocalTest, decision.DeploymentProfile);
        foreach (var code in DeferrableControls)
        {
            var control = decision.Controls.Single(x => x.Code == code);
            Assert.False(control.Satisfied);
            Assert.Contains(control.Disposition, new[]
            {
                ActivationControlDispositions.Deferred, ActivationControlDispositions.ExternallyBlocked
            });
            // The deferral names what production would need. A relaxed gate with no statement of
            // what it relaxed is the thing this whole vocabulary exists to avoid.
            Assert.False(string.IsNullOrWhiteSpace(control.ProductionRequirement));
            Assert.False(string.IsNullOrWhiteSpace(control.DeferralKey));
        }

        // The certification-only control is unsatisfied here too, and is reported as what it is
        // rather than borrowing the profile's deferral vocabulary — it would read identically on a
        // PRODUCTION tenant, which is the whole point of it not being a gate.
        foreach (var code in CertificationOnlyControls)
        {
            var control = decision.Controls.Single(x => x.Code == code);
            Assert.False(control.Satisfied);
            Assert.Equal(ActivationControlDispositions.CertificationOnly, control.Disposition);
            Assert.True(control.BlocksProduction);
            Assert.False(string.IsNullOrWhiteSpace(control.ProductionRequirement));
        }

        // The activation actually goes through — this is the point of the profile.
        var activated = await ActivateAsync(harness, tenantId);
        Assert.True(activated.Ready);
        await using var db = harness.Context();
        Assert.Equal(TenantStatus.Active,
            (await db.Set<Tenant>().IgnoreQueryFilters().SingleAsync(x => x.Id == tenantId)).Status);
    }

    [Fact]
    public async Task The_same_dependency_blocks_activation_on_a_production_tenant()
    {
        using var harness = new ProvisioningHarness();
        var tenantId = await ReadyTenantAsync(harness, "northwind-prod", "grace@northwind.test");
        // No profile change: PRODUCTION is the default, and the default is the strict one.

        var decision = await EvaluateAsync(harness, tenantId);

        Assert.Equal(TenantDeploymentProfiles.Production, decision.DeploymentProfile);
        Assert.False(decision.Ready);
        foreach (var code in DeferrableControls)
        {
            var control = decision.Controls.Single(x => x.Code == code);
            Assert.Equal(ActivationControlDispositions.Blocking, control.Disposition);
            Assert.Null(control.DeferralKey);
            Assert.Contains(code, decision.BlockingControls);
        }

        // PRODUCTION gets no discount on the certification-only control either: it reads exactly as
        // it does on LOCAL_TEST, because it is not the profile that took it off the gate.
        foreach (var code in CertificationOnlyControls)
        {
            var control = decision.Controls.Single(x => x.Code == code);
            Assert.Equal(ActivationControlDispositions.CertificationOnly, control.Disposition);
            Assert.DoesNotContain(code, decision.BlockingControls);
            Assert.Contains(code, decision.CertificationOnlyControls);
            // Not a discount: it is still on the list production has to clear.
            Assert.Contains(code, decision.ProductionBlockingControls);
        }

        Assert.Empty(decision.DeferredControls);
        Assert.Empty(decision.ExternallyBlockedControls);

        // On a production tenant "blocking" is exactly "unsatisfied and not certification-only".
        // The exception is named rather than implicit: growing it is a decision about what can be
        // switched on unproven, and it should cost a test edit.
        Assert.Equal(
            decision.Controls
                .Where(x => !x.Satisfied && x.Disposition != ActivationControlDispositions.CertificationOnly)
                .Select(x => x.Code),
            decision.BlockingControls);

        var refusal = await Assert.ThrowsAsync<TenantActivationBlockedException>(
            () => ActivateAsync(harness, tenantId));
        Assert.False(refusal.Decision.Ready);
        await using var db = harness.Context();
        Assert.Equal(TenantStatus.Provisioning,
            (await db.Set<Tenant>().IgnoreQueryFilters().SingleAsync(x => x.Id == tenantId)).Status);
    }

    [Fact]
    public async Task A_deferral_stays_a_production_blocker_and_forbids_production_certification()
    {
        using var harness = new ProvisioningHarness();
        var tenantId = await ReadyTenantAsync(harness, "northwind-cert", "hopper@northwind.test");
        await SetProfileAsync(harness, tenantId, TenantDeploymentProfile.LocalTest,
            "Laptop workspace for reproducing the RFQ intake defect.");

        var decision = await EvaluateAsync(harness, tenantId);

        // Activatable here, and still owing production every single one of them — the deferrable
        // ones and the certification-only one alike.
        Assert.True(decision.Ready);
        foreach (var code in ProductionOwedControls)
        {
            Assert.Contains(code, decision.ProductionBlockingControls);
            Assert.True(decision.Controls.Single(x => x.Code == code).BlocksProduction);
        }

        Assert.False(decision.ProductionReadiness.Certifiable);
        Assert.Equal(decision.ProductionBlockingControls, decision.ProductionReadiness.BlockingControls);

        // Every catalogued prerequisite is reported, including the one that governs no activation
        // control at all — production-readiness is the only place it is ever answered for.
        Assert.Equal(DeploymentPrerequisiteCatalog.All.Count, decision.ProductionReadiness.Prerequisites.Count);
        var clamAv = decision.ProductionReadiness.Prerequisites
            .Single(x => x.Key == DeploymentPrerequisiteCatalog.PrivateClamAv);
        Assert.Null(clamAv.ControlCode);
        Assert.False(clamAv.Satisfied);

        // And the operator is told, on the decision itself, that this is what happened.
        Assert.Contains(decision.Warnings, warning => warning.Contains("LOCAL_TEST"));
    }

    [Fact]
    public async Task An_unapproved_demo_tenant_defers_nothing_and_fails_closed_like_production()
    {
        using var harness = new ProvisioningHarness();
        var tenantId = await ReadyTenantAsync(harness, "northwind-demo", "turing@northwind.test");

        // The profile column set WITHOUT the approval the audited endpoint records — a hand-written
        // UPDATE, a restored row, a fixture. It must buy nothing.
        await using (var db = harness.Context())
        {
            var tenant = await db.Set<Tenant>().IgnoreQueryFilters().SingleAsync(x => x.Id == tenantId);
            tenant.DeploymentProfile = TenantDeploymentProfile.Demo;
            await db.SaveChangesAsync();
        }

        var decision = await EvaluateAsync(harness, tenantId);

        Assert.Equal(TenantDeploymentProfiles.Demo, decision.DeploymentProfile);
        Assert.False(decision.Ready);
        Assert.Empty(decision.DeferredControls);
        Assert.Empty(decision.ExternallyBlockedControls);
        Assert.Equal(
            decision.Controls
                .Where(x => !x.Satisfied && x.Disposition != ActivationControlDispositions.CertificationOnly)
                .Select(x => x.Code),
            decision.BlockingControls);
        Assert.Contains(decision.Warnings, warning => warning.Contains("no recorded Owner approval"));

        // With the approval the audited endpoint writes, the same tenant defers exactly the three
        // that still gate activation. The certification-only control is in neither list because the
        // profile never had anything to do with it.
        await SetProfileAsync(harness, tenantId, TenantDeploymentProfile.Demo,
            "Sales demonstration workspace for the Riyadh pilot.");
        var approved = await EvaluateAsync(harness, tenantId);
        Assert.True(approved.Ready);
        Assert.Equal(DeferrableControls.Length,
            approved.DeferredControls.Count + approved.ExternallyBlockedControls.Count);
        Assert.Equal(CertificationOnlyControls, approved.CertificationOnlyControls);
        Assert.False(approved.ProductionReadiness.Certifiable);
    }

    // ---- fixture ---------------------------------------------------------------------------

    /// <summary>
    /// A genuinely provisioned tenant with every control satisfied EXCEPT the four that depend on
    /// a third party. Built by running the real provisioning engine rather than by hand-inserting
    /// rows, so the controls that read provisioning's own output are answered by provisioning.
    /// </summary>
    private static async Task<long> ReadyTenantAsync(ProvisioningHarness harness, string slug, string email)
    {
        long planId;
        long rateCardId;
        await using (var db = harness.Context())
        {
            var plan = new Plan
            {
                Code = $"enterprise-{Guid.NewGuid():N}", Name = "Enterprise", IsActive = true,
                MaxSeats = 25, MaxDocsPerMonth = 5000, MaxConcurrentExtractionJobs = 4, Weight = 3,
                MonthlyPriceUsd = 999m,
                Features = JsonSerializer.Serialize(
                    TypedEntitlementCatalog.Keys.ToDictionary(key => key, _ => true))
            };
            db.Set<Plan>().Add(plan);

            var card = new RateCard
            {
                Code = $"standard-{Guid.NewGuid():N}", Currency = "USD", IsActive = true,
                EffectiveFromUtc = DateTime.UtcNow.AddDays(-30), Version = 1
            };
            card.Lines.Add(new RateCardLine
            {
                MeterKey = "documents", IncludedQuantity = 1000, UnitPrice = 0.25m, Unit = "document"
            });
            db.Set<RateCard>().Add(card);
            await db.SaveChangesAsync();
            planId = plan.Id;
            rateCardId = card.Id;
        }

        // The password path, so the founding administrator is created ACTIVE and
        // admin.first-activated is answered by a real user holding a real Owner-rank role.
        var execution = await harness.ProvisionAsync(ProvisioningHarness.Request(
            slug, email, planId, activation: AdminActivationMethods.Password, password: "Correct-Horse-9!"));
        Assert.Equal(ProvisioningExecutionState.Succeeded, execution.State);
        var tenantId = execution.TenantId!.Value;

        await using (var db = harness.Context())
        {
            var tenant = await db.Set<Tenant>().IgnoreQueryFilters().SingleAsync(x => x.Id == tenantId);
            tenant.LegalName = "Northwind Trading LLC";
            tenant.RegistrationNumber = "CR-1010101010";
            tenant.ContactEmail = "info@northwind.test";
            tenant.BillingContactName = "Accounts Payable";
            tenant.BillingContactEmail = "ap@northwind.test";
            tenant.BillingAddress = "1 Trading Way, Riyadh";
            tenant.PaymentTermsDays = 30;
            tenant.BaseCurrencyCode = "USD";
            tenant.RateCardId = rateCardId;
            tenant.ContractStartOn = DateTime.UtcNow.AddDays(-1);
            tenant.BillingStartsOn = DateTime.UtcNow.AddDays(-1);
            // Deliberately absent: TaxNumber (production tax certification), DataRegion + a verified
            // data asset (client-supplied storage / PITR evidence), the privileged-MFA attestation
            // (enterprise SSO/SCIM) and the mandatory-integration evidence (the customer's ERP).

            // audit.health reads one persisted privileged occurrence against this tenant. The
            // harness has no IPlatformAuditService, so the row the runner would have written is
            // written here instead.
            db.Set<PlatformAuditLog>().Add(new PlatformAuditLog
            {
                ActorPlatformUserId = 7, ActAsTenantId = tenantId, Action = "tenant.provision",
                TargetType = nameof(Tenant), TargetId = tenantId.ToString(),
                Result = PlatformAuditResults.Success, CreatedOn = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }

        return tenantId;
    }

    /// <summary>Writes the profile exactly as the Owner-gated endpoint does: value, reason, approver, instant.</summary>
    private static async Task SetProfileAsync(
        ProvisioningHarness harness, long tenantId, TenantDeploymentProfile profile, string reason)
    {
        await using var db = harness.Context();
        var tenant = await db.Set<Tenant>().IgnoreQueryFilters().SingleAsync(x => x.Id == tenantId);
        tenant.DeploymentProfile = profile;
        tenant.DeploymentProfileReason = reason;
        tenant.DeploymentProfileApprovedBy = "owner@nexora.app";
        tenant.DeploymentProfileApprovedOn = DateTime.UtcNow;
        await db.SaveChangesAsync();
    }

    private static async Task<TenantActivationDecision> EvaluateAsync(ProvisioningHarness harness, long tenantId)
    {
        await using var db = harness.Context();
        var decision = await PolicyFor(db).EvaluateAsync(tenantId);
        Assert.NotNull(decision);
        return decision!;
    }

    private static async Task<TenantActivationDecision> ActivateAsync(ProvisioningHarness harness, long tenantId)
    {
        await using var db = harness.Context();
        return await PolicyFor(db).ActivateAsync(tenantId,
            new ClaimsPrincipal(new ClaimsIdentity([new Claim("email", "owner@nexora.app")], "test")),
            new DefaultHttpContext());
    }

    private static TenantActivationPolicyService PolicyFor(ErpRfqAutomationContext db)
        => new(db, new NoopAudit(),
            new TenantAccessService(db, new MemoryCache(new MemoryCacheOptions()),
                NullLogger<TenantAccessService>.Instance));

    private sealed class NoopAudit : IPlatformAuditService
    {
        public Task WriteAsync(ClaimsPrincipal actor, string action, string? targetType = null,
            string? targetId = null, object? metadata = null, long? actAsTenantId = null,
            HttpContext? httpContext = null, CancellationToken ct = default) => Task.CompletedTask;
    }
}
