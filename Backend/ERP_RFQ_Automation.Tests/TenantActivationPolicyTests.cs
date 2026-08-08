using System.Security.Claims;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Platform.Activation;
using ERP_RFQ_Automation.Platform.Entitlements;
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

    private sealed class NoopAudit : IPlatformAuditService
    {
        public Task WriteAsync(ClaimsPrincipal actor, string action, string? targetType = null,
            string? targetId = null, object? metadata = null, long? actAsTenantId = null,
            HttpContext? httpContext = null, CancellationToken ct = default) => Task.CompletedTask;
    }
}
