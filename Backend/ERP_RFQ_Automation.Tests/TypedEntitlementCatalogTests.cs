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
    public async Task Feature_authority_is_default_deny_including_ungoverned_legacy_boundary()
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
        Assert.False((await legacy.CheckFeatureAsync(
            businessUnitId, TypedEntitlementCatalog.Exports)).Allowed);
    }

    [Theory]
    [InlineData(TypedEntitlementCatalog.Api)]
    [InlineData(TypedEntitlementCatalog.Automation)]
    [InlineData(TypedEntitlementCatalog.Sso)]
    [InlineData(TypedEntitlementCatalog.Scim)]
    [InlineData(TypedEntitlementCatalog.DedicatedResources)]
    public async Task Unimplemented_runtime_capabilities_deny_even_when_plan_flag_is_true(string key)
    {
        var service = new EntitlementService(
            new FixedAccess(new TenantAccessSnapshot(
                44, 9, TenantStatus.Active,
                new PlanSnapshot(2, "future", 1, 2, 100, 5, $"{{\"{key}\":true}}"))),
            null!);

        var decision = await service.CheckFeatureAsync(44, key);

        Assert.False(decision.Allowed);
        Assert.Contains("execution boundary is not implemented", decision.Reason);
    }

    [Fact]
    public void Declarations_reject_unknown_keys_at_construction_time()
    {
        Assert.Equal(TypedEntitlementCatalog.Rfq,
            TypedEntitlementCatalog.RequireKnown(TypedEntitlementCatalog.Rfq));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            TypedEntitlementCatalog.RequireKnown("future.unreviewed"));
    }

    [Fact]
    public void Real_domain_controllers_declare_their_server_entitlement_boundary()
    {
        var expected = new Dictionary<Type, string>
        {
            [typeof(ERP_RFQ_Automation.Controllers.RfqController)] = TypedEntitlementCatalog.Rfq,
            [typeof(ERP_RFQ_Automation.Controllers.QuoteController)] = TypedEntitlementCatalog.Quotes,
            [typeof(ERP_RFQ_Automation.Controllers.OrderController)] = TypedEntitlementCatalog.Orders,
            [typeof(ERP_RFQ_Automation.Controllers.ProcurementController)] = TypedEntitlementCatalog.Procurement,
            [typeof(ERP_RFQ_Automation.Controllers.InventoryIntelligenceController)] = TypedEntitlementCatalog.Inventory,
            [typeof(ERP_RFQ_Automation.Controllers.AgentController)] = TypedEntitlementCatalog.Ai,
            [typeof(ERP_RFQ_Automation.Controllers.ProcurementIntegrationController)] = TypedEntitlementCatalog.Integrations,
            [typeof(ERP_RFQ_Automation.Controllers.EmailTriageController)] = TypedEntitlementCatalog.EmailIntake,
            [typeof(ERP_RFQ_Automation.Controllers.MailboxController)] = TypedEntitlementCatalog.EmailIntake,
            [typeof(ERP_RFQ_Automation.Controllers.ExtractionController)] = TypedEntitlementCatalog.Rfq
        };

        foreach (var (controller, key) in expected)
        {
            var declaration = Assert.Single(controller.GetCustomAttributes(
                typeof(RequiresEntitlementAttribute), inherit: true).Cast<RequiresEntitlementAttribute>());
            Assert.Equal(key, declaration.Key);
        }

        var supplierSearch = typeof(ERP_RFQ_Automation.Controllers.ProcurementController)
            .GetMethod(nameof(ERP_RFQ_Automation.Controllers.ProcurementController.SearchSourcingCandidates))!;
        Assert.Contains(supplierSearch.GetCustomAttributes(typeof(RequiresEntitlementAttribute), true)
                .Cast<RequiresEntitlementAttribute>(),
            x => x.Key == TypedEntitlementCatalog.SupplierSearch);

        var exports = new[]
        {
            typeof(ERP_RFQ_Automation.Controllers.CustomerUploaderController).GetMethod("ExportData")!,
            typeof(ERP_RFQ_Automation.Controllers.SupplierUploaderController).GetMethod("ExportData")!,
            typeof(ERP_RFQ_Automation.Controllers.ProductUploaderController).GetMethod("ExportProducts")!,
            typeof(ERP_RFQ_Automation.Controllers.ProductCategoryUploaderController).GetMethod("ExportCategoryData")!,
            typeof(ERP_RFQ_Automation.Controllers.ProductCategoryUploaderController).GetMethod("ExportSubCategoryData")!,
            typeof(ERP_RFQ_Automation.Controllers.BoqController).GetMethod("ExportCsv")!
        };
        foreach (var export in exports)
        {
            Assert.Contains(export.GetCustomAttributes(typeof(RequiresEntitlementAttribute), true)
                    .Cast<RequiresEntitlementAttribute>(),
                x => x.Key == TypedEntitlementCatalog.Exports);
        }

        Assert.Empty(EntitlementEnforcementCoverage.Missing);
        Assert.Equal(TypedEntitlementCatalog.Keys.Order(),
            EntitlementEnforcementCoverage.Enforced.Keys.Order());
        Assert.Equal(TypedEntitlementCatalog.Keys.Order(),
            TypedEntitlementCatalog.RuntimeAvailableKeys
                .Concat(TypedEntitlementCatalog.Keys.Except(TypedEntitlementCatalog.RuntimeAvailableKeys))
                .Order());
    }

    private sealed class FixedAccess(TenantAccessSnapshot snapshot) : ITenantAccessService
    {
        public Task<TenantAccessSnapshot> GetAccessAsync(
            long businessUnitId, CancellationToken ct = default)
            => Task.FromResult(snapshot with { BusinessUnitId = businessUnitId });
    }
}
