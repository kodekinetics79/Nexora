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
        Assert.Contains("security.privileged-mfa-policy", decision.BlockingControls);
        Assert.Contains("integrations.mandatory", decision.BlockingControls);
        Assert.Equal(decision.Controls.Where(x => !x.Satisfied).Select(x => x.Code), decision.BlockingControls);
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
        Assert.Equal("tenant-activation/2026-08-10.v2", decision!.PolicyVersion);
        Assert.False(decision.Ready);
        // Every unsatisfied control still blocks, whether or not it has somewhere to be fixed.
        Assert.Equal(decision.Controls.Where(x => !x.Satisfied).Select(x => x.Code), decision.BlockingControls);
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
