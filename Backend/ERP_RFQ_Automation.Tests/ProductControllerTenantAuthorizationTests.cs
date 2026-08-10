using System.Reflection;
using System.Security.Claims;
using ERP_RFQ_Automation.Authorization;
using ERP_RFQ_Automation.Controllers;
using ERP_RFQ_Automation.Interfaces;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace ERP_RFQ_Automation.Tests;

public sealed class ProductControllerTenantAuthorizationTests
{
    [Theory]
    [InlineData(nameof(ProductController.GetBusinessUnits))]
    [InlineData(nameof(ProductController.GetProductCategories))]
    [InlineData(nameof(ProductController.GetItemStatuses))]
    [InlineData(nameof(ProductController.GetSuppliers))]
    [InlineData(nameof(ProductController.GetProductSubCategories))]
    [InlineData(nameof(ProductController.GetWarehouses))]
    [InlineData(nameof(ProductController.GetUoms))]
    [InlineData(nameof(ProductController.MatchProduct))]
    public void Product_lookup_routes_require_product_view_permission(string action)
    {
        var attribute = Assert.Single(typeof(ProductController).GetMethods().Single(x => x.Name == action)
            .GetCustomAttributes<RequireModulePermissionAttribute>());
        Assert.Equal("Products", attribute.ModuleName);
        Assert.Equal(PermissionAction.View, attribute.Action);
    }

    [Fact]
    public async Task Query_tenant_cannot_replace_missing_authenticated_tenant_claim()
    {
        using var database = new TestDb();
        using var context = database.ContextFor(null);
        var repository = DispatchProxy.Create<IProductRepository, UnexpectedRepositoryCall>();
        var controller = new ProductController(repository, context, new StubMasterDataChangeHistoryReader())
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                        [new Claim(ClaimTypes.NameIdentifier, "test-user")], "test"))
                }
            }
        };

        var result = await controller.GetAll(businessUnitId: 999);

        Assert.IsType<ForbidResult>(result.Result);
    }

    [Fact]
    public async Task Business_unit_lookup_returns_only_authenticated_tenant()
    {
        using var database = new TestDb();
        using var context = database.ContextFor(null);
        Seed.EnsureBusinessUnit(context, 701);
        Seed.EnsureBusinessUnit(context, 702);
        context.SaveChanges();
        var repository = DispatchProxy.Create<IProductRepository, UnexpectedRepositoryCall>();
        var controller = new ProductController(repository, context, new StubMasterDataChangeHistoryReader())
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                        [new Claim("businessUnitId", "701"), new Claim(ClaimTypes.NameIdentifier, "test-user")], "test"))
                }
            }
        };

        var result = await controller.GetBusinessUnits();

        var rows = Assert.IsType<OkObjectResult>(result.Result).Value;
        var lookup = Assert.Single(Assert.IsAssignableFrom<IEnumerable<ERP_RFQ_Automation.DTOs.LookupDTOs.BusinessUnitLookupDTO>>(rows));
        Assert.Equal(701, lookup.Id);
    }

    private class UnexpectedRepositoryCall : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            throw new InvalidOperationException($"Repository method {targetMethod?.Name} must not run without a tenant claim.");
    }
}
