using System.Security.Claims;
using ERP_RFQ_Automation.Controllers;
using ERP_RFQ_Automation.DTOs;
using ERP_RFQ_Automation.DTOs.OrderDTOs;
using ERP_RFQ_Automation.Intelligence.Decision;
using ERP_RFQ_Automation.Inventory;
using ERP_RFQ_Automation.Inventory.Commercial;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Procurement;
using ERP_RFQ_Automation.Repositories;
using ERP_RFQ_Automation.Services;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using OfficeOpenXml;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// Certifies the cross-module contracts around the authoritative stock ledger — the ones that sat
/// in modules the inventory workstream could not reach, and each of which left physical stock and
/// the system's idea of it permanently out of step:
///
/// <list type="bullet">
/// <item>a shipment is a goods issue, so it must decrement on-hand, atomically, and only for the
/// caller's own tenant;</item>
/// <item>a cancelled or deleted order must give its holds back rather than suppressing
/// available-to-promise forever;</item>
/// <item>an Excel opening-stock import must land somewhere the availability engine can see;</item>
/// <item>the lead decision brief must quote available-to-promise, not the legacy product column;</item>
/// <item>inbound supply already committed to one customer must not read as free supply to the next;</item>
/// <item>a purchase order that will never arrive must be cancellable, or its RFQ line can never be
/// re-sourced.</item>
/// </list>
/// </summary>
public sealed class CrossModuleStockIntegrityTests
{
    private const long Tenant = 94_001;
    private const long OtherTenant = 94_002;
    private const long Warehouse = 94_010;
    private const long OtherWarehouse = 94_011;
    private const long Product = 94_020;
    private const long OtherProduct = 94_021;
    private const long InventoryRow = 94_030;
    private const long OtherInventoryRow = 94_031;
    private const long OrderId = 94_040;
    private const long OtherOrderId = 94_041;
    private const long OrderItemId = 94_050;
    private const long OtherOrderItemId = 94_051;
    private const long OpenStatus = 94_060;
    private const long ShippedStatus = 94_061;
    private const long CancelledStatus = 94_062;
    private const long OtherOpenStatus = 94_063;

    private static readonly DateTime Now = new(2026, 8, 4, 9, 0, 0, DateTimeKind.Utc);

    // ================================================================== shipment issues stock

    [Fact]
    public async Task Shipping_an_order_decrements_on_hand_stock_and_posts_an_issue_movement()
    {
        using var database = NewDatabase();

        var result = await CreateShipmentAsync(database, Tenant, OrderId, OrderItemId, quantity: 4m);

        Assert.IsType<CreatedAtActionResult>(result);

        await using var verify = database.ContextFor(Tenant);
        // Before this fix ShipmentRepository contained no reference to stock at all: goods shipped
        // and invoiced left on-hand at its opening balance until somebody remembered to call
        // POST /api/Order/{id}/consume-stock by hand.
        Assert.Equal(6m, await verify.Set<Models.Inventory>().Where(x => x.Id == InventoryRow)
            .Select(x => x.QtyOnHand).SingleAsync());
        var movement = Assert.Single(await verify.InventoryMovements
            .Where(x => x.InventoryId == InventoryRow).ToListAsync());
        Assert.Equal(InventoryMovementType.Issue, movement.Type);
        Assert.Equal(4m, movement.Quantity);
        Assert.Equal(StockReservationStatus.Consumed,
            Assert.Single(await verify.StockReservations.Where(x => x.OrderId == OrderId).ToListAsync()).Status);
    }

