using System.Reflection;
using ERP_RFQ_Automation.Authorization;
using ERP_RFQ_Automation.Controllers;

namespace ERP_RFQ_Automation.Tests;

public sealed class ProcurementControllerAuthorizationTests
{
    [Theory]
    [InlineData(nameof(ProcurementController.GetWorkbench), "RFQ Management", PermissionAction.View)]
    [InlineData(nameof(ProcurementController.SearchPurchaseOrders), "Orders", PermissionAction.View)]
    [InlineData(nameof(ProcurementController.CreatePurchaseOrder), "Orders", PermissionAction.Create)]
    [InlineData(nameof(ProcurementController.IssuePurchaseOrder), "Orders", PermissionAction.Edit)]
    [InlineData(nameof(ProcurementController.PostGoodsReceipt), "Orders", PermissionAction.Edit)]
    public void Procurement_routes_retain_explicit_module_permissions(
        string actionName, string module, PermissionAction action)
    {
        var method = typeof(ProcurementController).GetMethod(actionName)
            ?? throw new InvalidOperationException($"Missing action {actionName}.");
        var permissions = method.GetCustomAttributes<RequireModulePermissionAttribute>(true).ToArray();

        Assert.Contains(permissions, permission =>
            permission.ModuleName == module && permission.Action == action);
    }

    [Fact]
    public void Orders_view_does_not_authorize_the_sourcing_workbench()
    {
        var method = typeof(ProcurementController).GetMethod(nameof(ProcurementController.GetWorkbench))!;
        var permissions = method.GetCustomAttributes<RequireModulePermissionAttribute>(true).ToArray();

        Assert.Single(permissions);
        Assert.Equal("RFQ Management", permissions[0].ModuleName);
    }
}
