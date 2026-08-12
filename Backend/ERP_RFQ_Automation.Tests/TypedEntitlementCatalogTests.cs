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

    /// <summary>
    /// The activation control asks whether every key is PRESENT, not whether any is enabled — so a
    /// plan stored as "{}" parses cleanly, reads as all-off, and then blocks activation forever for
    /// every tenant on it. Plan "001 / Test" was created exactly that way and tenant 3 sat
    /// unactivatable behind it with provisioning fully succeeded.
    /// </summary>
    [Fact]
    public void Complete_fills_every_absent_key_with_false_so_a_plan_can_never_block_activation()
    {
        var completed = TypedEntitlementCatalog.Complete("{}");

        Assert.True(TypedEntitlementCatalog.TryParse(completed, out var values, out var error), error);
        // This is the exact predicate TenantActivationPolicyService evaluates.
        Assert.True(TypedEntitlementCatalog.Keys.All(values.ContainsKey));
        Assert.All(values, pair => Assert.False(pair.Value));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Complete_treats_an_absent_declaration_as_all_off_rather_than_as_nothing(string? json)
    {
        Assert.True(TypedEntitlementCatalog.TryParse(TypedEntitlementCatalog.Complete(json), out var values, out _));
        Assert.True(TypedEntitlementCatalog.Keys.All(values.ContainsKey));
    }

    /// <summary>
    /// The completion must never GRANT anything: a declared capability keeps its value, and the
    /// keys it adds are false. If this ever inverts, a plan that deliberately withheld a module
    /// would start handing it out on the next save.
    /// </summary>
    [Fact]
    public void Complete_preserves_declared_values_and_grants_nothing_new()
    {
        var completed = TypedEntitlementCatalog.Complete(
            $$"""{"{{TypedEntitlementCatalog.Rfq}}":true,"{{TypedEntitlementCatalog.Sso}}":false}""");

        Assert.True(TypedEntitlementCatalog.TryParse(completed, out var values, out _));
        Assert.True(values[TypedEntitlementCatalog.Rfq]);
        Assert.False(values[TypedEntitlementCatalog.Sso]);
        // Everything the caller did not mention is off.
        Assert.All(values.Where(pair => pair.Key != TypedEntitlementCatalog.Rfq),
            pair => Assert.False(pair.Value));
    }

    /// <summary>Idempotent — completing a completed declaration changes nothing.</summary>
    [Fact]
    public void Complete_is_idempotent()
    {
        var once = TypedEntitlementCatalog.Complete($$"""{"{{TypedEntitlementCatalog.Ai}}":true}""");
        Assert.Equal(once, TypedEntitlementCatalog.Complete(once));
    }

    private sealed class FixedAccess(TenantAccessSnapshot snapshot) : ITenantAccessService
    {
        public Task<TenantAccessSnapshot> GetAccessAsync(
            long businessUnitId, CancellationToken ct = default)
            => Task.FromResult(snapshot with { BusinessUnitId = businessUnitId });
    }
}
