using System.Security.Claims;
using System.Text.Json;
using ERP_RFQ_Automation.Controllers;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace ERP_RFQ_Automation.Tests;

public sealed class ProductStockAvailabilityReadTests
{
    [Fact]
    public async Task Product_filter_runs_before_row_cap_and_keeps_tenant_isolation()
    {
        using var database = new TestDb();
        await using (var seed = database.ContextFor(null))
        {
            Seed.EnsureBusinessUnit(seed, 1);
            Seed.EnsureBusinessUnit(seed, 2);
            seed.Warehouses.Add(new Warehouse
            {
                Id = 20, BusinessUnitId = 1, WarehouseCode = "MAIN", WarehouseName = "Main",
                IsActive = true, CreatedBy = "test", CreatedOn = DateTime.UtcNow,
            });
            // The requested product occurs after more than one complete unfiltered result page.
            for (var id = 1; id <= 502; id++)
            {
                seed.Products.Add(new Product
                {
                    Id = id, Buid = 1, PartNo = $"PART-{id}", ProductName = "Actuator",
                    IsActive = true, CreatedBy = "test", CreatedOn = DateTime.UtcNow,
                });
                seed.Set<ERP_RFQ_Automation.Models.Inventory>().Add(new ERP_RFQ_Automation.Models.Inventory
                {
                    Id = id, Buid = 1, ProductId = id, WarehouseId = 20, PartNo = $"PART-{id}",
                    QtyOnHand = 40, QuarantineQuantity = 3, SafetyStockQuantity = 2,
                    CreatedBy = "test", CreatedOn = DateTime.UtcNow,
                });
            }
            await seed.SaveChangesAsync();
        }

        await using var context = database.ContextFor(1);
        var all = await Read(Controller(context, 1), null);
        Assert.Equal(500, all.Length);
        var rows = await Read(Controller(context, 1), 502);
        var row = Assert.Single(rows);
        Assert.Equal(502, row.GetProperty("ProductId").GetInt64());
        Assert.Equal(40, row.GetProperty("OnHand").GetDecimal());
        Assert.Equal(35, row.GetProperty("Available").GetDecimal());
        Assert.Empty(await Read(Controller(context, 2), 502));
        Assert.Empty(await Read(Controller(context, 1), 9999));
    }

    private static async Task<JsonElement[]> Read(InventoryIntelligenceController controller, long? productId)
    {
        var response = Assert.IsType<OkObjectResult>(await controller.Availability(null, null, default, productId));
        return JsonSerializer.SerializeToElement(response.Value).EnumerateArray().ToArray();
    }

    private static InventoryIntelligenceController Controller(ErpRfqAutomationContext db, long tenant)
    {
        var controller = new InventoryIntelligenceController(db, null!, null!, null!, null!, null!);
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("businessUnitId", tenant.ToString())], "test")),
        } };
        return controller;
    }
}
