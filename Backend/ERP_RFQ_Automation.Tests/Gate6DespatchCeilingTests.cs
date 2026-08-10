using System.Security.Claims;
using ERP_RFQ_Automation.Controllers;
using ERP_RFQ_Automation.DTOs;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Repositories;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// Gate 6 — the two defects the despatch door was carrying, at the layer that carried them.
///
/// <para>Both were invisible to the existing suite because both were <b>absences</b>: a ceiling
/// that existed only in a browser input, and a return value the controller awaited and discarded.
/// Neither can be caught by testing that a shipment saves — a shipment that over-ships saves
/// perfectly. The assertions are therefore on the HTTP result and on what did NOT reach the
/// database.</para>
/// </summary>
public sealed class Gate6DespatchCeilingTests
{
    private const long Tenant = 97_100;
    private const long CustomerId = 97_110;
    private const long WarehouseId = 97_120;
    private const long ProductId = 97_130;
    private const long InventoryId = 97_140;
    private const long OrderId = 97_150;
    private const long OrderItemId = 97_160;
    private const long OrderStatusId = 97_170;
    private const long ShipmentStatusId = 97_171;

    [Fact]
    public async Task Over_shipping_a_line_in_one_despatch_is_refused_by_the_server()
    {
        using var db = Seeded(onHand: 500m, orderedQuantity: 100m);

        // The browser capped this at the ordered quantity. Nothing else did: the server rejected
        // only Quantity <= 0, so 150 against an order for 100 was accepted, written and issued —
        // and could then never be invoiced, because the INVOICE ceiling is enforced. Stock gone,
        // revenue unbillable, order looking clean on every screen.
        var result = await CreateShipmentAsync(db, 150m);

        var conflict = Assert.IsType<ConflictObjectResult>(result);
        Assert.Contains("exceeds the remaining quantity", Message(conflict));

        await using var verify = db.ContextFor(Tenant);
        Assert.Empty(await verify.Shipments.ToListAsync());
        Assert.Equal(500m, await OnHandAsync(verify));
    }

    [Fact]
    public async Task The_ceiling_is_cumulative_across_despatches_exactly_as_the_invoice_ceiling_is()
    {
        using var db = Seeded(onHand: 500m, orderedQuantity: 100m);

        Assert.IsType<CreatedAtActionResult>(await CreateShipmentAsync(db, 60m));
        Assert.IsType<CreatedAtActionResult>(await CreateShipmentAsync(db, 40m));

        // A per-request check would let this through: 30 is well under 100. Cumulatively the order
        // is already fully despatched, which is the whole reason the invoice check counts prior
        // invoices rather than just the one in front of it.
        var third = Assert.IsType<ConflictObjectResult>(await CreateShipmentAsync(db, 30m));
        var message = Message(third);
        Assert.Contains("exceeds the remaining quantity", message);
        // The refusal names the arithmetic rather than saying "invalid quantity": the operator has
        // to be able to see that the line is already fully despatched without opening another screen.
        Assert.Contains("already shipped", message);
        Assert.Contains($"{100m} ordered", message);
        Assert.Contains($"{100m} already shipped", message);
        Assert.Contains($"{30m} declared now", message);

        await using var verify = db.ContextFor(Tenant);
        Assert.Equal(2, await verify.Shipments.CountAsync());
        Assert.Equal(400m, await OnHandAsync(verify));
    }

    [Fact]
    public async Task A_despatch_that_cannot_issue_the_goods_is_refused_rather_than_recorded()
    {
        // The books hold 10 but the whole balance has been written off as damaged, so nothing is
        // promisable and nothing can be issued. The shipment used to be created anyway: allocation
        // reported a shortage instead of throwing, the consume moved nothing, both results were
        // discarded, and the order was then marked SHIPPED because completion counted shipment
        // LINES rather than issued QUANTITY.
        using var db = Seeded(onHand: 10m, orderedQuantity: 10m, damaged: 10m);

        var result = await CreateShipmentAsync(db, 10m);

        var conflict = Assert.IsType<ConflictObjectResult>(result);
        Assert.Contains("could not move the declared quantity", Message(conflict));

        await using var verify = db.ContextFor(Tenant);
        // One transaction: no despatch note, no status transition, no stock movement.
        Assert.Empty(await verify.Shipments.ToListAsync());
        Assert.Empty(await verify.InventoryMovements.ToListAsync());
        Assert.Equal(10m, await OnHandAsync(verify));
        Assert.Equal(OrderStatusId,
            await verify.Orders.Where(x => x.Id == OrderId).Select(x => x.StatusId).SingleAsync());
    }

