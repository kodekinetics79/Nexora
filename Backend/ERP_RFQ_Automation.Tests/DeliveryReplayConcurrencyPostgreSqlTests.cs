using System.Data.Common;
using ERP_RFQ_Automation.Delivery;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// Production-dialect proof that the delivery service interprets the database's two POD replay
/// invariants after a real race. The barrier makes both commands observe no proof before either
/// may insert; a sequential retry test cannot reach the unique-violation recovery path.
/// </summary>
[Collection(PostgreSqlIntegrationCollection.Name)]
public sealed class DeliveryReplayConcurrencyPostgreSqlTests(PostgreSqlTestDatabase database)
{
    private static long _nextTenant = 880_000_000;
    private static readonly DateTime ReceivedOn =
        new(2026, 8, 29, 18, 30, 0, DateTimeKind.Utc);

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task Concurrent_exact_confirmation_returns_one_proof_to_both_callers()
    {
        var fixture = await SeedAsync();
        var barrier = new DeliveryReplayReadBarrier();
        await using var firstContext = ContextFor(fixture.Tenant, barrier);
        await using var secondContext = ContextFor(fixture.Tenant, barrier);
        var first = Service(firstContext);
        var second = Service(secondContext);
        var command = Command(fixture.ShipmentItem, "Receiving clerk");

        var results = await Task.WhenAll(
            first.ConfirmAsync(fixture.Tenant, fixture.Shipment, "pod-concurrent-exact",
                command, "driver-one"),
            second.ConfirmAsync(fixture.Tenant, fixture.Shipment, "pod-concurrent-exact",
                command, "driver-two"));

        Assert.Equal(results[0].Id, results[1].Id);
        Assert.All(results, result => Assert.Equal(DeliveryStatuses.Delivered, result.Outcome));

        await using var verify = database.ContextFor(fixture.Tenant);
        Assert.Single(await verify.DeliveryProofs.Where(x => x.BusinessUnitId == fixture.Tenant).ToListAsync());
        Assert.Single(await verify.DeliveryProofLines.Where(x => x.BusinessUnitId == fixture.Tenant).ToListAsync());
        Assert.Single(await verify.ShipmentStatusHistories.Where(x => x.ShipmentId == fixture.Shipment).ToListAsync());
        Assert.Equal(fixture.DeliveredStatus, await verify.Orders.Where(x => x.Id == fixture.Order)
            .Select(x => x.StatusId).SingleAsync());
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task Concurrent_different_confirmation_on_one_shipment_is_a_governed_conflict()
    {
        var fixture = await SeedAsync();
        var barrier = new DeliveryReplayReadBarrier();
        await using var firstContext = ContextFor(fixture.Tenant, barrier);
        await using var secondContext = ContextFor(fixture.Tenant, barrier);

        var attempts = await Task.WhenAll(
            CaptureAsync(Service(firstContext), fixture.Tenant, fixture.Shipment, "pod-concurrent-a",
                Command(fixture.ShipmentItem, "Receiver A")),
            CaptureAsync(Service(secondContext), fixture.Tenant, fixture.Shipment, "pod-concurrent-b",
                Command(fixture.ShipmentItem, "Receiver B")));

        Assert.Single(attempts, x => x.View is not null);
        var conflict = Assert.Single(attempts, x => x.Error is not null).Error;
        var governed = Assert.IsType<DeliveryConflictException>(conflict);
        Assert.Contains("different delivery confirmation command", governed.Message,
            StringComparison.OrdinalIgnoreCase);

        await using var verify = database.ContextFor(fixture.Tenant);
        Assert.Single(await verify.DeliveryProofs.Where(x => x.BusinessUnitId == fixture.Tenant).ToListAsync());
        Assert.Single(await verify.DeliveryProofLines.Where(x => x.BusinessUnitId == fixture.Tenant).ToListAsync());
        Assert.Single(await verify.ShipmentStatusHistories.Where(x => x.ShipmentId == fixture.Shipment).ToListAsync());
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task Concurrent_reuse_of_one_key_for_different_shipments_is_a_governed_conflict()
    {
        var fixture = await SeedAsync();
        var barrier = new DeliveryReplayReadBarrier();
        await using var firstContext = ContextFor(fixture.Tenant, barrier);
        await using var secondContext = ContextFor(fixture.Tenant, barrier);

        var attempts = await Task.WhenAll(
            CaptureAsync(Service(firstContext), fixture.Tenant, fixture.Shipment, "pod-shared-key",
                Command(fixture.ShipmentItem, "Receiver A")),
            CaptureAsync(Service(secondContext), fixture.Tenant, fixture.SecondShipment, "pod-shared-key",
                Command(fixture.SecondShipmentItem, "Receiver B")));

        Assert.Single(attempts, x => x.View is not null);
        var conflict = Assert.Single(attempts, x => x.Error is not null).Error;
        var governed = Assert.IsType<DeliveryConflictException>(conflict);
        Assert.Contains("different shipment", governed.Message, StringComparison.OrdinalIgnoreCase);

        await using var verify = database.ContextFor(fixture.Tenant);
        Assert.Single(await verify.DeliveryProofs.Where(x => x.BusinessUnitId == fixture.Tenant).ToListAsync());
        Assert.Single(await verify.DeliveryProofLines.Where(x => x.BusinessUnitId == fixture.Tenant).ToListAsync());
    }

    private async Task<Fixture> SeedAsync()
    {
        // Several globally keyed child rows derive their ids from this value; reserve a block per
        // fixture so two tests in the shared PostgreSQL collection cannot overlap those ids.
        var tenant = Interlocked.Add(ref _nextTenant, 100);
        var confirmedStatus = tenant + 1;
        var deliveredStatus = tenant + 2;
        var shipmentStatus = tenant + 3;
        var order = tenant + 10;
        var orderItem = tenant + 11;
        var shipment = tenant + 20;
        var shipmentItem = tenant + 21;
        var secondShipment = tenant + 30;
        var secondShipmentItem = tenant + 31;

        await using var seed = database.ContextFor(null);
        Seed.EnsureBusinessUnit(seed, tenant);
        Seed.Customer(seed, tenant, tenant, $"Concurrent delivery customer {tenant}");
        seed.SetupMasters.AddRange(
            new SetupMaster
            {
                SetupId = confirmedStatus, SetupType = "OrderStatus", SetupCode = "CONFIRMED",
                SetupValue = "CONFIRMED", BusinessUnitId = tenant, IsActive = true,
                CreatedBy = "qa", CreatedOn = ReceivedOn
            },
            new SetupMaster
            {
                SetupId = deliveredStatus, SetupType = "OrderStatus", SetupCode = "DELIVERED",
                SetupValue = "DELIVERED", BusinessUnitId = tenant, IsActive = true,
                CreatedBy = "qa", CreatedOn = ReceivedOn
            },
            new SetupMaster
            {
                SetupId = shipmentStatus, SetupType = "ShipmentStatus", SetupCode = "OPEN",
                SetupValue = "Open", BusinessUnitId = tenant, IsActive = true,
                CreatedBy = "qa", CreatedOn = ReceivedOn
            });
        seed.Warehouses.Add(new Warehouse
        {
            Id = tenant, BusinessUnitId = tenant, WarehouseCode = $"WH{tenant}",
            WarehouseName = $"Warehouse {tenant}", IsActive = true,
            CreatedBy = "qa", CreatedOn = ReceivedOn
        });
        seed.Products.Add(new Product
        {
            Id = tenant, Buid = tenant, PartNo = $"PART{tenant}", ProductName = "Replay test product",
            WarehouseId = tenant, QtyOnHand = 0m, ReorderPoint = 0m, IsActive = true,
            CreatedBy = "qa", CreatedOn = ReceivedOn
        });
        seed.Orders.Add(new Order
        {
            Id = order, OrderNo = $"SO-{order}", CustomerId = tenant, BusinessUnitId = tenant,
            StatusId = confirmedStatus, TotalAmount = 400m, OrderDate = ReceivedOn,
            CreatedBy = "qa", CreatedOn = ReceivedOn, IsActive = true
        });
        seed.OrderItems.Add(new OrderItem
        {
            Id = orderItem, OrderId = order, ProductId = tenant, WarehouseId = tenant,
            Quantity = 4m, UnitPrice = 100m, TotalAmount = 400m,
            CreatedBy = "qa", CreatedDate = ReceivedOn, IsActive = true
        });
        seed.Shipments.AddRange(
            new Shipment
            {
                Id = shipment, ShipmentNo = $"DN-{shipment}", OrderId = order,
                BusinessUnitId = tenant, StatusId = shipmentStatus, ShipmentDate = ReceivedOn,
                DeliveryStatus = DeliveryStatuses.Dispatched, DeliveryStatusChangedBy = "qa",
                DeliveryStatusChangedOn = ReceivedOn, CreatedBy = "qa", CreatedOn = ReceivedOn,
                IsActive = true
            },
            new Shipment
            {
                Id = secondShipment, ShipmentNo = $"DN-{secondShipment}", OrderId = order,
                BusinessUnitId = tenant, StatusId = shipmentStatus, ShipmentDate = ReceivedOn,
                DeliveryStatus = DeliveryStatuses.Dispatched, DeliveryStatusChangedBy = "qa",
                DeliveryStatusChangedOn = ReceivedOn, CreatedBy = "qa", CreatedOn = ReceivedOn,
                IsActive = true
            });
        seed.ShipmentItems.AddRange(
            new ShipmentItem
            {
                Id = shipmentItem, ShipmentId = shipment, OrderItemId = orderItem, Quantity = 4m,
                CreatedBy = "qa", CreatedOn = ReceivedOn, IsActive = true
            },
            new ShipmentItem
            {
                Id = secondShipmentItem, ShipmentId = secondShipment, OrderItemId = orderItem, Quantity = 4m,
                CreatedBy = "qa", CreatedOn = ReceivedOn, IsActive = true
            });
        await seed.SaveChangesAsync();
        return new Fixture(
            tenant, order, shipment, shipmentItem, secondShipment, secondShipmentItem, deliveredStatus);
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

    private static DeliveryConfirmationService Service(ErpRfqAutomationContext context)
        => new(context, NullLogger<DeliveryConfirmationService>.Instance);

    private static ConfirmDeliveryCommand Command(long shipmentItemId, string receivedBy)
        => new(receivedBy, null, null, ReceivedOn, null, null, null, null, null, null, null, null,
            [new ConfirmDeliveryLineCommand(shipmentItemId, 4m, null, null, null)]);

    private static async Task<Attempt> CaptureAsync(
        DeliveryConfirmationService service, long tenant, long shipment, string key,
        ConfirmDeliveryCommand command)
    {
        try
        {
            return new Attempt(await service.ConfirmAsync(
                tenant, shipment, key, command, "driver"), null);
        }
        catch (Exception exception)
        {
            return new Attempt(null, exception);
        }
    }

    private sealed class DeliveryReplayReadBarrier : DbCommandInterceptor
    {
        private readonly TaskCompletionSource _bothReaders =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _arrived;

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("delivery_proofs", StringComparison.OrdinalIgnoreCase)
                && command.CommandText.Contains("IdempotencyKey", StringComparison.OrdinalIgnoreCase)
                && Interlocked.Increment(ref _arrived) <= 2)
            {
                if (Volatile.Read(ref _arrived) == 2) _bothReaders.TrySetResult();
                await _bothReaders.Task.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken);
            }

            return result;
        }
    }

    private sealed record Fixture(
        long Tenant, long Order, long Shipment, long ShipmentItem,
        long SecondShipment, long SecondShipmentItem, long DeliveredStatus);
    private sealed record Attempt(DeliveryProofView? View, Exception? Error);
}
