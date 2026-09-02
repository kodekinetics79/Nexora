using System.Security.Claims;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Platform.Activation;
using ERP_RFQ_Automation.Platform.Entitlements;
using ERP_RFQ_Automation.Platform.Lifecycle;
using ERP_RFQ_Automation.Platform.Models;
using ERP_RFQ_Automation.Platform.Services;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace ERP_RFQ_Automation.Tests;

public sealed class TenantActivationPolicyTests
{
    /// <summary>
    /// Billing currency versus functional currency. A Saudi client quotes in SAR
    /// (<c>Tenant.BaseCurrencyCode</c>, seeded as its base <c>Currency</c> row) and is billed by
    /// Nexora in USD (the platform constant carried on the pinned rate card). Activation used to
    /// compare the FUNCTIONAL column to "USD", so such a client could never activate as Production.
    /// </summary>
    [Fact]
    public async Task A_tenant_quoting_in_SAR_and_billed_in_USD_passes_the_currency_and_rate_card_controls()
    {
        using var database = new TestDb();
        await using var db = database.ContextFor(null);
        var (tenantId, _) = await SeedBillableTenantAsync(db, functionalCurrency: "SAR", cardCurrency: "USD");

        var decision = await Evaluate(db, tenantId);

        var currencyTax = decision.Controls.Single(x => x.Code == "billing.currency-tax");
        var rateCard = decision.Controls.Single(x => x.Code == "commercial.rate-card");
        Assert.True(currencyTax.Satisfied, currencyTax.Detail);
        Assert.True(rateCard.Satisfied, rateCard.Detail);
        Assert.DoesNotContain("billing.currency-tax", decision.BlockingControls);
        Assert.DoesNotContain("commercial.rate-card", decision.BlockingControls);
    }

    [Fact]
    public async Task A_rate_card_outside_the_platform_billing_currency_still_blocks_regardless_of_the_tenants_own_currency()
    {
        using var database = new TestDb();
        await using var db = database.ContextFor(null);
        var (tenantId, _) = await SeedBillableTenantAsync(db, functionalCurrency: "AED", cardCurrency: "AED");

        var decision = await Evaluate(db, tenantId);

        Assert.False(decision.Controls.Single(x => x.Code == "billing.currency-tax").Satisfied);
        Assert.False(decision.Controls.Single(x => x.Code == "commercial.rate-card").Satisfied);
    }

    [Fact]
    public async Task The_tenants_functional_currency_is_never_read_by_the_billing_controls()
    {
        // The same tenant, every functional currency: the verdict on the two billing controls
        // depends on the rate card and the tax identity alone.
        foreach (var functional in new[] { "USD", "SAR", "AED", "EUR" })
        {
            using var database = new TestDb();
            await using var db = database.ContextFor(null);
            var (tenantId, _) = await SeedBillableTenantAsync(db, functional, cardCurrency: "USD");

            var decision = await Evaluate(db, tenantId);

            Assert.True(decision.Controls.Single(x => x.Code == "billing.currency-tax").Satisfied, functional);
            Assert.True(decision.Controls.Single(x => x.Code == "commercial.rate-card").Satisfied, functional);
        }
    }

    private static async Task<TenantActivationDecision> Evaluate(ErpRfqAutomationContext db, long tenantId)
    {
        var access = new TenantAccessService(db, new MemoryCache(new MemoryCacheOptions()),
            NullLogger<TenantAccessService>.Instance);
        var decision = await new TenantActivationPolicyService(db, new NoopAudit(), access).EvaluateAsync(tenantId);
        return decision!;
    }