    [Fact]
    public async Task Reshipping_the_same_order_is_refused_rather_than_silently_moving_no_stock()
    {
        using var database = NewDatabase();

        Assert.IsType<CreatedAtActionResult>(
            await CreateShipmentAsync(database, Tenant, OrderId, OrderItemId, quantity: 4m));

        // GATE 6. The line was ordered 4 and 4 have shipped, so a second despatch for 4 is
        // over-shipment and the server now refuses it.
        //
        // This assertion USED to be `Assert.Equal(2, Shipments.Count)`: the duplicate despatch note
        // was accepted and written, and the test settled for proving that it moved no stock a
        // second time. That is the weaker half of the guarantee. A delivery note for goods that
        // cannot leave — because they have already left — is a document the warehouse acts on and
        // finance cannot invoice, since the invoice ceiling was already cumulative while this one
        // was not. Refusing it outright is the behaviour the order line's quantity was always
        // meant to have.
        var second = Assert.IsType<ConflictObjectResult>(
            await CreateShipmentAsync(database, Tenant, OrderId, OrderItemId, quantity: 4m));
        Assert.Contains("exceeds the remaining quantity",
            second.Value?.GetType().GetProperty("message")?.GetValue(second.Value)?.ToString() ?? "");

        await using var verify = database.ContextFor(Tenant);
        // The original guarantee still holds: stock moved exactly once.
        Assert.Equal(6m, await verify.Set<Models.Inventory>().Where(x => x.Id == InventoryRow)
            .Select(x => x.QtyOnHand).SingleAsync());
        Assert.Equal(1, await verify.Shipments.CountAsync());
    }

    [Fact]
    public async Task A_shipment_against_another_tenants_order_is_refused_and_moves_no_stock()
    {
        using var database = NewDatabase();

        // The order id belongs to OtherTenant; the caller authenticates as Tenant. The old code
        // resolved the order with Orders.FindAsync(id) — no business-unit predicate at all — so
        // this created a shipment against the victim's order, and now that a shipment issues
        // stock it would have decremented the victim's on-hand too.
        var result = await CreateShipmentAsync(database, Tenant, OtherOrderId, OtherOrderItemId, quantity: 4m);

        Assert.IsType<NotFoundObjectResult>(result);

        await using var verify = database.ContextFor(null);
        Assert.Empty(await verify.Shipments.ToListAsync());
        Assert.Empty(await verify.StockReservations.ToListAsync());
        Assert.Equal(10m, await verify.Set<Models.Inventory>().Where(x => x.Id == OtherInventoryRow)
            .Select(x => x.QtyOnHand).SingleAsync());
    }

    [Fact]
    public async Task A_partial_shipment_moves_only_the_shipped_quantity()
    {
        using var database = NewDatabase();

        // 1 of the 4 ordered units leaves the warehouse. The UI actively invites this: the create
        // form renders an editable Ship Qty per line, capped at the ordered quantity.
        var result = await CreateShipmentAsync(database, Tenant, OrderId, OrderItemId, quantity: 1m);

        Assert.IsType<CreatedAtActionResult>(result);

        await using var verify = database.ContextFor(Tenant);
        // The goods issue used to be order-scoped and quantity-blind: shipping 1 decremented all 4,
        // posted an Issue movement for 4 and consumed the hold, so the 3 units still on the shelf
        // were gone from the books with nothing left to release them — and the reconciler saw no
        // drift, because the movement and the balance were wrong in exactly the same way.
        Assert.Equal(9m, await verify.Set<Models.Inventory>().Where(x => x.Id == InventoryRow)
            .Select(x => x.QtyOnHand).SingleAsync());
        var movement = Assert.Single(await verify.InventoryMovements
            .Where(x => x.InventoryId == InventoryRow).ToListAsync());
        Assert.Equal(InventoryMovementType.Issue, movement.Type);
        Assert.Equal(1m, movement.Quantity);

        var reservations = await verify.StockReservations.Where(x => x.OrderId == OrderId).ToListAsync();
        Assert.Equal(1m, reservations.Where(x => x.Status == StockReservationStatus.Consumed).Sum(x => x.Quantity));
        Assert.Equal(3m, reservations.Where(x => x.Status == StockReservationStatus.Active).Sum(x => x.Quantity));

        // 9 on hand - 3 still held for this order = 6 promisable elsewhere.
        await AssertAvailableAsync(database, 6m);

        // And the order is NOT closed: 3 of 4 units have not shipped.
        Assert.Equal(OpenStatus, await verify.Orders.Where(x => x.Id == OrderId).Select(x => x.StatusId).SingleAsync());
    }

