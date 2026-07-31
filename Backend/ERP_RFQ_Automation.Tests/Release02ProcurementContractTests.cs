using System.Reflection;
using ERP_RFQ_Automation.Authorization;
using ERP_RFQ_Automation.Controllers;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Procurement;
using ERP_RFQ_Automation.Tests.Support;

namespace ERP_RFQ_Automation.Tests;

public sealed class Release02ProcurementContractTests
{
    [Theory]
    [InlineData(nameof(ProcurementController.CreateOrOpenSourcingCase), "RFQ Management", PermissionAction.Edit)]
    [InlineData(nameof(ProcurementController.CreateOrOpenSourcingCase), "Supplier History", PermissionAction.View)]
    [InlineData(nameof(ProcurementController.GetSourcingCase), "Supplier History", PermissionAction.View)]
    [InlineData(nameof(ProcurementController.SearchSourcingCandidates), "Supplier History", PermissionAction.Edit)]
    [InlineData(nameof(ProcurementController.PrepareSupplierRfq), "RFQ Management", PermissionAction.Edit)]
    [InlineData(nameof(ProcurementController.PrepareSupplierRfq), "Supplier History", PermissionAction.Create)]
    [InlineData(nameof(ProcurementController.QueuePreparedSupplierRfq), "RFQ Management", PermissionAction.Edit)]
    [InlineData(nameof(ProcurementController.QueuePreparedSupplierRfq), "Supplier History", PermissionAction.Create)]
    public void Sourcing_routes_require_explicit_commercial_permissions(
        string actionName, string module, PermissionAction action)
    {
        var method = typeof(ProcurementController).GetMethod(actionName)
            ?? throw new InvalidOperationException($"Missing action {actionName}.");
        var permissions = method.GetCustomAttributes<RequireModulePermissionAttribute>(true).ToArray();

        Assert.Contains(permissions, permission =>
            permission.ModuleName == module && permission.Action == action);
    }

    [Fact]
    public void Sourcing_entities_have_tenant_filters_concurrency_and_qualified_relationships()
    {
        using var database = new TestDb();
        using var context = database.ContextFor(ProcurementTestData.Tenant);
        var model = context.Model;

        var demandLine = model.FindEntityType(typeof(CommercialDemandLine))!;
        var sourcingCase = model.FindEntityType(typeof(SourcingCase))!;
        var candidate = model.FindEntityType(typeof(SourcingCaseCandidate))!;
        Assert.NotNull(demandLine.GetQueryFilter());
        Assert.NotNull(sourcingCase.GetQueryFilter());
        Assert.NotNull(candidate.GetQueryFilter());
        Assert.True(sourcingCase.FindProperty(nameof(SourcingCase.Version))!.IsConcurrencyToken);

        Assert.Contains(sourcingCase.GetForeignKeys(), foreignKey =>
            foreignKey.PrincipalEntityType.ClrType == typeof(CommercialDemandLine)
            && foreignKey.Properties.Select(x => x.Name)
                .SequenceEqual([nameof(SourcingCase.BusinessUnitId), nameof(SourcingCase.CommercialDemandLineId)]));
        Assert.Contains(candidate.GetForeignKeys(), foreignKey =>
            foreignKey.PrincipalEntityType.ClrType == typeof(Supplier)
            && foreignKey.Properties.Select(x => x.Name)
                .SequenceEqual([nameof(SourcingCaseCandidate.SupplierId), nameof(SourcingCaseCandidate.BusinessUnitId)]));
    }
}