    /// <summary>A billable tenant with a tax number and a pinned, effective, priced rate card.</summary>
    private static async Task<(long TenantId, long RateCardId)> SeedBillableTenantAsync(
        ErpRfqAutomationContext db, string functionalCurrency, string cardCurrency)
    {
        Seed.EnsureBusinessUnit(db, 1_991);
        var plan = new Plan
        {
            Id = 1_992, Code = "bounded", Name = "Bounded", IsActive = true,
            MaxSeats = 5, MaxDocsPerMonth = 100, MaxConcurrentExtractionJobs = 2, Weight = 1,
            Features = "{}"
        };
        db.Set<Plan>().Add(plan);
        var card = new ERP_RFQ_Automation.Billing.RateCard
        {
            Id = 1_994, Code = "standard-2026", Currency = cardCurrency, IsActive = true,
            EffectiveFromUtc = DateTime.UtcNow.AddDays(-30), EffectiveToUtc = null, CreatedBy = "tests",
            Lines = { new ERP_RFQ_Automation.Billing.RateCardLine { MeterKey = "seats", IncludedQuantity = 0, UnitPrice = 25m, Unit = "seat" } }
        };
        db.Set<ERP_RFQ_Automation.Billing.RateCard>().Add(card);
        db.Set<Tenant>().Add(new Tenant
        {
            Id = 1_993, Name = "Noor & Sons", Slug = "noor-sons",
            Status = TenantStatus.Provisioning, PlanId = plan.Id, RateCardId = card.Id,
            PrimaryBusinessUnitId = 1_991, CreatedOn = DateTime.UtcNow,
            BaseCurrencyCode = functionalCurrency, TaxNumber = "300012345600003",
            BillingMode = TenantBillingMode.Billable
        });
        await db.SaveChangesAsync();
        return (1_993, card.Id);
    }