    [Fact]
    public async Task The_balance_of_a_partially_shipped_order_is_still_releasable()
    {
        using var database = NewDatabase();
        await CreateShipmentAsync(database, Tenant, OrderId, OrderItemId, quantity: 1m);

        await using (var release = database.ContextFor(Tenant))
        {
            await InventoryServices.OrderStock(release)
                .ReleaseOrderAsync(Tenant, OrderId, "order-cancelled");
        }

        // The 3 unshipped units come back. Against the quantity-blind issue they were Consumed,
        // so this returned nothing and the stock was suppressed from availability forever.
        await AssertAvailableAsync(database, 9m);
    }

    [Fact]
    public async Task Shipping_the_remainder_completes_the_order()
    {
        using var database = NewDatabase();

        await CreateShipmentAsync(database, Tenant, OrderId, OrderItemId, quantity: 1m);
        var second = await CreateShipmentAsync(database, Tenant, OrderId, OrderItemId, quantity: 3m);

        Assert.IsType<CreatedAtActionResult>(second);

        await using var verify = database.ContextFor(Tenant);
        Assert.Equal(6m, await verify.Set<Models.Inventory>().Where(x => x.Id == InventoryRow)
            .Select(x => x.QtyOnHand).SingleAsync());
        Assert.Equal(new[] { 1m, 3m }, (await verify.InventoryMovements
            .Where(x => x.InventoryId == InventoryRow).OrderBy(x => x.Id).ToListAsync())
            .Select(x => x.Quantity).ToArray());
        Assert.All(await verify.StockReservations.Where(x => x.OrderId == OrderId).ToListAsync(),
            x => Assert.Equal(StockReservationStatus.Consumed, x.Status));

        // Every ordered unit has now shipped, across two shipments, so the order closes.
        Assert.Equal(ShippedStatus, await verify.Orders.Where(x => x.Id == OrderId)
            .Select(x => x.StatusId).SingleAsync());
    }

    [Fact]
    public async Task A_shipment_line_belonging_to_another_order_is_refused_and_moves_no_stock()
    {
        using var database = NewDatabase();

        // OrderItem ids are global (the table has no BusinessUnitId — isolation is parent-derived),
        // so a caller naming the victim's line must be refused before anything is written.
        var result = await CreateShipmentAsync(database, Tenant, OrderId, OtherOrderItemId, quantity: 1m);

        Assert.IsType<NotFoundObjectResult>(result);
        await using var verify = database.ContextFor(null);
        Assert.Empty(await verify.Shipments.ToListAsync());
        Assert.Empty(await verify.StockReservations.ToListAsync());
        Assert.Equal(10m, await verify.Set<Models.Inventory>().Where(x => x.Id == OtherInventoryRow)
            .Select(x => x.QtyOnHand).SingleAsync());
    }

    [Fact]
    public async Task A_shipment_that_cannot_be_stocked_leaves_no_shipment_row_behind()
    {
        using var database = NewDatabase();

        // Physical stock is gone but the hold survives, so consumption must fail. The whole method
        // used to be wrapped in `catch (Exception) => BadRequest`, which reported a failure while
        // the shipment row had already been committed by an earlier SaveChanges.
        await using (var setup = database.ContextFor(Tenant))
        {
            await InventoryServices.OrderStock(setup)
                .ReserveOrderAsync(Tenant, OrderId, "rep@acme");
        }
        await using (var damage = database.ContextFor(Tenant))
        {
            var row = await damage.Set<Models.Inventory>().SingleAsync(x => x.Id == InventoryRow);
            row.QtyOnHand = 1m;
            await damage.SaveChangesAsync();
        }

        var result = await CreateShipmentAsync(database, Tenant, OrderId, OrderItemId, quantity: 4m);

        Assert.IsType<ConflictObjectResult>(result);
        await using var verify = database.ContextFor(Tenant);
        Assert.Empty(await verify.Shipments.ToListAsync());
        Assert.Equal(1m, await verify.Set<Models.Inventory>().Where(x => x.Id == InventoryRow)
            .Select(x => x.QtyOnHand).SingleAsync());
    }

    // ================================================================== order release