    [Fact]
    public async Task A_despatch_within_the_ceiling_still_issues_and_still_closes_the_order()
    {
        using var db = Seeded(onHand: 500m, orderedQuantity: 100m);

        Assert.IsType<CreatedAtActionResult>(await CreateShipmentAsync(db, 100m));

        await using var verify = db.ContextFor(Tenant);
        Assert.Equal(400m, await OnHandAsync(verify));
        Assert.Equal(ShipmentStatusId,
            await verify.Orders.Where(x => x.Id == OrderId).Select(x => x.StatusId).SingleAsync());
    }

    // ------------------------------------------------------------------------------------------

    private static string Message(ObjectResult result)
        => result.Value?.GetType().GetProperty("message")?.GetValue(result.Value)?.ToString() ?? "";

    private static Task<decimal> OnHandAsync(ErpRfqAutomationContext context)
        => context.Set<ERP_RFQ_Automation.Models.Inventory>()
            .Where(x => x.Id == InventoryId).Select(x => x.QtyOnHand).SingleAsync();

    private static async Task<IActionResult> CreateShipmentAsync(TestDb db, decimal quantity)
    {
        await using var context = db.ContextFor(Tenant);
        var controller = new ShipmentController(
            new ShipmentRepository(context), context, InventoryServices.OrderStock(context))
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                    [
                        new Claim("businessUnitId", Tenant.ToString()),
                        new Claim("email", "despatch@qa"),
                    ], "test")),
                },
            },
        };

        return await controller.CreateShipment(new CreateShipmentDto
        {
            OrderId = OrderId,
            BusinessUnitId = Tenant,
            StatusId = ShipmentStatusId,
            ShipmentDate = DateTime.UtcNow,
            Items = [new CreateShipmentItemDto { OrderItemId = OrderItemId, Quantity = quantity }],
        });
    }

    private static TestDb Seeded(decimal onHand, decimal orderedQuantity, decimal damaged = 0m)
    {
        var db = new TestDb();
        using var context = db.ContextFor(null);
        Seed.EnsureBusinessUnit(context, Tenant);
        Seed.Customer(context, CustomerId, Tenant, "Despatch QA");
        context.SetupMasters.Add(new SetupMaster
        {
            SetupId = OrderStatusId, SetupType = "OrderStatus", SetupCode = "OPEN", SetupValue = "OPEN",
            BusinessUnitId = Tenant, IsActive = true, CreatedBy = "qa", CreatedOn = DateTime.UtcNow,
        });
        context.SetupMasters.Add(new SetupMaster
        {
            SetupId = ShipmentStatusId, SetupType = "OrderStatus", SetupCode = "SHIPPED",
            SetupValue = "SHIPPED", BusinessUnitId = Tenant, IsActive = true, CreatedBy = "qa",
            CreatedOn = DateTime.UtcNow,
        });
        context.Warehouses.Add(new Warehouse
        {
            Id = WarehouseId, BusinessUnitId = Tenant, WarehouseCode = "DSP", WarehouseName = "Despatch",
            IsActive = true, CreatedBy = "qa", CreatedOn = DateTime.UtcNow,
        });
        context.SaveChanges();

        context.Products.Add(new Product
        {
            Id = ProductId, Buid = Tenant, PartNo = "DSP-1", ProductName = "Despatch widget",
            WarehouseId = WarehouseId, CreatedBy = "qa", CreatedOn = DateTime.UtcNow, IsActive = true,
        });
        context.Set<ERP_RFQ_Automation.Models.Inventory>().Add(new ERP_RFQ_Automation.Models.Inventory
        {
            Id = InventoryId, Buid = Tenant, ProductId = ProductId, WarehouseId = WarehouseId,
            PartNo = "DSP-1", ProductName = "Despatch widget", QtyOnHand = onHand, ReorderPoint = 0m,
            DamagedQuantity = damaged, CreatedBy = "qa", CreatedOn = DateTime.UtcNow,
        });
        context.Set<Order>().Add(new Order
        {
            Id = OrderId, OrderNo = "SO-DSP-1", CustomerId = CustomerId, BusinessUnitId = Tenant,
            StatusId = OrderStatusId, TotalAmount = orderedQuantity * 10m, OrderDate = DateTime.UtcNow,
            CreatedBy = "qa", CreatedOn = DateTime.UtcNow, IsActive = true,
        });
        context.Set<OrderItem>().Add(new OrderItem
        {
            Id = OrderItemId, OrderId = OrderId, ProductId = ProductId, WarehouseId = WarehouseId,
            Quantity = orderedQuantity, UnitPrice = 10m, Discount = 0m, TaxAmount = 0m,
            TotalAmount = orderedQuantity * 10m, CreatedBy = "qa", CreatedDate = DateTime.UtcNow,
            IsActive = true,
        });
        context.SaveChanges();
        return db;
    }
}