    [Fact]
    public async Task Decision_is_structured_versioned_and_fails_closed_for_missing_controls()
    {
        using var database = new TestDb();
        await using var db = database.ContextFor(null);
        Seed.EnsureBusinessUnit(db, 991);
        var plan = new Plan
        {
            Id = 992, Code = "bounded", Name = "Bounded", IsActive = true,
            MaxSeats = 5, MaxDocsPerMonth = 100, MaxConcurrentExtractionJobs = 2, Weight = 1,
            Features = "{}"
        };
        db.Set<Plan>().Add(plan);
        db.Set<Tenant>().Add(new Tenant
        {
            Id = 993, Name = "Policy Tenant", Slug = "policy-tenant",
            Status = TenantStatus.Provisioning, PlanId = plan.Id,
            PrimaryBusinessUnitId = 991, CreatedOn = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        var access = new TenantAccessService(db, new MemoryCache(new MemoryCacheOptions()),
            NullLogger<TenantAccessService>.Instance);
        var service = new TenantActivationPolicyService(db, new NoopAudit(), access);

        var decision = await service.EvaluateAsync(993);

        Assert.NotNull(decision);
        Assert.False(decision!.Ready);
        Assert.Equal(TenantActivationPolicy.Version, decision.PolicyVersion);
        Assert.Equal("PROSPECT", decision.CommercialState);
        Assert.Equal("RESTRICTED", decision.AccessState);
        Assert.Contains("identity.legal-customer", decision.BlockingControls);
        Assert.Contains("entitlements.typed-hard-limits", decision.BlockingControls);
        Assert.Contains("integrations.mandatory", decision.BlockingControls);

        // security.privileged-mfa-policy is unsatisfied and does NOT block activation: the tenant
        // identity plane persists no MFA assurance, so gating switch-on on an attestation about a
        // capability that does not exist was collecting a signature, not checking anything. It is
        // still owed to production and still makes certification impossible.
        var mfa = decision.Controls.Single(x => x.Code == "security.privileged-mfa-policy");
        Assert.False(mfa.Satisfied);
        Assert.Equal(ActivationControlDispositions.CertificationOnly, mfa.Disposition);
        Assert.DoesNotContain("security.privileged-mfa-policy", decision.BlockingControls);
        Assert.Contains("security.privileged-mfa-policy", decision.ProductionBlockingControls);
        Assert.False(decision.ProductionReadiness.Certifiable);

        Assert.Equal(
            decision.Controls
                .Where(x => !x.Satisfied && x.Disposition != ActivationControlDispositions.CertificationOnly)
                .Select(x => x.Code),
            decision.BlockingControls);
    }

    /// <summary>
    /// The drift guard for control #15.
    ///
    /// <para>The remediation catalogue is the map from a blocking control to the screen that owns
    /// its fix, and its only failure mode is silence: somebody adds a fifteenth
    /// <c>Add(...)</c> to the policy, the console renders the new code as a bare string with no
    /// Resolve button and no explanation, and the operator is back to guessing which of eleven tabs
    /// owns it — which is the defect this whole thing exists to close. So this evaluates a tenant
    /// that fails EVERY control and demands that each returned code is either resolvable or
    /// carries a written reason for not being.</para>
    ///
    /// <para>It deliberately asserts against the codes the SERVICE returned rather than against
    /// the catalogue's own key list. A catalogue that agrees with itself proves nothing.</para>
    /// </summary>
    [Fact]
    public async Task Every_evaluated_control_is_either_resolvable_or_records_why_it_is_not()
    {
        using var database = new TestDb();
        await using var db = database.ContextFor(null);
        // No business unit, no plan, no data asset, no audit history, no provisioning execution:
        // every control that can fail, does. A tenant in this shape is what an operator is looking
        // at on the morning after a provisioning run went wrong.
        db.Set<Tenant>().Add(new Tenant
        {
            Id = 9971, Name = "Unremediated", Slug = "unremediated",
            Status = TenantStatus.Provisioning, CreatedOn = DateTime.UtcNow
        });
        // data.lifecycle-operational is the one control that PASSES by default — an absent
        // offboarding record is a clean one — so it needs a scheduled deletion to fail. Without
        // this row the tenant fails thirteen of fourteen controls and the fourteenth never gets
        // its mapping checked, which is exactly the gap this test exists to close.
        db.Set<TenantOffboarding>().Add(new TenantOffboarding
        {
            TenantId = 9971, Stage = TenantOffboardingStage.PendingDeletion,
            RetentionDays = 30, DeletionScheduledOn = DateTime.UtcNow,
            PurgeEligibleOn = DateTime.UtcNow.AddDays(30),
            DeletionReason = "Seeded so the lifecycle control fails alongside the others.",
            DeletionScheduledBy = "test"
        });
        await db.SaveChangesAsync();
        var service = new TenantActivationPolicyService(db, new NoopAudit(),
            new TenantAccessService(db, new MemoryCache(new MemoryCacheOptions()),
                NullLogger<TenantAccessService>.Instance));

        var decision = await service.EvaluateAsync(9971);

        Assert.NotNull(decision);
        Assert.NotEmpty(decision!.Controls);
        // Nothing passes, so nothing is excused from the check by being satisfied.
        Assert.All(decision.Controls, control => Assert.False(control.Satisfied));

        var unmapped = decision.Controls
            .Where(control => !ActivationControlRemediationCatalog.Covers(control.Code))
            .Select(control => control.Code).ToArray();
        Assert.True(unmapped.Length == 0,
            "These activation controls have no entry in ActivationControlRemediationCatalog, so the "
            + "console can only render them as a bare code with no way to act on them: "
            + string.Join(", ", unmapped));

        foreach (var control in decision.Controls)
        {
            var reason = ActivationControlRemediationCatalog.NoRemedyReason(control.Code);
            if (control.Remediation is { } remediation)
            {
                Assert.Null(reason);
                Assert.False(string.IsNullOrWhiteSpace(remediation.Surface));
                Assert.False(string.IsNullOrWhiteSpace(remediation.Action));
                Assert.False(string.IsNullOrWhiteSpace(remediation.Label));
                Assert.False(string.IsNullOrWhiteSpace(remediation.Hint));
                // The console gates its Resolve button on this string. An authority it does not
                // recognise fails open or fails silent depending on how it is written, and both
                // are worse than the control having no button at all.
                Assert.Contains(remediation.RequiredAuthority, new[]
                {
                    ActivationRemediationAuthorities.Owner,
                    ActivationRemediationAuthorities.Billing,
                    ActivationRemediationAuthorities.TenantAdmin,
                    ActivationRemediationAuthorities.OwnerMfa
                });
            }
            else
            {
                // "No resolver" has to be a decision somebody wrote down and can be held to, not
                // the silence left by a control nobody got round to mapping.
                Assert.False(string.IsNullOrWhiteSpace(reason),
                    $"Activation control '{control.Code}' has no remediation and no recorded reason "
                    + "for having none.");
            }
        }

        // The four that are unresolvable BY DESIGN, pinned by name. Growing this list is a decision
        // about what an operator can no longer fix from the console, and it should cost a test edit.
        var unresolvable = decision.Controls.Where(x => x.Remediation is null)
            .Select(x => x.Code).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        Assert.Equal(
            ["admin.first-activated", "audit.health", "data.lifecycle-operational", "provisioning.completed-verified"],
            unresolvable);
    }

    /// <summary>
    /// The remediation is navigation and nothing else. If attaching it could move a control's
    /// verdict — or the policy version stamped into every <c>tenant.activate</c> audit row — then
    /// a screen-location change would be rewriting the record of what was checked.
    /// </summary>
    [Fact]
    public async Task Remediation_changes_no_verdict_and_does_not_move_the_policy_version()
    {
        using var database = new TestDb();
        await using var db = database.ContextFor(null);
        db.Set<Tenant>().Add(new Tenant
        {
            Id = 9972, Name = "Verdict", Slug = "verdict",
            Status = TenantStatus.Provisioning, CreatedOn = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
        var service = new TenantActivationPolicyService(db, new NoopAudit(),
            new TenantAccessService(db, new MemoryCache(new MemoryCacheOptions()),
                NullLogger<TenantAccessService>.Instance));

        var decision = await service.EvaluateAsync(9972);

        Assert.NotNull(decision);
        Assert.Equal("tenant-activation/2026-08-12.v3", decision!.PolicyVersion);
        Assert.False(decision.Ready);
        // Every unsatisfied control still blocks activation, whether or not it has somewhere to be
        // fixed — except the one that is explicitly a certification requirement rather than a gate.
        Assert.Equal(
            decision.Controls
                .Where(x => !x.Satisfied && x.Disposition != ActivationControlDispositions.CertificationOnly)
                .Select(x => x.Code),
            decision.BlockingControls);
        Assert.All(decision.Controls.Where(x => !x.Satisfied), control => Assert.True(control.BlocksProduction));
        Assert.False(decision.ProductionReadiness.Certifiable);
        // A satisfied control carries no remediation: there is nothing to fix, and a Resolve
        // button beside a passing control is an invitation to edit a customer's record for sport.
        Assert.All(decision.Controls.Where(x => x.Satisfied), control => Assert.Null(control.Remediation));
    }

    [Fact]
    public async Task Activate_refuses_transition_and_returns_the_same_authoritative_decision()
    {
        using var database = new TestDb();
        await using var db = database.ContextFor(null);
        Seed.EnsureBusinessUnit(db, 994);
        db.Set<Tenant>().Add(new Tenant
        {
            Id = 995, Name = "Blocked", Slug = "blocked", Status = TenantStatus.Provisioning,
            PrimaryBusinessUnitId = 994, CreatedOn = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
        var service = new TenantActivationPolicyService(db, new NoopAudit(),
            new TenantAccessService(db, new MemoryCache(new MemoryCacheOptions()),
                NullLogger<TenantAccessService>.Instance));

        var error = await Assert.ThrowsAsync<TenantActivationBlockedException>(() =>
            service.ActivateAsync(995, new ClaimsPrincipal(new ClaimsIdentity([
                new Claim("email", "owner@example.test")], "test")), new DefaultHttpContext()));

        Assert.False(error.Decision.Ready);
        Assert.Equal(TenantStatus.Provisioning, db.Set<Tenant>().Single(x => x.Id == 995).Status);
    }

    [Fact]
    public async Task Control_evidence_rejects_secret_bearing_references()
    {
        using var database = new TestDb();
        await using var db = database.ContextFor(null);
        db.Set<Tenant>().Add(new Tenant
        {
            Id = 996, Name = "Evidence", Slug = "evidence", Status = TenantStatus.Provisioning,
            CreatedOn = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
        var service = new TenantActivationPolicyService(db, new NoopAudit(),
            new TenantAccessService(db, new MemoryCache(new MemoryCacheOptions()),
                NullLogger<TenantAccessService>.Instance));
        var request = new RecordActivationControlEvidenceRequest(
            "approved", "https://operator:secret@example.test/evidence?token=secret", new string('a', 64),
            DateTime.UtcNow.AddMinutes(-1), DateTime.UtcNow.AddDays(1), "Reviewed security controls.");

        await Assert.ThrowsAsync<ArgumentException>(() => service.RecordControlEvidenceAsync(
            996, "security.privileged-mfa-policy", request, new ClaimsPrincipal(), new DefaultHttpContext()));
        var accepted = await service.RecordControlEvidenceAsync(996, "security.privileged-mfa-policy",
            request with { EvidenceReference = "urn:nexora:evidence:security:996:v1" },
            new ClaimsPrincipal(), new DefaultHttpContext());

        Assert.Equal("urn:nexora:evidence:security:996:v1", accepted.EvidenceReference);
    }

    private sealed class NoopAudit : IPlatformAuditService
    {
        public Task WriteAsync(ClaimsPrincipal actor, string action, string? targetType = null,
            string? targetId = null, object? metadata = null, long? actAsTenantId = null,
            HttpContext? httpContext = null, CancellationToken ct = default) => Task.CompletedTask;
    }
}