    [Fact]
    public async Task Cancelling_an_order_releases_its_stock_holds()
    {
        using var database = NewDatabase();
        await using (var allocate = database.ContextFor(Tenant))
        {
            await InventoryServices.OrderStock(allocate)
                .ReserveOrderAsync(Tenant, OrderId, "rep@acme");
        }
        await AssertAvailableAsync(database, 6m);

        await using (var context = database.ContextFor(Tenant))
        {
            await new OrderService(new OrderRepository(context), context)
                .UpdateOrderAsync(OrderId, new UpdateOrderDto { StatusId = CancelledStatus }, Tenant);
        }

        // Cancelling used to leave the holds Active forever: the units were never resellable and
        // nothing in the product ever told anyone why.
        await AssertAvailableAsync(database, 10m);
        await using var verify = database.ContextFor(Tenant);
        Assert.Equal(StockReservationStatus.Released,
            Assert.Single(await verify.StockReservations.Where(x => x.OrderId == OrderId).ToListAsync()).Status);
    }

    [Fact]
    public async Task A_status_change_that_is_not_a_cancellation_keeps_the_holds()
    {
        using var database = NewDatabase();
        await using (var allocate = database.ContextFor(Tenant))
        {
            await InventoryServices.OrderStock(allocate)
                .ReserveOrderAsync(Tenant, OrderId, "rep@acme");
        }

        await using (var context = database.ContextFor(Tenant))
        {
            await new OrderService(new OrderRepository(context), context)
                .UpdateOrderAsync(OrderId, new UpdateOrderDto { StatusId = ShippedStatus }, Tenant);
        }

        await AssertAvailableAsync(database, 6m);
    }

    [Fact]
    public async Task Deleting_an_order_releases_its_holds_without_waiting_for_the_orphan_sweep()
    {
        using var database = NewDatabase();
        await using (var allocate = database.ContextFor(Tenant))
        {
            await InventoryServices.OrderStock(allocate)
                .ReserveOrderAsync(Tenant, OrderId, "rep@acme");
        }

        await using (var context = database.ContextFor(Tenant))
        {
            await new OrderService(new OrderRepository(context), context).DeleteOrderAsync(OrderId, Tenant);
        }

        // StockReservation has no FK to Orders, so the row survives the delete with a dangling
        // OrderId and ReleaseForOrderAsync can never be called for it again. Releasing first is
        // the fix; the orphan sweep is only the recovery path for holds already stranded.
        await AssertAvailableAsync(database, 10m);
        await using var verify = database.ContextFor(Tenant);
        Assert.Equal(StockReservationStatus.Released,
            Assert.Single(await verify.StockReservations.ToListAsync()).Status);
        Assert.Equal(0, await InventoryServices.OrderStock(verify)
            .ReleaseOrphanedAsync(Tenant, "sweeper"));
    }

    // ================================================================== excel opening stock

    [Fact]
    public async Task An_excel_stock_import_is_visible_to_the_availability_engine()
    {
        ExcelPackage.LicenseContext = LicenseContext.NonCommercial;
        using var database = new TestDb();
        await using (var seed = database.ContextFor(null))
        {
            Seed.EnsureBusinessUnit(seed, Tenant);
            await seed.SaveChangesAsync();
        }

        byte[] workbook;
        await using (var context = database.ContextFor(Tenant))
        {
            // The shipped template carries sample rows with a Warehouse and a Qty On Hand, which
            // is exactly the shape a client returns: "here is my stock list".
            workbook = await Uploader(context).GenerateTemplateAsync(Tenant);
        }

        await using (var context = database.ContextFor(Tenant))
        {
            using var stream = new MemoryStream(workbook);
            var result = await Uploader(context).UploadTemplateAsync(stream, Tenant, "importer@acme");
            Assert.True(result.Success);
        }

        await using var verify = database.ContextFor(Tenant);
        var laptop = await verify.Products.SingleAsync(x => x.PartNo == "CLP-001");
        var mainWarehouse = await verify.Warehouses.SingleAsync(x => x.WarehouseName == "Main WH");

        // The import used to write Product.QtyOnHand, a legacy denormalised column that posts no
        // movement and that no availability screen reads — so an opening stock upload was
        // invisible to every warehouse, ATP and order-allocation path in the product.
        Assert.Equal(0m, laptop.QtyOnHand);

        var stock = await verify.Set<Models.Inventory>()
            .SingleAsync(x => x.ProductId == laptop.Id && x.WarehouseId == mainWarehouse.Id);
        Assert.Equal(10m, stock.QtyOnHand);
        var availability = await new InventoryAvailabilityService(verify).GetAvailabilityAsync(Tenant, stock.Id);
        Assert.Equal(10m, availability.Available);

        // Balance and movement are posted together, so the reconciler cannot report drift.
        var movement = Assert.Single(await verify.InventoryMovements.Where(x => x.InventoryId == stock.Id).ToListAsync());
        Assert.Equal(InventoryMovementType.AdjustmentIncrease, movement.Type);
        Assert.Equal(10m, movement.Quantity);
        Assert.Empty(await new InventoryAvailabilityService(verify).ReconcileLedgerAsync(Tenant));
    }

