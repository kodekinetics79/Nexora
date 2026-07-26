using ERP_RFQ_Automation.Authorization;
using ERP_RFQ_Automation.CommercialLearning;
using ERP_RFQ_Automation.Controllers;
using Microsoft.AspNetCore.Authorization;

namespace ERP_RFQ_Automation.Tests;

public sealed class Release02CommercialLearningTests
{
    [Theory]
    [InlineData(0, 0, false)]
    [InlineData(5, 1, false)]
    [InlineData(4, 4, false)]
    [InlineData(5, 2, true)]
    public void Stocking_recommendation_requires_decided_and_won_evidence(int decided, int won, bool expected) =>
        Assert.Equal(expected, CommercialLearningRules.CanRecommendStocking(decided, won));

    [Theory]
    [InlineData("PRICE", "COMMERCIAL_CONSTRAINT")]
    [InlineData("NO_STOCK", "COMMERCIAL_CONSTRAINT")]
    [InlineData("CUSTOMER_CANCELLED", "CUSTOMER_DECISION")]
    [InlineData("NO_RESPONSE", "CUSTOMER_DECISION")]
    [InlineData("INCORRECT_COMMITMENT", "EXECUTION_REVIEW")]
    public void Loss_attribution_does_not_blame_rep_for_external_constraints(string reason, string expected) =>
        Assert.Equal(expected, CommercialLearningRules.ClassifyLoss(reason));

    [Fact]
    public void Learning_endpoints_are_authenticated_and_permission_scoped()
    {
        var controller = typeof(CommercialLearningController);
        Assert.NotNull(controller.GetCustomAttributes(typeof(AuthorizeAttribute), true).SingleOrDefault());
        AssertPermissions(nameof(CommercialLearningController.Products), "Products", "Quotations");
        AssertPermissions(nameof(CommercialLearningController.Product), "Products", "Quotations");
        AssertPermission(nameof(CommercialLearningController.InventoryDemand), "Products");
        AssertPermissions(nameof(CommercialLearningController.Supplier), "Supplier History", "Quotations");
        AssertPermissions(nameof(CommercialLearningController.Suppliers), "Supplier History", "Quotations");
        AssertPermissions(nameof(CommercialLearningController.Customer), "Customers", "Quotations");
        AssertPermissions(nameof(CommercialLearningController.Customers), "Customers", "Quotations");
        AssertPermission(nameof(CommercialLearningController.SalesRep), "Dashboard");
        AssertPermission(nameof(CommercialLearningController.SalesReps), "Dashboard");
        AssertPermissions(nameof(CommercialLearningController.MemoryCard), "Products", "Quotations", "RFQ Management", "Supplier History");
        AssertPermission(nameof(CommercialLearningController.LearningStudio), "Dashboard");
    }

    private static void AssertPermission(string methodName, string module)
    {
        var attribute = Assert.Single(typeof(CommercialLearningController).GetMethod(methodName)!
            .GetCustomAttributes(typeof(RequireModulePermissionAttribute), true)
            .Cast<RequireModulePermissionAttribute>());
        Assert.Equal(module, attribute.ModuleName);
        Assert.Equal(PermissionAction.View, attribute.Action);
    }

    private static void AssertPermissions(string methodName, params string[] modules)
    {
        var attributes = typeof(CommercialLearningController).GetMethod(methodName)!
            .GetCustomAttributes(typeof(RequireModulePermissionAttribute), true)
            .Cast<RequireModulePermissionAttribute>().ToArray();
        Assert.Equal(modules.Order(), attributes.Select(x => x.ModuleName).Order());
        Assert.All(attributes, attribute => Assert.Equal(PermissionAction.View, attribute.Action));
    }
}
