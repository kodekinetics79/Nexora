using ERP_RFQ_Automation.Platform.Entitlements;
using ERP_RFQ_Automation.Platform.Models;

namespace ERP_RFQ_Automation.Tests;

public sealed class TypedEntitlementCatalogTests
{
    [Fact]
    public void Catalogue_rejects_unknown_and_non_boolean_values()
    {
        Assert.False(TypedEntitlementCatalog.TryParse("{\"future.magic\":true}", out _, out var unknown));
        Assert.Contains("Unknown entitlement", unknown);

        Assert.False(TypedEntitlementCatalog.TryParse(
            $"{{\"{TypedEntitlementCatalog.Api}\":17}}", out _, out var wrongType));
        Assert.Contains("must be true or false", wrongType);
    }

    [Fact]
    public async Task Feature_authority_is_default_deny_and_legacy_boundary_is_explicit()
    {
        const long businessUnitId = 44;
        var denied = new EntitlementService(
            new FixedAccess(new TenantAccessSnapshot(
                businessUnitId, 9, TenantStatus.Active,
                new PlanSnapshot(2, "paid", 1, 2, 100, 5, "{}"))),
            null!);

        var absent = await denied.CheckFeatureAsync(businessUnitId, TypedEntitlementCatalog.Exports);
        Assert.False(absent.Allowed);

        var enabled = new EntitlementService(
            new FixedAccess(new TenantAccessSnapshot(
                businessUnitId, 9, TenantStatus.Active,
                new PlanSnapshot(2, "paid", 1, 2, 100, 5,
                    $"{{\"{TypedEntitlementCatalog.Exports}\":true}}"))),
            null!);
        Assert.True((await enabled.CheckFeatureAsync(
            businessUnitId, TypedEntitlementCatalog.Exports)).Allowed);

        var legacy = new EntitlementService(
            new FixedAccess(new TenantAccessSnapshot(businessUnitId, null, null, null)), null!);
        Assert.True((await legacy.CheckFeatureAsync(
            businessUnitId, TypedEntitlementCatalog.Exports)).Allowed);
    }

    private sealed class FixedAccess(TenantAccessSnapshot snapshot) : ITenantAccessService
    {
        public Task<TenantAccessSnapshot> GetAccessAsync(
            long businessUnitId, CancellationToken ct = default)
            => Task.FromResult(snapshot with { BusinessUnitId = businessUnitId });
    }
}