    [Fact]
    public async Task An_imported_balance_round_trips_through_the_export()
    {
        ExcelPackage.LicenseContext = LicenseContext.NonCommercial;
        using var database = new TestDb();
        await using (var seed = database.ContextFor(null))
        {
            Seed.EnsureBusinessUnit(seed, Tenant);
            await seed.SaveChangesAsync();
        }

        byte[] exported;
        await using (var context = database.ContextFor(Tenant))
        {
            var workbook = await Uploader(context).GenerateTemplateAsync(Tenant);
            using var stream = new MemoryStream(workbook);
            await Uploader(context).UploadTemplateAsync(stream, Tenant, "importer@acme");
            exported = await Uploader(context).ExportProductsAsync(Tenant);
        }

        using var package = new ExcelPackage(new MemoryStream(exported));
        var sheet = package.Workbook.Worksheets[0];
        var quantities = new Dictionary<string, decimal>();
        for (var row = 2; row <= sheet.Dimension.Rows; row++)
            quantities[sheet.Cells[row, 3].Text] = decimal.Parse(sheet.Cells[row, 10].Text);

        // The export reads the ledger too, so what the client uploads is what they get back.
        Assert.Equal(10m, quantities["CLP-001"]);
        Assert.Equal(50m, quantities["ACC-001"]);
    }

    // ================================================================== decision brief

