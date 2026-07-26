using ERP_RFQ_Automation.Authorization;
using ERP_RFQ_Automation.Controllers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ERP_RFQ_Automation.Tests;

public sealed class Release02SupplierQuoteAuthorizationTests
{
    [Fact]
    public void Inbox_controller_requires_authentication_and_module_permissions()
    {
        var controller = typeof(SupplierQuoteInboxController);
        Assert.NotNull(controller.GetCustomAttributes(typeof(AuthorizeAttribute), true).SingleOrDefault());

        AssertPermission(nameof(SupplierQuoteInboxController.Search), PermissionAction.View);
        AssertPermission(nameof(SupplierQuoteInboxController.Get), PermissionAction.View);
        AssertPermission(nameof(SupplierQuoteInboxController.Capture), PermissionAction.Create);
        AssertPermission(nameof(SupplierQuoteInboxController.Upload), PermissionAction.Create);
        AssertPermission(nameof(SupplierQuoteInboxController.Review), PermissionAction.Edit);
    }

    private static void AssertPermission(string methodName, PermissionAction action)
    {
        var method = typeof(SupplierQuoteInboxController).GetMethod(methodName)!;
        var permission = Assert.Single(method.GetCustomAttributes(typeof(RequireModulePermissionAttribute), true)
            .Cast<RequireModulePermissionAttribute>());
        Assert.Equal("Supplier History", permission.ModuleName);
        Assert.Equal(action, permission.Action);
        Assert.Contains(method.GetCustomAttributes(true), attribute =>
            attribute is HttpGetAttribute or HttpPostAttribute);
    }
}
