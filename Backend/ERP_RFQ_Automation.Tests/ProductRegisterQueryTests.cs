using System.Security.Claims;
using ERP_RFQ_Automation.Controllers;
using ERP_RFQ_Automation.DTOs.ProductDTOs;
using ERP_RFQ_Automation.Inventory.Commercial;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Repositories;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace ERP_RFQ_Automation.Tests;

[Collection(PostgreSqlIntegrationCollection.Name)]
public sealed class ProductRegisterQueryTests(PostgreSqlTestDatabase database)
{
    [Fact]
    public async Task Register_filters_and_sorts_database_rows_before_paging_with_warehouse_scoped_quantities()
    {
        const long tenant = 998201, otherTenant = 998202, warehouse = 998211, secondWarehouse = 998212;
        await using var db = database.ContextFor(null);
        Seed.EnsureBusinessUnit(db, tenant);
        Seed.EnsureBusinessUnit(db, otherTenant);
        db.Warehouses.AddRange(Warehouse(warehouse, tenant), Warehouse(secondWarehouse, tenant), Warehouse(998213, otherTenant));
        for (var index = 1; index <= 5; index++)
        {
            db.Products.Add(new Product
            {
                Id = 998220 + index, Buid = tenant, PartNo = $"REGISTER-{index}", ProductName = index <= 2 ? "Same name" : $"Product {index}",
                UnitCost = index == 5 ? null : index * 10m, SellingPrice = index == 5 ? null : index * 20m,
                QtyOnHand = 999, IsActive = true, CreatedBy = "test", CreatedOn = DateTime.UtcNow,
            });
            if (index < 5) db.Set<ERP_RFQ_Automation.Models.Inventory>().Add(new()
            {
                Id = 998240 + index, Buid = tenant, ProductId = 998220 + index, WarehouseId = warehouse,
                PartNo = $"REGISTER-{index}", QtyOnHand = index == 1 ? 0 : index * 5m,
                QuarantineQuantity = index == 4 ? 18m : 0m,
                ReorderPoint = 8, MinimumLevel = 5, CreatedBy = "test", CreatedOn = DateTime.UtcNow,
            });
        }
        db.Set<ERP_RFQ_Automation.Models.Inventory>().Add(new()
        {
            Id = 998250, Buid = tenant, ProductId = 998221, WarehouseId = secondWarehouse,
            PartNo = "REGISTER-1", QtyOnHand = 30, SafetyStockQuantity = 2, CreatedBy = "test", CreatedOn = DateTime.UtcNow,
        });
        db.Products.Add(new Product { Id = 998231, Buid = otherTenant, PartNo = "REGISTER-FOREIGN", ProductName = "Other",
            IsActive = true, CreatedBy = "test", CreatedOn = DateTime.UtcNow });
        db.IncomingInventory.Add(new IncomingInventory
        {
            Id = 998260, BusinessUnitId = tenant, ProductId = 998224, InventoryId = 998244, WarehouseId = warehouse,
            OrderedQuantity = 20, ReceivedQuantity = 1, AllocatedQuantity = 1, ExpectedOn = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(1)),
            Status = IncomingInventoryStatus.Confirmed, SourceType = "Test", SourceId = "register-query",
        });
        await db.SaveChangesAsync();

        var controller = Controller(db, tenant);
        var first = Page(await controller.GetAll(pageSize: 1, sortBy: "onHand", sortDirection: "desc"));
        Assert.Equal(5, first.TotalItems);
        Assert.Equal(998221, Assert.Single(first.Items).Id);
        Assert.Equal(30, Assert.Single(first.Items).QtyOnHand);
        var second = Page(await controller.GetAll(pageNumber: 2, pageSize: 1, sortBy: "onHand", sortDirection: "desc"));
        Assert.Equal(998224, Assert.Single(second.Items).Id);
        var available = Page(await controller.GetAll(pageSize: 1, sortBy: "available", sortDirection: "asc"));
        Assert.Equal(998225, Assert.Single(available.Items).Id); // Product master 999 is not inventory.

        var inStock = Page(await controller.GetAll(pageSize: 1, stock: "in-stock", warehouseId: warehouse,
            sortBy: "onHand", sortDirection: "desc"));
        Assert.Equal(3, inStock.TotalItems);
        Assert.Equal(998224, Assert.Single(inStock.Items).Id);
        var outOfStock = Page(await controller.GetAll(stock: "out-of-stock", warehouseId: warehouse));
        Assert.Equal(998221, Assert.Single(outOfStock.Items).Id);
        Assert.Equal(0, Assert.Single(outOfStock.Items).QtyOnHand);
        Assert.Equal(0, Assert.Single(outOfStock.Items).AvailableQuantity);
        var low = Page(await controller.GetAll(stock: "low-stock", warehouseId: warehouse));
        Assert.Equal(998221, Assert.Single(low.Items).Id); // Incoming covers product 4's shortage.

        var otherWarehouse = Page(await controller.GetAll(warehouseId: secondWarehouse));
        var row = Assert.Single(otherWarehouse.Items);
        Assert.Equal(30, row.QtyOnHand);
        Assert.Equal(28, row.AvailableQuantity);
        Assert.Equal(1, row.WarehouseCount);

        foreach (var field in ProductListQuery.SortFields)
        {
            foreach (var direction in new[] { "asc", "desc" })
            {
                var sorted = Page(await controller.GetAll(sortBy: field, sortDirection: direction));
                Assert.Equal(5, sorted.TotalItems);
                Assert.DoesNotContain(sorted.Items, p => p.Buid != tenant);
                if (field is "landedCost" or "salePrice") Assert.Equal(998225, sorted.Items.Last().Id);
            }
        }
        var ties = Page(await controller.GetAll(search: "Same name", sortBy: "name", sortDirection: "desc"));
        Assert.Equal(new long[] { 998221, 998222 }, ties.Items.Select(p => p.Id));
        Assert.IsType<BadRequestObjectResult>((await controller.GetAll(warehouseId: 998213)).Result);
        Assert.Equal(1, Page(await Controller(db, otherTenant).GetAll()).TotalItems);
    }

    [Theory]
    [InlineData("unknown", "partNo", "asc", null)]
    [InlineData("all", "unknown", "asc", null)]
    [InlineData("all", "partNo", "sideways", null)]
    [InlineData("all", "partNo", "asc", 0L)]
    public async Task Invalid_query_returns_400(string stock, string sort, string direction, long? warehouse)
    {
        await using var db = database.ContextFor(null);
        var result = await Controller(db, 998201).GetAll(stock: stock, sortBy: sort, sortDirection: direction, warehouseId: warehouse);
        var problem = Assert.IsType<ProblemDetails>(Assert.IsType<BadRequestObjectResult>(result.Result).Value);
        Assert.Equal(400, problem.Status);
    }

    private static Warehouse Warehouse(long id, long tenant) => new()
    {
        Id = id, BusinessUnitId = tenant, WarehouseName = $"Warehouse {id}", WarehouseCode = $"W-{id}",
        IsActive = true, CreatedBy = "test", CreatedOn = DateTime.UtcNow,
    };

    private static ProductController Controller(ErpRfqAutomationContext db, long tenant) => new(
        new ProductRepository(db, null!, null!), db, new StubMasterDataChangeHistoryReader())
    {
        ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("businessUnitId", tenant.ToString())], "test")),
        } },
    };

    private static PaginatedProductResponseDTO Page(ActionResult<PaginatedProductResponseDTO> result) =>
        Assert.IsType<PaginatedProductResponseDTO>(Assert.IsType<OkObjectResult>(result.Result).Value);
}
