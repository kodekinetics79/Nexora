using System.Security.Claims;
using ERP_RFQ_Automation.Controllers;
using ERP_RFQ_Automation.DTOs;
using ERP_RFQ_Automation.Inventory;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Procurement;
using ERP_RFQ_Automation.Repositories;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// The commercial case is allocated at lead ingestion and has to survive to the last document of
/// the cycle. It already reached the RFQ, the quote, the sales order and the client PO, then
/// stopped: the supplier purchase order and the shipment had the columns but nothing wrote them,
/// so the two documents an operator physically hands to a supplier and a driver were the only ones
/// that could not state which case they belonged to without a multi-table join.
/// </summary>
public sealed class CommercialCaseContinuityTests
{
    private static readonly DateTime Now = new(2026, 8, 9, 9, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task A_supplier_purchase_order_raised_from_a_customer_award_carries_the_case_reference()
    {
        using var fixture = new ProcurementScenario();
        var (caseId, serial) = await fixture.AllocateCommercialCaseAsync();
        var award = await fixture.CreateAwardAsync("case-continuity", quantity: 8m);

        var draft = await fixture.Execute(service => service.CreatePurchaseOrderAsync(
            fixture.PurchaseOrder([award.Id], "case-continuity-po")));

        await using var verify = fixture.Context();
        var purchaseOrder = await verify.SupplierPurchaseOrders.SingleAsync(x => x.Id == draft.Id);
        Assert.Equal(SupplierPurchaseOrderDemandSources.CustomerDemand, purchaseOrder.DemandSource);
        Assert.Equal(caseId, purchaseOrder.CommercialCaseId);
        Assert.Equal(serial, purchaseOrder.NexoraSerial);
    }

    /// <summary>
    /// The reference is written by the same transaction that inserts the row, so there is no window
    /// in which a committed purchase order exists without the case it was raised for.
    /// </summary>
    [Fact]
    public async Task An_issued_purchase_order_still_carries_the_reference_it_was_created_with()
    {
        using var fixture = new ProcurementScenario();
        var (caseId, serial) = await fixture.AllocateCommercialCaseAsync();

        var issued = await fixture.CreatePurchaseOrderAsync("case-continuity-issue", quantity: 8m);

        await using var verify = fixture.Context();
        var purchaseOrder = await verify.SupplierPurchaseOrders.SingleAsync(x => x.Id == issued.Id);
        Assert.Equal(SupplierPurchaseOrderStatuses.Sent, purchaseOrder.Status);
        Assert.Equal(caseId, purchaseOrder.CommercialCaseId);
        Assert.Equal(serial, purchaseOrder.NexoraSerial);
    }

    /// <summary>
    /// Replenishment has no customer behind it. Leaving the reference null is the correct answer
    /// rather than missing data — a manufactured case would be quoted back at us as though real.
    /// </summary>
    [Fact]
    public void A_stock_replenishment_purchase_order_is_left_without_a_case_reference()
    {
        var purchaseOrder = new SupplierPurchaseOrder
        {
            BusinessUnitId = 96_001,
            DemandSource = SupplierPurchaseOrderDemandSources.Stock
        };

        purchaseOrder.InheritCommercialIdentity(96_001, 96_080, "NXR-QA-96001-96060");

        Assert.Null(purchaseOrder.CommercialCaseId);
        Assert.Null(purchaseOrder.NexoraSerial);
    }

    /// <summary>
    /// A case id is a tenant-owned key. Carrying one across business units would print another
    /// tenant's master reference on this tenant's supplier paperwork.
    /// </summary>
    [Fact]
    public void A_case_belonging_to_another_business_unit_is_refused()
    {
        var purchaseOrder = new SupplierPurchaseOrder { BusinessUnitId = 96_001 };

        var failure = Assert.Throws<InvalidOperationException>(() =>
            purchaseOrder.InheritCommercialIdentity(96_002, 96_080, "NXR-QA-96002-96060"));

        Assert.Contains("business unit", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Null(purchaseOrder.CommercialCaseId);
    }

    [Fact]
    public async Task A_shipment_raised_against_an_order_carries_the_orders_case_reference()
    {
        var scenario = new ShipmentScenario();
        using var database = scenario.Database;

        var response = await scenario.CreateShipmentAsync();

        Assert.IsType<CreatedAtActionResult>(response);
        await using var verify = database.ContextFor(ShipmentScenario.Tenant);
        var shipment = await verify.Shipments.SingleAsync();
        Assert.Equal(ShipmentScenario.CommercialCaseId, shipment.CommercialCaseId);
        Assert.Equal(ShipmentScenario.Serial, shipment.NexoraSerial);
    }

    /// <summary>
    /// A sales order raised by hand never came from a lead and has no case. The despatch note
    /// records that gap honestly instead of inventing a reference for it.
    /// </summary>
    [Fact]
    public async Task A_shipment_for_an_order_with_no_case_records_the_gap_rather_than_inventing_one()
    {
        var scenario = new ShipmentScenario(allocateCase: false);
        using var database = scenario.Database;

        var response = await scenario.CreateShipmentAsync();

        Assert.IsType<CreatedAtActionResult>(response);
        await using var verify = database.ContextFor(ShipmentScenario.Tenant);
        var shipment = await verify.Shipments.SingleAsync();
        Assert.Null(shipment.CommercialCaseId);
        Assert.Null(shipment.NexoraSerial);
    }

    [Fact]
    public void A_shipment_refuses_an_order_from_another_business_unit()
    {
        var shipment = new Shipment { BusinessUnitId = 96_301, OrderId = 96_310 };
        var order = Seed.StampCommercialCase(
            new Order { Id = 96_310, BusinessUnitId = 96_302 }, 96_320, "NXR-QA-96302-96330");

        var failure = Assert.Throws<InvalidOperationException>(() =>
            shipment.InheritCommercialIdentity(order));

        Assert.Contains("tenant", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Null(shipment.CommercialCaseId);
    }

    /// <summary>
    /// A sales order, its stock and the statuses a despatch needs, with the commercial case a
    /// converted lead would have handed down the chain.
    /// </summary>
    private sealed class ShipmentScenario
    {
        public const long Tenant = 96_301;
        public const long CommercialCaseId = 96_320;
        public const string Serial = "NXR-QA-96301-96330";
        private const long OrderId = 96_310;
        private const long OrderItemId = 96_311;
        private const long WarehouseId = 96_312;
        private const long ProductId = 96_313;
        private const long InventoryId = 96_314;
        private const long OpenStatusId = 96_315;
        private const long ShippedStatusId = 96_316;
        private const long ShipmentStatusId = 96_317;

        public ShipmentScenario(bool allocateCase = true)
        {
            Database = new TestDb();
            using var seed = Database.ContextFor(null);
            Seed.EnsureBusinessUnit(seed, Tenant);
            Seed.Customer(seed, Tenant, Tenant, "Continuity customer");
            seed.SetupMasters.AddRange(
                Status(OpenStatusId, "OrderStatus", "OPEN"),
                Status(ShippedStatusId, "OrderStatus", "SHIPPED"),
                Status(ShipmentStatusId, "ShipmentStatus", "READY"));
            seed.Warehouses.Add(new Warehouse
            {
                Id = WarehouseId, BusinessUnitId = Tenant, WarehouseCode = "WH-CONT",
                WarehouseName = "Continuity warehouse", IsActive = true, CreatedBy = "qa", CreatedOn = Now
            });
            seed.Products.Add(new Product
            {
                Id = ProductId, Buid = Tenant, PartNo = "PART-CONT", ProductName = "Continuity product",
                WarehouseId = WarehouseId, QtyOnHand = 10m, ReorderPoint = 0m, IsActive = true,
                CreatedBy = "qa", CreatedOn = Now
            });
            seed.Set<Models.Inventory>().Add(new Models.Inventory
            {
                Id = InventoryId, Buid = Tenant, ProductId = ProductId, WarehouseId = WarehouseId,
                PartNo = "PART-CONT", ProductName = "Continuity product", QtyOnHand = 10m,
                ReorderPoint = 0m, CreatedBy = "qa", CreatedOn = Now
            });
            if (allocateCase)
            {
                var commercialCase = new CommercialCase
                {
                    Id = CommercialCaseId, BusinessUnitId = Tenant, CreatedBy = "qa", CreatedOn = Now
                };
                commercialCase.AssignIdentity(1, Serial);
                seed.CommercialCases.Add(commercialCase);
            }
            var order = new Order
            {
                Id = OrderId, OrderNo = "SO-96310", CustomerId = Tenant, BusinessUnitId = Tenant,
                StatusId = OpenStatusId, TotalAmount = 40m, OrderDate = Now, CreatedBy = "qa",
                CreatedOn = Now, IsActive = true
            };
            if (allocateCase)
                Seed.StampCommercialCase(order, CommercialCaseId, Serial);
            seed.Set<Order>().Add(order);
            seed.Set<OrderItem>().Add(new OrderItem
            {
                Id = OrderItemId, OrderId = OrderId, ProductId = ProductId, WarehouseId = WarehouseId,
                Quantity = 4m, UnitPrice = 10m, Discount = 0m, TaxAmount = 0m, TotalAmount = 40m,
                CreatedBy = "qa", CreatedDate = Now, IsActive = true
            });
            seed.SaveChanges();
        }

        public TestDb Database { get; }

        public async Task<IActionResult> CreateShipmentAsync()
        {
            await using var context = Database.ContextFor(Tenant);
            var controller = new ShipmentController(
                new ShipmentRepository(context), context,
                InventoryServices.OrderStock(context))
            {
                ControllerContext = new ControllerContext
                {
                    HttpContext = new DefaultHttpContext
                    {
                        User = new ClaimsPrincipal(new ClaimsIdentity(
                        [
                            new Claim("businessUnitId", Tenant.ToString()),
                            new Claim("email", "rep@continuity")
                        ], "test"))
                    }
                }
            };

            return await controller.CreateShipment(new CreateShipmentDto
            {
                OrderId = OrderId,
                BusinessUnitId = Tenant,
                StatusId = ShipmentStatusId,
                ShipmentDate = Now,
                Items = [new CreateShipmentItemDto { OrderItemId = OrderItemId, Quantity = 4m }]
            });
        }

        private static SetupMaster Status(long setupId, string type, string value) => new()
        {
            SetupId = setupId, SetupType = type, SetupCode = value, SetupValue = value,
            BusinessUnitId = Tenant, IsActive = true, CreatedBy = "qa", CreatedOn = Now
        };
    }
}