    [Fact]
    public async Task Lead_decision_brief_reports_available_to_promise_not_the_product_master_column()
    {
        using var database = new TestDb();
        await using (var seed = database.ContextFor(null))
        {
            Seed.EnsureBusinessUnit(seed, Tenant);
            var lead = Seed.Lead(seed, 1, Tenant, buyersName: null);
            lead.LeadItems.Add(new LeadItem
            {
                Id = 94_100, ItemMaterialCode = "DECIDE-1", ProductShortName = "Decision widget",
                Quantity = 5, UnitPrice = 100m, Currency = "USD"
            });
            seed.Products.Add(new Product
            {
                // The legacy column claims 999 units. Nothing has ever posted a movement for them
                // and the ATP engine cannot see them.
                Id = Product, Buid = Tenant, PartNo = "DECIDE-1", ProductName = "Decision widget",
                QtyOnHand = 999m, IsActive = true, CreatedBy = "qa", CreatedOn = Now
            });
            seed.Warehouses.Add(new Warehouse
            {
                Id = Warehouse, BusinessUnitId = Tenant, WarehouseCode = "WH-D", WarehouseName = "Decision WH",
                IsActive = true, CreatedBy = "qa", CreatedOn = Now
            });
            seed.Set<Models.Inventory>().Add(new Models.Inventory
            {
                Id = InventoryRow, Buid = Tenant, ProductId = Product, WarehouseId = Warehouse,
                PartNo = "DECIDE-1", QtyOnHand = 12m, SafetyStockQuantity = 2m, QuarantineQuantity = 3m,
                ReorderPoint = 0m, CreatedBy = "qa", CreatedOn = Now
            });
            await seed.SaveChangesAsync();
        }
        await using (var hold = database.ContextFor(Tenant))
        {
            await new InventoryAvailabilityService(hold)
                .ReserveAsync(Tenant, InventoryRow, 3m, "another-order", orderId: 94_999);
        }

        await using var context = database.ContextFor(Tenant);
        var brief = await new LeadDecisionService(context).GetBriefAsync(1, Tenant, default);

        // 12 on hand - 3 reserved - 3 quarantined - 2 safety stock = 4 promisable. The brief used
        // to print 999.
        var item = Assert.Single(brief.Coverage.Items);
        Assert.True(item.Matched);
        Assert.Equal(4m, item.CatalogQtyOnHand);
        Assert.True(item.InStock);
        Assert.Equal(4m, brief.Coverage.CatalogOnHandQuantity);
        Assert.DoesNotContain(brief.Reasons, reason => reason.Contains("999", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Lead_decision_brief_reports_no_stock_when_the_product_has_no_inventory_row()
    {
        using var database = new TestDb();
        await using (var seed = database.ContextFor(null))
        {
            Seed.EnsureBusinessUnit(seed, Tenant);
            var lead = Seed.Lead(seed, 1, Tenant, buyersName: null);
            lead.LeadItems.Add(new LeadItem
            {
                Id = 94_100, ItemMaterialCode = "GHOST-1", ProductShortName = "Ghost widget",
                Quantity = 5, UnitPrice = 100m, Currency = "USD"
            });
            seed.Products.Add(new Product
            {
                Id = Product, Buid = Tenant, PartNo = "GHOST-1", ProductName = "Ghost widget",
                QtyOnHand = 500m, IsActive = true, CreatedBy = "qa", CreatedOn = Now
            });
            await seed.SaveChangesAsync();
        }

        await using var context = database.ContextFor(Tenant);
        var brief = await new LeadDecisionService(context).GetBriefAsync(1, Tenant, default);

        var item = Assert.Single(brief.Coverage.Items);
        Assert.True(item.Matched);
        Assert.Equal(0m, item.CatalogQtyOnHand);
        Assert.False(item.InStock);
        Assert.Equal(0, brief.Coverage.CatalogOnHandItems);
    }

    // ================================================================== incoming supply

    [Fact]
    public async Task Issuing_a_purchase_order_commits_its_incoming_supply_so_it_cannot_be_promised_twice()
    {
        using var fixture = new ProcurementScenario();

        await fixture.CreatePurchaseOrderAsync("incoming-commitment", quantity: 8m);

        await using var verify = fixture.Context();
        var incoming = Assert.Single(await verify.IncomingInventory.ToListAsync());
        Assert.Equal(8m, incoming.OrderedQuantity);
        // AllocatedQuantity was hard-coded to 0 and never incremented, so OpenQuantity subtracted
        // nothing: the same inbound container read as 8 free units to every customer line that
        // asked, and two customers could be promised it.
        Assert.Equal(8m, incoming.AllocatedQuantity);
        Assert.Equal(0m, incoming.OpenQuantity);
    }

    [Fact]
    public async Task Receiving_inbound_supply_releases_its_commitment_as_the_units_become_on_hand()
    {
        using var fixture = new ProcurementScenario();
        var purchaseOrder = await fixture.CreatePurchaseOrderAsync("incoming-receipt", quantity: 8m);
        var lineId = await fixture.PurchaseOrderLineIdAsync(purchaseOrder.Id);

        await fixture.Execute(service => service.PostGoodsReceiptAsync(
            fixture.Receipt(purchaseOrder.Id, lineId, 3m, 3, "incoming-receipt-post", "GR-INCOMING")));

        await using var verify = fixture.Context();
        var incoming = Assert.Single(await verify.IncomingInventory.ToListAsync());
        Assert.Equal(3m, incoming.ReceivedQuantity);
        Assert.Equal(5m, incoming.AllocatedQuantity);
        Assert.Equal(0m, incoming.OpenQuantity); // still nothing uncommitted to promise elsewhere
    }

    // ================================================================== purchase order cancel

    [Fact]
    public async Task A_draft_purchase_order_no_longer_suppresses_the_net_sourcing_requirement()
    {
        using var fixture = new ProcurementScenario();
        var award = await fixture.CreateAwardAsync("draft-suppression", quantity: 8m);
        await fixture.Execute(service => service.CreatePurchaseOrderAsync(
            fixture.PurchaseOrder([award.Id], "draft-suppression-po")));

        // A DRAFT purchase order used to count as committed supply, so this threw "already fully
        // covered" and the customer line could never be re-sourced.
        var solicitation = await fixture.Execute(service =>
            service.CreateSolicitationAsync(fixture.Solicitation("draft-suppression-retry")));

        Assert.True(solicitation.Id > 0);
    }

    [Fact]
    public async Task A_stranded_draft_purchase_order_can_be_cancelled_and_the_line_re_sourced()
    {
        using var fixture = new ProcurementScenario();
        var award = await fixture.CreateAwardAsync("stranded-draft", quantity: 8m);
        var draft = await fixture.Execute(service => service.CreatePurchaseOrderAsync(
            fixture.PurchaseOrder([award.Id], "stranded-draft-po")));

        var cancelled = await fixture.Execute(service => service.CancelPurchaseOrderAsync(
            fixture.Cancel(draft.Id, "stranded-draft-cancel", "Supplier quotes lapsed before issuance.")));

        Assert.Equal(SupplierPurchaseOrderStatuses.Cancelled, cancelled.Status);
        Assert.False(cancelled.Replayed);

        await using var verify = fixture.Context();
        Assert.Equal("CANCELLED", Assert.Single(await verify.Set<ERP_RFQ_Automation.Agent.Models.SourcingAward>()
            .Where(x => x.Id == award.Id).ToListAsync()).Status);
        Assert.Single(await verify.ProcurementEvents.Where(x => x.EventType == "SUPPLIER_PO_CANCELLED").ToListAsync());
    }

    [Fact]
    public async Task Cancelling_an_issued_purchase_order_withdraws_its_incoming_supply()
    {
        using var fixture = new ProcurementScenario();
        var purchaseOrder = await fixture.CreatePurchaseOrderAsync("issued-cancel", quantity: 8m);

        await fixture.Execute(service => service.CancelPurchaseOrderAsync(
            fixture.Cancel(purchaseOrder.Id, "issued-cancel-command", "Supplier cannot deliver.", version: 3)));

        await using var verify = fixture.Context();
        var incoming = Assert.Single(await verify.IncomingInventory.ToListAsync());
        Assert.Equal(IncomingInventoryStatus.Cancelled, incoming.Status);
        Assert.Equal(0m, incoming.AllocatedQuantity);
        Assert.Equal(0m, incoming.OpenQuantity);
    }

    [Fact]
    public async Task A_purchase_order_with_received_goods_cannot_be_cancelled()
    {
        using var fixture = new ProcurementScenario();
        var purchaseOrder = await fixture.CreatePurchaseOrderAsync("received-cancel", quantity: 8m);
        var lineId = await fixture.PurchaseOrderLineIdAsync(purchaseOrder.Id);
        await fixture.Execute(service => service.PostGoodsReceiptAsync(
            fixture.Receipt(purchaseOrder.Id, lineId, 3m, 3, "received-cancel-receipt", "GR-CANCEL")));

        var exception = await Assert.ThrowsAsync<ProcurementConflictException>(() =>
            fixture.Execute(service => service.CancelPurchaseOrderAsync(
                fixture.Cancel(purchaseOrder.Id, "received-cancel-command", "Too late.", version: 4))));

        Assert.Contains("received", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Cancelling_a_purchase_order_replays_on_the_same_idempotency_key()
    {
        using var fixture = new ProcurementScenario();
        var award = await fixture.CreateAwardAsync("cancel-replay", quantity: 8m);
        var draft = await fixture.Execute(service => service.CreatePurchaseOrderAsync(
            fixture.PurchaseOrder([award.Id], "cancel-replay-po")));
        var command = fixture.Cancel(draft.Id, "cancel-replay-command", "Withdrawn.");

        await fixture.Execute(service => service.CancelPurchaseOrderAsync(command));
        var replay = await fixture.Execute(service => service.CancelPurchaseOrderAsync(command));

        Assert.True(replay.Replayed);
        await using var verify = fixture.Context();
        Assert.Single(await verify.ProcurementEvents.Where(x => x.EventType == "SUPPLIER_PO_CANCELLED").ToListAsync());
    }

    // ================================================================== helpers

    private static ProductUploaderService Uploader(ErpRfqAutomationContext context)
        => new(context, NullLogger<ProductUploaderService>.Instance);

    private static async Task<IActionResult> CreateShipmentAsync(TestDb database, long tenant,
        long orderId, long orderItemId, decimal quantity)
    {
        await using var context = database.ContextFor(tenant);
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
                        new Claim("businessUnitId", tenant.ToString()),
                        new Claim("email", "rep@acme")
                    ], "test"))
                }
            }
        };

