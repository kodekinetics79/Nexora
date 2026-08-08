using System.Reflection;
using System.Security.Claims;
using ERP_RFQ_Automation.Platform.Auth;
using ERP_RFQ_Automation.Platform.DataAssets;
using ERP_RFQ_Automation.Platform.Models;
using ERP_RFQ_Automation.Platform.Services;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace ERP_RFQ_Automation.Tests;

public sealed class TenantDataAssetRegistryTests
{
    private const long TenantId = 83001;
    private const long BusinessUnitId = 83002;

    [Fact]
    public async Task Register_verify_and_activation_data_decision_are_evidence_backed_and_audited()
    {
        using var database = new TestDb();
        await using var context = database.ContextFor(null);
        context.Set<Tenant>().Add(Tenant());
        await context.SaveChangesAsync();
        var registry = Registry(context);

        var registered = await registry.RegisterAsync(
            TenantId, Registration(), Actor(), null, default);
        var blocked = await registry.ActivationDataDecisionAsync(TenantId, default);

        Assert.Equal(TenantDataAssetStatuses.Registered, registered.Status);
        Assert.False(blocked.DataGateReady);
        Assert.Contains(blocked.Blockers, x => x.Contains("not verified", StringComparison.Ordinal));

        var verified = await registry.VerifyAsync(TenantId, registered.Id,
            Verification(registered.Version), Actor(), null, default);
        var ready = await registry.ActivationDataDecisionAsync(TenantId, default);

        Assert.Equal(TenantDataAssetStatuses.Verified, verified.Status);
        Assert.Equal(1, verified.VerificationVersion);
        Assert.Equal(new string('a', 64), verified.VerificationEvidenceSha256);
        Assert.True(ready.DataGateReady);
        Assert.Equal("DataGateReady", ready.Decision);
        Assert.Contains("does not activate", ready.Boundary, StringComparison.OrdinalIgnoreCase);
        var actions = await context.Set<PlatformAuditLog>().OrderBy(x => x.Id).Select(x => x.Action).ToListAsync();
        Assert.Equal(new[]
        {
            TenantDataAssetRegistryService.RegisterAction,
            TenantDataAssetRegistryService.VerifyAction
        }, actions);
    }

    [Theory]
    [InlineData(TenantStatus.Archived)]
    [InlineData(TenantStatus.Suspended)]
    public async Task Activation_data_decision_fails_closed_for_ineligible_lifecycle_state(
        TenantStatus status)
    {
        using var database = new TestDb();
        await using var context = database.ContextFor(null);
        var tenant = Tenant();
        tenant.Status = status;
        context.Set<Tenant>().Add(tenant);
        await context.SaveChangesAsync();
        var registry = Registry(context);
        var registered = await registry.RegisterAsync(TenantId, Registration(), Actor(), null, default);
        await registry.VerifyAsync(TenantId, registered.Id, Verification(registered.Version), Actor(), null, default);

        var decision = await registry.ActivationDataDecisionAsync(TenantId, default);

        Assert.False(decision.DataGateReady);
        Assert.Contains(decision.Blockers,
            blocker => blocker.Contains(status.ToString(), StringComparison.Ordinal));
    }

    [Fact]
    public async Task Verification_refuses_a_scope_or_region_not_owned_by_the_tenant()
    {
        using var database = new TestDb();
        await using var context = database.ContextFor(null);
        context.Set<Tenant>().Add(Tenant());
        await context.SaveChangesAsync();
        var registry = Registry(context);
        var registered = await registry.RegisterAsync(TenantId, Registration(), Actor(), null, default);

        await Assert.ThrowsAsync<TenantDataAssetValidationException>(() => registry.VerifyAsync(
            TenantId, registered.Id, Verification(registered.Version) with { ObservedBusinessUnitId = 99999 },
            Actor(), null, default));
        await Assert.ThrowsAsync<TenantDataAssetValidationException>(() => registry.VerifyAsync(
            TenantId, registered.Id, Verification(registered.Version) with { ObservedRegion = "eu-west-1" },
            Actor(), null, default));
        Assert.Empty(await context.Set<PlatformAuditLog>()
            .Where(x => x.Action == TenantDataAssetRegistryService.VerifyAction).ToListAsync());
    }

