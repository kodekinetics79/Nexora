using System.Data.Common;
using System.Security.Claims;
using ERP_RFQ_Automation.Controllers;
using ERP_RFQ_Automation.DTOs;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Repositories;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// Production-dialect proof that two separately identified despatch commands cannot both consume
/// the same remaining order quantity. Idempotency cannot solve this race because both commands are
/// legitimate and carry different keys; the order row is the serialization boundary.
/// </summary>
[Collection(PostgreSqlIntegrationCollection.Name)]
public sealed class ShipmentCreationConcurrencyPostgreSqlTests(PostgreSqlTestDatabase database)
{
    private static long _nextTenant = 881_000_000;
    private static readonly DateTime Now = new(2026, 8, 29, 19, 0, 0, DateTimeKind.Utc);

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task Concurrent_distinct_despatches_cannot_exceed_the_order_quantity()
    {
        var fixture = await SeedAsync();
        var barrier = new InitialCeilingReadBarrier();
        await using var firstContext = ContextFor(fixture.Tenant, barrier);
        await using var secondContext = ContextFor(fixture.Tenant, barrier);

        var attempts = await Task.WhenAll(
            CreateAsync(Controller(firstContext, fixture.Tenant, "loader-one"), fixture, "shipment-concurrent-a"),
            CreateAsync(Controller(secondContext, fixture.Tenant, "loader-two"), fixture, "shipment-concurrent-b"));

        Assert.Single(attempts.Where(x => x is CreatedAtActionResult));
        var conflict = Assert.IsType<ConflictObjectResult>(Assert.Single(attempts.Where(x => x is ConflictObjectResult)));
        Assert.Contains("exceeds the remaining quantity", Message(conflict));

        await using var verify = database.ContextFor(fixture.Tenant);
        Assert.Single(await verify.Shipments.Where(x => x.BusinessUnitId == fixture.Tenant).ToListAsync());
        Assert.Equal(100m, await verify.ShipmentItems.Where(x => x.OrderItemId == fixture.OrderItem)
            .SumAsync(x => x.Quantity));
        Assert.Equal(100m, await verify.InventoryMovements.Where(x => x.InventoryId == fixture.Inventory)
            .SumAsync(x => x.Quantity));
        Assert.Equal(100m, await verify.Set<Models.Inventory>().Where(x => x.Id == fixture.Inventory)
            .Select(x => x.QtyOnHand).SingleAsync());
    }

    private async Task<Fixture> SeedAsync()
    {
        var tenant = Interlocked.Add(ref _nextTenant, 100);
        var openStatus = tenant + 1;
        var shippedStatus = tenant + 2;
        var shipmentStatus = tenant + 3;
        var order = tenant + 10;
        var orderItem = tenant + 11;
        var inventory = tenant + 12;

        await using var seed = database.ContextFor(null);
        Seed.EnsureBusinessUnit(seed, tenant);
        Seed.Customer(seed, tenant, tenant, $"Concurrent despatch customer {tenant}");
        seed.SetupMasters.AddRange(
            Status(openStatus, tenant, "OrderStatus", "CONFIRMED"),
            Status(shippedStatus, tenant, "OrderStatus", "SHIPPED"),
            Status(shipmentStatus, tenant, "ShipmentStatus", "READY"));
        seed.Warehouses.Add(new Warehouse
        {
            Id = tenant, BusinessUnitId = tenant, WarehouseCode = $"WH{tenant}",
            WarehouseName = $"Warehouse {tenant}", IsActive = true, CreatedBy = "qa", CreatedOn = Now
        });
        seed.Products.Add(new Product
        {
            Id = tenant, Buid = tenant, PartNo = $"PART{tenant}", ProductName = "Concurrency product",
            WarehouseId = tenant, QtyOnHand = 200m, ReorderPoint = 0m, IsActive = true,
            CreatedBy = "qa", CreatedOn = Now
        });
        seed.Set<Models.Inventory>().Add(new Models.Inventory
        {
            Id = inventory, Buid = tenant, ProductId = tenant, WarehouseId = tenant,
            PartNo = $"PART{tenant}", ProductName = "Concurrency product", QtyOnHand = 200m,
            ReorderPoint = 0m, CreatedBy = "qa", CreatedOn = Now
        });
        seed.Orders.Add(new Order
        {
            Id = order, OrderNo = $"SO-{order}", CustomerId = tenant, BusinessUnitId = tenant,
            StatusId = openStatus, TotalAmount = 1_000m, OrderDate = Now,
            CreatedBy = "qa", CreatedOn = Now, IsActive = true
        });
        seed.OrderItems.Add(new OrderItem
        {
            Id = orderItem, OrderId = order, ProductId = tenant, WarehouseId = tenant,
            Quantity = 100m, UnitPrice = 10m, TotalAmount = 1_000m,
            CreatedBy = "qa", CreatedDate = Now, IsActive = true
        });
        await seed.SaveChangesAsync();
        return new Fixture(tenant, order, orderItem, inventory, shipmentStatus);
    }

    private ErpRfqAutomationContext ContextFor(long tenant, IInterceptor interceptor)
    {
        var options = new DbContextOptionsBuilder<ErpRfqAutomationContext>()
            .UseNpgsql(database.ConnectionString)
            .EnableDetailedErrors()
            .AddInterceptors(interceptor)
            .Options;
        return new ErpRfqAutomationContext(options, new StubTenant(tenant));
    }

    private static ShipmentController Controller(ErpRfqAutomationContext context, long tenant, string actor)
        => new(new ShipmentRepository(context), context, InventoryServices.OrderStock(context))
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                    [
                        new Claim("businessUnitId", tenant.ToString()),
                        new Claim("email", actor)
                    ], "test"))
                }
            }
        };

    private static Task<IActionResult> CreateAsync(ShipmentController controller, Fixture fixture, string key)
    {
        controller.ControllerContext.HttpContext.Request.Headers["Idempotency-Key"] = key;
        return controller.CreateShipment(new CreateShipmentDto
        {
            OrderId = fixture.Order,
            BusinessUnitId = fixture.Tenant,
            StatusId = fixture.ShipmentStatus,
            ShipmentDate = Now,
            Items = [new CreateShipmentItemDto { OrderItemId = fixture.OrderItem, Quantity = 100m }]
        });
    }

    private static SetupMaster Status(long id, long tenant, string type, string code) => new()
    {
        SetupId = id, SetupType = type, SetupCode = code, SetupValue = code,
        BusinessUnitId = tenant, IsActive = true, CreatedBy = "qa", CreatedOn = Now
    };

    private static string Message(ObjectResult result)
        => result.Value?.GetType().GetProperty("message")?.GetValue(result.Value)?.ToString() ?? "";

    private sealed class InitialCeilingReadBarrier : DbCommandInterceptor
    {
        private readonly TaskCompletionSource _bothReaders =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _arrived;

        public override async ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command, CommandExecutedEventData eventData, DbDataReader result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("ShipmentItems", StringComparison.OrdinalIgnoreCase)
                && command.CommandText.Contains("DeliveryStatus", StringComparison.OrdinalIgnoreCase)
                && Interlocked.Increment(ref _arrived) <= 2)
            {
                if (Volatile.Read(ref _arrived) == 2) _bothReaders.TrySetResult();
                await _bothReaders.Task.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken);
            }
            return result;
        }
    }

    private sealed record Fixture(
        long Tenant, long Order, long OrderItem, long Inventory, long ShipmentStatus);
}