        return await controller.CreateShipment(new CreateShipmentDto
        {
            OrderId = orderId,
            BusinessUnitId = tenant,
            StatusId = tenant == Tenant ? OpenStatus : OtherOpenStatus,
            ShipmentDate = Now,
            Items = [new CreateShipmentItemDto { OrderItemId = orderItemId, Quantity = quantity }]
        });
    }

    private static async Task AssertAvailableAsync(TestDb database, decimal expected)
    {
        await using var context = database.ContextFor(Tenant);
        var availability = await new InventoryAvailabilityService(context)
            .GetAvailabilityAsync(Tenant, InventoryRow);
        Assert.Equal(expected, availability.Available);
    }

    private static TestDb NewDatabase()
    {
        var database = new TestDb();
        using var seed = database.ContextFor(null);
        SeedTenant(seed, Tenant, Warehouse, Product, InventoryRow, OrderId, OrderItemId, OpenStatus, "home");
        seed.SetupMasters.AddRange(
            OrderStatus(ShippedStatus, Tenant, "SHIPPED"),
            OrderStatus(CancelledStatus, Tenant, "CANCELLED"));
        SeedTenant(seed, OtherTenant, OtherWarehouse, OtherProduct, OtherInventoryRow, OtherOrderId,
            OtherOrderItemId, OtherOpenStatus, "victim");
        seed.SaveChanges();
        return database;
    }

    private static void SeedTenant(ErpRfqAutomationContext context, long tenant, long warehouseId,
        long productId, long inventoryId, long orderId, long orderItemId, long statusId, string label)
    {
        Seed.EnsureBusinessUnit(context, tenant);
        Seed.Customer(context, tenant, tenant, $"Customer {label}");
        context.SetupMasters.Add(OrderStatus(statusId, tenant, "OPEN"));
        context.Warehouses.Add(new Warehouse
        {
            Id = warehouseId, BusinessUnitId = tenant, WarehouseCode = $"WH-{label}",
            WarehouseName = $"Warehouse {label}", IsActive = true, CreatedBy = "qa", CreatedOn = Now
        });
        context.Products.Add(new Product
        {
            Id = productId, Buid = tenant, PartNo = $"PART-{label}", ProductName = $"Product {label}",
            WarehouseId = warehouseId, QtyOnHand = 0m, ReorderPoint = 0m, IsActive = true,
            CreatedBy = "qa", CreatedOn = Now
        });
        context.Set<Models.Inventory>().Add(new Models.Inventory
        {
            Id = inventoryId, Buid = tenant, ProductId = productId, WarehouseId = warehouseId,
            PartNo = $"PART-{label}", ProductName = $"Product {label}", QtyOnHand = 10m, ReorderPoint = 0m,
            CreatedBy = "qa", CreatedOn = Now
        });
        context.Set<Order>().Add(new Order
        {
            Id = orderId, OrderNo = $"SO-{orderId}", CustomerId = tenant, BusinessUnitId = tenant,
            StatusId = statusId, TotalAmount = 40m, OrderDate = Now, CreatedBy = "qa", CreatedOn = Now,
            IsActive = true
        });
        context.Set<OrderItem>().Add(new OrderItem
        {
            Id = orderItemId, OrderId = orderId, ProductId = productId, WarehouseId = warehouseId,
            Quantity = 4m, UnitPrice = 10m, Discount = 0m, TaxAmount = 0m, TotalAmount = 40m,
            CreatedBy = "qa", CreatedDate = Now, IsActive = true
        });
    }

    private static SetupMaster OrderStatus(long setupId, long tenant, string value) => new()
    {
        SetupId = setupId, SetupType = "OrderStatus", SetupCode = value, SetupValue = value,
        BusinessUnitId = tenant, IsActive = true, CreatedBy = "qa", CreatedOn = Now
    };
}