    [Fact]
    public async Task Registration_refuses_provider_urls_connection_strings_and_credentials()
    {
        using var database = new TestDb();
        await using var context = database.ContextFor(null);
        context.Set<Tenant>().Add(Tenant());
        await context.SaveChangesAsync();
        var registry = Registry(context);

        foreach (var unsafeReference in new[]
                 {
                     "postgresql://user:password@host/db",
                     "Host=db;Password=secret",
                     "project@example.test"
                 })
        {
            await Assert.ThrowsAsync<TenantDataAssetValidationException>(() => registry.RegisterAsync(
                TenantId, Registration() with { OpaqueProviderReference = unsafeReference },
                Actor(), null, default));
        }

        Assert.Empty(await context.Set<TenantDataAsset>().ToListAsync());
    }

    [Fact]
    public async Task Audit_failure_rolls_back_asset_registration()
    {
        using var database = new TestDb();
        await using (var seed = database.ContextFor(null))
        {
            seed.Set<Tenant>().Add(Tenant());
            await seed.SaveChangesAsync();
        }

        await using (var failing = database.ContextFor(null))
        {
            var registry = new TenantDataAssetRegistryService(failing, new ThrowingAudit());
            await Assert.ThrowsAsync<InvalidOperationException>(() => registry.RegisterAsync(
                TenantId, Registration(), Actor(), null, default));
        }

        await using var verification = database.ContextFor(null);
        Assert.Empty(await verification.Set<TenantDataAsset>().ToListAsync());
    }

    [Fact]
    public void Every_registry_endpoint_is_owner_only()
    {
        var attribute = Assert.Single(typeof(TenantDataAssetsController)
            .GetCustomAttributes<AuthorizeAttribute>());
        Assert.Equal(PlatformPolicies.Owner, attribute.Policy);
    }

    private static TenantDataAssetRegistryService Registry(ERP_RFQ_Automation.Models.ErpRfqAutomationContext context) =>
        new(context, new PlatformAuditService(context, NullLogger<PlatformAuditService>.Instance));

    private static Tenant Tenant() => new()
    {
        Id = TenantId,
        Name = "Asset registry tenant",
        Slug = "asset-registry-tenant",
        Status = TenantStatus.Provisioning,
        PrimaryBusinessUnitId = BusinessUnitId,
        DataRegion = "us-east-1"
    };

    private static RegisterTenantDataAssetRequest Registration() => new(
        TenantDataAssetRegistryService.PostgreSqlLogicalKey,
        "neon-project-83001:primary",
        "us-east-1",
        TenantDataAssetClassifications.CustomerData,
        TenantDataAssetDispositions.BackupRetainedUntilExpiryThenDestroy,
        "backup-policy:standard",
        3,
        "Register the tenant's primary PostgreSQL isolation boundary.");

    private static VerifyTenantDataAssetRequest Verification(long version) => new(
        version,
        BusinessUnitId,
        "us-east-1",
        "probe:postgresql-scope:83001:v1",
        new string('a', 64),
        "Owner reviewed the signed isolation probe evidence.");

    private static ClaimsPrincipal Actor() => new(new ClaimsIdentity(new[]
    {
        new Claim("sub", "7"),
        new Claim("email", "owner@nexora.test"),
        new Claim(PlatformAuthConstants.PlatformRoleClaim, nameof(PlatformRole.Owner))
    }, "test"));

    private sealed class ThrowingAudit : IPlatformAuditService
    {
        public Task WriteAsync(
            ClaimsPrincipal actor, string action, string? targetType = null, string? targetId = null,
            object? metadata = null, long? actAsTenantId = null, HttpContext? httpContext = null,
            CancellationToken ct = default) => throw new InvalidOperationException("audit unavailable");
    }
}
