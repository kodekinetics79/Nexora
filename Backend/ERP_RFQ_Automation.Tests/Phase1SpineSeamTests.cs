using ERP_RFQ_Automation.CommercialCases.Lifecycle;
using ERP_RFQ_Automation.CommercialFinance;
using ERP_RFQ_Automation.Delivery;
using ERP_RFQ_Automation.InboundLogistics;
using ERP_RFQ_Automation.Inventory;
using ERP_RFQ_Automation.Inventory.Commercial;
using ERP_RFQ_Automation.LeadIdentity;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Procurement;
using ERP_RFQ_Automation.Repositories;
using ERP_RFQ_Automation.Tests.Support;
using ERP_RFQ_Automation.Traceability;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// THE SEAMS, not the steps.
///
/// <para>Every gate in this build was closed and certified in isolation, and every gate fixture
/// hand-builds the document its own step starts from: <c>ProcurementScenario</c> seeds an
/// <c>Rfq</c> with <c>AgentSeed.Rfq</c> rather than converting a lead, and
/// <c>Gate7DeliveryConfirmationTests.SeedTenant</c> inserts an <c>Order</c>, an
/// <c>OrderItem</c>, a <c>Shipment</c> and a <c>ShipmentItem</c> directly. A seam proven by a
/// fixture that builds both of its sides is not proven at all: cut the join and the fixture keeps
/// passing, because it never used the join.</para>
///
/// <para>These tests take the opposite shape. Each one starts at a document the PREVIOUS module
/// produced, through that module's own application service, and asserts that the value the next
/// module needs actually arrived. If a handoff is cut — the case is not carried, the receipt does
/// not settle its shipment, the lot is not the one that ships, the invoice reads despatched
/// instead of accepted — one of these fails and no gate suite does.</para>
///
/// <para><b>What this file deliberately does NOT cover</b>, so nobody reads it as more assurance
/// than it is:</para>
/// <list type="bullet">
/// <item>Email arrival and extraction. The journey starts at the identity service the extraction
/// worker calls (<c>ExtractionWorker.cs:1084</c>), not at IMAP; OCR accuracy is not a seam.</item>
/// <item>The customer arm — quotation, customer PO, customer award, sales order — is exercised
/// only as far as the sales order the despatch needs. Award conversion has its own suite
/// (<c>CustomerAwardApplicationServiceTests</c>).</item>
/// <item>HTTP, authorization and RLS. This is the domain spine; the authenticated-HTTP suite and
/// the PostgreSQL lane own those.</item>
/// <item>The despatch ORCHESTRATION lives in <c>ShipmentController.CreateShipment</c> and has no
/// service, so this file reproduces its two calls (<c>ReserveOrderAsync</c> then
/// <c>ConsumeOrderLinesAsync</c>) rather than invoking it. That is itself a finding.</item>
/// </list>
/// </summary>
public sealed class Phase1SpineSeamTests
{
    // ==========================================================================================
    // SEAM 1-2. Ingestion -> lead -> RFQ -> sourcing case
    // ==========================================================================================

    /// <summary>
    /// The upstream half of the spine, walked through the real services: the identity pipeline
    /// establishes the lead (the same call the extraction worker makes), the governed lifecycle
    /// qualifies it, <c>LeadRepository</c> converts it, and the procurement service opens a
    /// sourcing case on the resulting line.
    ///
    /// <para>The assertions are all about CARRIAGE — the case allocated at ingestion, the line's
    /// quantity and unit, and the serial that ties them together — because those are the values a
    /// cut seam drops silently.</para>
    /// </summary>
    [Fact]
    public async Task An_inquiry_becomes_a_lead_an_RFQ_and_a_sourcing_case_carrying_one_case_throughout()
    {
        using var spine = new UpstreamSpine();
        var lead = await spine.EstablishLeadAsync();

        // Ingestion allocated the commercial case. Nothing downstream may invent one.
        Assert.True(lead.CommercialCaseId > 0);
        Assert.False(string.IsNullOrWhiteSpace(lead.CommercialCaseReference));
        Assert.Equal(2, lead.LeadItems.Count);

        var (rfqId, _) = await spine.ConvertAsync(lead.Id);

        await using (var read = spine.Context())
        {
            var rfq = await read.Rfqs.Include(x => x.Rfqitems).SingleAsync(x => x.Id == rfqId);

            // SEAM: the RFQ takes the LEAD's case rather than minting its own.
            Assert.Equal(lead.CommercialCaseId, rfq.CommercialCaseId);
            Assert.Equal(lead.CommercialCaseReference, rfq.NexoraSerial);
            Assert.Equal(lead.CustomerId, rfq.CustomerId);

            // SEAM: the lines survive with their numbers intact. Quantity is the value the whole
            // downstream chain is denominated in; the unit is what makes it mean anything.
            var line = rfq.Rfqitems.Single(x => x.LineItemNo == "00010");
            Assert.Equal(UpstreamSpine.FirstLineQuantity, line.Quantity);
            Assert.Equal("EA", line.UnitOfMeasure);
            Assert.Equal(UpstreamSpine.FirstLinePart, line.ManufacturerPartNumber);

            // THE GAP THAT WAS PINNED HERE, NOW CLOSED. The buyer's required-by date is parsed
            // per row by the spreadsheet parsers and collapsed to the header by
            // CanonicalRfqNormalizer; LeadItem has no delivery-date column, so the value lives on
            // Lead.RequiredDeliveryDate and conversion used to drop it. Rfqitem.RequiredDesiredDate
            // — read by the sourcing case, the inbound SLA sweep and commercial learning — was
            // therefore null on every RFQ line born from ingestion, and its only writer was a
            // manual API payload. Both conversion paths now carry the header date down.
            //
            // Asserted on EVERY line, not just the first: a carriage that reaches one line and not
            // the next is the same defect wearing a smaller hat.
            Assert.All(rfq.Rfqitems, item =>
                Assert.Equal(UpstreamSpine.RequiredDeliveryDate, item.RequiredDesiredDate));
        }

        var sourcingCase = await spine.OpenSourcingCaseAsync(rfqId);

        await using (var read = spine.Context())
        {
            var opened = await read.SourcingCases.SingleAsync(x => x.Id == sourcingCase.Id);

            // SEAM: the sourcing case names the RFQ line it exists for, and carries the same
            // serial. It has no CommercialCaseId column — the case link is the serial string —
            // so the serial is the whole join and must match exactly.
            Assert.Equal(rfqId, opened.RfqId);
            Assert.Equal(lead.CommercialCaseReference, opened.NexoraSerial);
            Assert.Equal(lead.CustomerId, opened.CustomerId);

            // Where the carriage above actually bites: the sourcing case's RequiredOn is copied
            // from Rfqitem.RequiredDesiredDate, which is the date the supplier is asked to quote
            // against. This is the assertion that proves the buyer's date reached procurement
            // rather than merely being stored one table earlier.
            Assert.Equal(UpstreamSpine.RequiredDeliveryDate, opened.RequiredOn);
        }
    }

    // ==========================================================================================
    // SEAMS 5-7. Supplier PO -> inbound shipment -> receipt -> lot -> stock -> despatch -> POD
    //            -> invoice ceiling
    // ==========================================================================================

    /// <summary>
    /// The downstream half, in ONE flow. Each module's output is the next module's only input:
    /// nothing here is seeded between the steps.
    ///
    /// <para>The pivot assertion is the last one. Gate 6 proves stock leaves, Gate 7 proves the
    /// invoice reads accepted — but each proves it against material it seeded itself. This proves
    /// that the units invoiced trace back, unbroken, to units a supplier actually delivered:
    /// receipt -> lot -> hold -> issue -> proof -> ceiling.</para>
    /// </summary>
    [Fact]
    public async Task Material_a_supplier_delivered_is_the_material_that_ships_and_bounds_the_invoice()
    {
        using var spine = new DownstreamSpine();

        // ---- SEAM 5a: supplier PO -> inbound shipment ---------------------------------------
        var (purchaseOrderId, purchaseOrderLineId) = await spine.IssuePurchaseOrderAsync();
        var inbound = await spine.DeclareInboundShipmentAsync(purchaseOrderId, purchaseOrderLineId,
            DownstreamSpine.ReceivedQuantity);
        Assert.Equal(purchaseOrderId, inbound.PurchaseOrderId);

        var onHandBefore = await spine.OnHandAsync();

        // ---- SEAM 5b: goods receipt settles the shipment and creates the lot ----------------
        await spine.PostReceiptAsync(purchaseOrderId, purchaseOrderLineId, DownstreamSpine.ReceivedQuantity);

        long lotId;
        await using (var read = spine.Context())
        {
            // The receipt SETTLES the shipment it never names: the settlement allocates against
            // open inbound lines rather than being told which one. Cut it and the shipment sits
            // permanently outstanding while the warehouse holds the goods.
            var line = await read.SupplierShipmentLines
                .SingleAsync(x => x.SupplierShipmentId == inbound.Id);
            Assert.Equal(DownstreamSpine.ReceivedQuantity, line.ReceivedQuantity);

            // The lot carries its supplier PO — the recall question "who sold us this?" — and its
            // origin, which is the customs/compliance answer.
            var lot = await read.Set<MaterialLot>().SingleAsync(x => x.InventoryId == spine.InventoryId);
            Assert.Equal(purchaseOrderId, lot.SupplierPurchaseOrderId);
            Assert.Equal(purchaseOrderLineId, lot.SupplierPurchaseOrderLineId);
            Assert.Equal(spine.SupplierId, lot.SupplierId);
            Assert.Equal(DownstreamSpine.OriginCountry, lot.CountryOfOrigin);
            Assert.Equal(DownstreamSpine.ReceivedQuantity, lot.QuantityReceived);
            lotId = lot.Id;

            // ...and the same receipt raised the stock the lot sits on.
            var inventory = await read.Set<Models.Inventory>().SingleAsync(x => x.Id == spine.InventoryId);
            Assert.Equal(onHandBefore + DownstreamSpine.ReceivedQuantity, inventory.QtyOnHand);
        }

        // ---- SEAM 6: sales order -> reservation names the lot -------------------------------
        var (orderId, orderItemId) = await spine.RaiseSalesOrderAsync(DownstreamSpine.OrderedQuantity);
        await spine.ReserveAsync(orderId);

        await using (var read = spine.Context())
        {
            var holds = await read.Set<StockReservation>()
                .Where(x => x.OrderId == orderId && x.Status == StockReservationStatus.Active)
                .ToListAsync();
            var held = Assert.Single(holds);
            // A hold that names only an inventory row can be satisfied by units that were later
            // recalled. The hold must name the LOT the receipt created.
            Assert.Equal(lotId, held.MaterialLotId);
            Assert.Equal(DownstreamSpine.OrderedQuantity, held.Quantity);
        }

        // ---- SEAM 6b: goods issue consumes what was named ------------------------------------
        var shipmentId = await spine.DespatchAsync(orderId, orderItemId, DownstreamSpine.OrderedQuantity);

        await using (var read = spine.Context())
        {
            var consumption = await read.Set<MaterialLotConsumption>()
                .SingleAsync(x => x.ShipmentId == shipmentId);
            // The units that left the building are declared against the lot that arrived. This is
            // the join a recall walks; if it is cut, the where-used trace answers nothing.
            Assert.Equal(lotId, consumption.MaterialLotId);
            Assert.Equal(DownstreamSpine.OrderedQuantity, consumption.Quantity);
            Assert.Equal(orderItemId, consumption.OrderItemId);

            var hold = await read.Set<StockReservation>().SingleAsync(x => x.OrderId == orderId);
            Assert.Equal(StockReservationStatus.Consumed, hold.Status);
        }

        // ---- SEAM 7: POD -> accepted quantity -> invoice ceiling ------------------------------
        // The customer signs for less than arrived: one unit refused at the door.
        var accepted = DownstreamSpine.OrderedQuantity - 1m;
        await spine.ConfirmDeliveryAsync(shipmentId, accepted);

        await using (var read = spine.Context())
        {
            var finance = new CommercialFinanceApplicationService(read);

            // The ORDERED ceiling would allow the full quantity and the DESPATCHED ceiling would
            // too — everything ordered went out on the lorry. Only the accepted quantity stops it.
            var refused = await Assert.ThrowsAsync<FinanceConflictException>(() =>
                finance.CreateInvoiceAsync(spine.BusinessUnitId, orderId, "spine-invoice-over",
                    new CreateInvoiceRequest(null, null,
                        [new CreateInvoiceLineRequest(orderItemId, DownstreamSpine.OrderedQuantity)]),
                    "billing"));
            Assert.Contains("accepted", refused.Message, StringComparison.OrdinalIgnoreCase);

            // ...and the accepted quantity bills, so the ceiling is a ceiling and not a wall.
            var invoice = await finance.CreateInvoiceAsync(spine.BusinessUnitId, orderId, "spine-invoice-ok",
                new CreateInvoiceRequest(null, null, [new CreateInvoiceLineRequest(orderItemId, accepted)]),
                "billing");
            Assert.Equal(accepted, Assert.Single(invoice.Lines).Quantity);
        }
    }
}

// ==============================================================================================
// Fixtures
// ==============================================================================================

/// <summary>
/// Lead -> RFQ -> sourcing case, through the services the API and the extraction worker use.
/// Nothing between those three documents is written by hand.
/// </summary>
internal sealed class UpstreamSpine : IDisposable
{
    public const long Tenant = 97_101;
    public const long CustomerId = 97_110;
    public const long ProductId = 97_120;
    public const long CurrencyId = 97_130;
    public const long WarehouseId = 97_140;
    public const int FirstLineQuantity = 40;
    public const string FirstLinePart = "SPINE-VALVE-0001";

    /// <summary>The date the buyer needs the goods by, as stated on the enquiry document.</summary>
    public static readonly DateTime RequiredDeliveryDate = new(2026, 11, 30, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// Anchored to the real clock, deliberately, where every other seeded date in this file is a
    /// literal.
    ///
    /// <para>This value becomes the goods-receipt date, and <c>PostGoodsReceiptAsync</c> refuses a
    /// receipt that precedes its purchase order's issuance event — an event stamped with
    /// <see cref="DateTime.UtcNow"/> when the test issues the order moments earlier. A literal
    /// "today" therefore has a shelf life of one day: this class pinned 2026-08-10 and began
    /// failing at 00:00 UTC on the 11th, in a subsystem nobody had touched, with a message
    /// ("Receipt date cannot precede the PO issuance date") that names the symptom and not the
    /// cause. Any hard-coded date compared against a real-clock value is the same trap.</para>
    /// </summary>
    private static DateTime Now => DateTime.UtcNow;
    private readonly TestDb _database = new();

    public UpstreamSpine()
    {
        using var seed = _database.ContextFor(null);
        seed.Database.ExecuteSqlRaw("PRAGMA ignore_check_constraints = ON");
        var businessUnit = Seed.EnsureBusinessUnit(seed, Tenant);
        seed.SaveChanges();

        // The governed lifecycle picklist. Without it the lead cannot be qualified and the RFQ
        // cannot be drafted — both resolve their status through this catalog.
        seed.SetupMasters.AddRange(LifecycleStatusCatalog.CreateFor(businessUnit, "qa", Now));
        Seed.Customer(seed, CustomerId, Tenant, "Spine Customer");
        seed.Currencies.Add(new Currency
        {
            Id = CurrencyId, BusinessUnitId = Tenant, Code = "SAR", CurrencyName = "Saudi Riyal",
            ExchangeRate = 1m, IsBaseCurrency = true, IsActive = true, CreatedBy = "qa", CreatedOn = Now
        });
        seed.Warehouses.Add(new Warehouse
        {
            Id = WarehouseId, BusinessUnitId = Tenant, WarehouseCode = "SPINE-WH",
            WarehouseName = "Spine Warehouse", IsActive = true, CreatedBy = "qa", CreatedOn = Now
        });
        seed.Products.Add(new Product
        {
            Id = ProductId, Buid = Tenant, PartNo = FirstLinePart, ProductName = "Ball valve 2IN class 300",
            WarehouseId = WarehouseId, QtyOnHand = 0m, ReorderPoint = 0m, IsActive = true,
            CreatedBy = "qa", CreatedOn = Now
        });
        seed.SaveChanges();
    }

    public ErpRfqAutomationContext Context() => _database.ContextFor(Tenant);

    /// <summary>
    /// Establishes the lead through <see cref="ILeadIdentityApplicationService.ReconcileAsync"/> —
    /// the same call <c>ExtractionWorker</c> makes — then walks the real lifecycle to QUALIFIED.
    /// A directly-constructed Lead has no current revision and conversion rightly refuses it, so
    /// building one here would prove nothing about the product.
    /// </summary>
    public async Task<Lead> EstablishLeadAsync()
    {
        const string reference = "SPINE-A-001";
        var candidate = new Lead
        {
            Rfqno = reference,
            BuyersName = "Spine Bid Desk",
            RecDate = Now,
            BidClosingDate = Now.Date.AddDays(21),
            // The buyer's own need-by date, stated in the enquiry. Distinct from BidClosingDate:
            // one is when the quote is due, the other is when the goods are. Seeded on the SOURCE
            // side only — every assertion about it below reads the DESTINATION the product wrote.
            RequiredDeliveryDate = RequiredDeliveryDate,
            LeadSource = "SpineSeamTests",
            CreatedBy = "qa",
            CreatedDate = Now,
            BusinessUnitId = Tenant,
            NoOfLineItems = 2,
            HeaderRemarks = "Two deterministic lines."
        };
        candidate.LeadItems.Add(new LeadItem
        {
            LineItemNo = "00010", ItemMaterialCode = FirstLinePart, ManufacturerPartNumber = FirstLinePart,
            ProductShortDescription = "Ball valve 2IN class 300", Quantity = FirstLineQuantity,
            UnitOfMeasure = "EA", Currency = "SAR", BidClosingDateLine = Now.Date.AddDays(21)
        });
        candidate.LeadItems.Add(new LeadItem
        {
            LineItemNo = "00020", ItemMaterialCode = "SPINE-GASKET-0002",
            ManufacturerPartNumber = "SPINE-GASKET-0002", ProductShortDescription = "Gasket spiral wound 4IN",
            Quantity = 12, UnitOfMeasure = "EA", Currency = "SAR", BidClosingDateLine = Now.Date.AddDays(21)
        });

        var key = $"spine:{Tenant}:{reference}";
        long leadId;
        await using (var context = Context())
        {
            var result = await new LeadIdentityApplicationService(context).ReconcileAsync(candidate,
                new LeadIntakeDescriptor(
                    BatchId: Guid.Parse("97101000-0000-0000-0000-000000000001"),
                    SourceChannel: "ManualUpload", IdempotencyKey: key, ExternalSourceId: key,
                    EmailThreadId: null, SourceSystem: "SpineSeamTests", Sender: null,
                    Subject: $"RFQ {reference}", OriginalFileName: $"{reference}.xlsx",
                    MimeType: "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                    FileSize: 20480, ContentHash: new string('c', 64), SourceDocumentId: null,
                    ExtractionJobId: null, SourceReceivedAtUtc: Now, IngestedAtUtc: Now,
                    ProcessingPath: LeadProcessingPath.Deterministic, ExternalAiUsed: false,
                    ExternalCost: null, ActorType: "Service", ActorId: "qa", CorrelationId: key),
                CancellationToken.None);
            leadId = result.LeadId;
        }

        // Starting state a reviewer produces: the customer is confirmed and the extracted
        // commercial facts are approved. Both are gates on conversion.
        await using (var context = Context())
        {
            var lead = await context.Leads.SingleAsync(x => x.Id == leadId);
            lead.ResolveCommercialIdentity(CustomerId, null, "CUSTOMER_CONFIRMED");
            lead.CommercialFactsVerified = true;
            await context.SaveChangesAsync();
        }

        // Lead status is domain-protected; the policy graph refuses a shortcut to QUALIFIED, so
        // the fixture walks every rung exactly as an operator would.
        foreach (var target in new[] { "PENDING_IDENTIFICATION", "ASSIGNED", "UNDER_REVIEW", "QUALIFIED" })
        {
            await using var context = Context();
            var current = await context.Leads.SingleAsync(x => x.Id == leadId);
            await new LifecycleApplicationService(context).TransitionLeadAsync(
                Tenant, leadId, new LifecycleActor("qa", "SpineSeamTests"),
                new LifecycleTransitionCommand(target, current.LifecycleVersion, null, null,
                    "Seed", $"spine-{leadId}-{target}", $"lead-{leadId}",
                    $"spine-{target.ToLowerInvariant()}:{Tenant}:{leadId}"),
                false, CancellationToken.None);
        }

        await using var verify = Context();
        return await verify.Leads.Include(x => x.LeadItems).SingleAsync(x => x.Id == leadId);
    }

    public async Task<(long RfqId, string Rfqno)> ConvertAsync(long leadId)
    {
        await using var context = Context();
        return await new LeadRepository(context).ConvertLeadToRfqAsync(leadId, Tenant, "qa");
    }

    public async Task<SourcingCaseView> OpenSourcingCaseAsync(long rfqId)
    {
        long rfqItemId;
        await using (var read = Context())
            rfqItemId = await read.Rfqitems.Where(x => x.Rfqid == rfqId && x.LineItemNo == "00010")
                .Select(x => x.Id).SingleAsync();

        // The sourcing case reads the line's product and warehouse to size the shortfall, so the
        // resolution a reviewer would have made is applied first.
        await using (var context = Context())
        {
            var line = await context.Rfqitems.SingleAsync(x => x.Id == rfqItemId);
            line.ProductId = ProductId;
            line.CurrencyId = CurrencyId;
            line.WarehouseId = WarehouseId;
            await context.SaveChangesAsync();
        }

        await using var open = Context();
        return await new ProcurementApplicationService(open).CreateOrOpenSourcingCaseAsync(
            new CreateSourcingCaseCommand(Tenant, rfqId, rfqItemId, SearchLimit: 10,
                SourceEntireQuantity: false, "spine-sourcing", "qa", "corr-spine-sourcing"));
    }

    public void Dispose() => _database.Dispose();
}

/// <summary>
/// Supplier PO -> inbound shipment -> receipt -> lot -> stock -> hold -> issue -> proof ->
/// invoice, sharing ONE inventory row so the material is provably the same material throughout.
///
/// <para>Built on <see cref="ProcurementScenario"/> because the Gate 4 chain (solicitation,
/// supplier quote, award, approval, issue) is already exercised there through its real service;
/// re-implementing it would only add a second way to be wrong.</para>
/// </summary>
internal sealed class DownstreamSpine : IDisposable
{
    public const decimal ReceivedQuantity = 6m;
    public const decimal OrderedQuantity = 4m;
    public const long OrderStatusId = 97_201;
    public const string OriginCountry = "DE";
    public const long ShipmentStatusId = 97_202;

    /// <summary>
    /// Anchored to the real clock, deliberately, where every other seeded date in this file is a
    /// literal.
    ///
    /// <para>This value becomes the goods-receipt date, and <c>PostGoodsReceiptAsync</c> refuses a
    /// receipt that precedes its purchase order's issuance event — an event stamped with
    /// <see cref="DateTime.UtcNow"/> when the test issues the order moments earlier. A literal
    /// "today" therefore has a shelf life of one day: this class pinned 2026-08-10 and began
    /// failing at 00:00 UTC on the 11th, in a subsystem nobody had touched, with a message
    /// ("Receipt date cannot precede the PO issuance date") that names the symptom and not the
    /// cause. Any hard-coded date compared against a real-clock value is the same trap.</para>
    /// </summary>
    private static DateTime Now => DateTime.UtcNow;
    private readonly ProcurementScenario _procurement = new();

    public DownstreamSpine()
    {
        using var seed = Context();
        seed.SetupMasters.AddRange(
            new SetupMaster
            {
                SetupId = OrderStatusId, BusinessUnitId = BusinessUnitId, SetupType = "OrderStatus",
                SetupCode = "CONFIRMED", SetupValue = "CONFIRMED", IsActive = true,
                CreatedBy = "qa", CreatedOn = Now
            },
            new SetupMaster
            {
                SetupId = ShipmentStatusId, BusinessUnitId = BusinessUnitId, SetupType = "ShipmentStatus",
                SetupCode = "OPEN", SetupValue = "Open", IsActive = true, CreatedBy = "qa", CreatedOn = Now
            });
        Seed.Customer(seed, CustomerId, BusinessUnitId, "Spine Downstream Customer");

        // The item master states the origin. It has to be stated SOMEWHERE for the lot to carry
        // one: the receipt UI cannot send a lot declaration at all
        // (Frontend/src/api/services/procurementService.ts postReceipt), and this purchase order
        // line carries no ordered origin, so the item master is the only live source and this
        // assertion is what proves that fallback is wired rather than decorative.
        var product = seed.Products.Single(x => x.Id == ProductId);
        product.CountryOfOrigin = OriginCountry;
        seed.SaveChanges();
    }

    public long BusinessUnitId => _procurement.BusinessUnitId;
    public long SupplierId => 96_050;
    public long ProductId => 96_030;
    public long InventoryId => 96_040;
    public long WarehouseId => 96_020;
    public long CustomerId => 97_210;

    public ErpRfqAutomationContext Context() => _procurement.Context();

    public async Task<decimal> OnHandAsync()
    {
        await using var read = Context();
        return await read.Set<Models.Inventory>().Where(x => x.Id == InventoryId)
            .Select(x => x.QtyOnHand).SingleAsync();
    }

    /// <summary>An approved and issued supplier purchase order, through the real Gate 4 path.</summary>
    public async Task<(long OrderId, long LineId)> IssuePurchaseOrderAsync()
    {
        var order = await _procurement.CreatePurchaseOrderAsync("spine", ReceivedQuantity);
        return (order.Id, await _procurement.PurchaseOrderLineIdAsync(order.Id));
    }

    public async Task<InboundShipmentView> DeclareInboundShipmentAsync(
        long purchaseOrderId, long purchaseOrderLineId, decimal quantity)
    {
        await using var context = Context();
        return await new InboundShipmentApplicationService(context).CreateShipmentAsync(
            new CreateInboundShipmentCommand(BusinessUnitId, purchaseOrderId,
                [new InboundShipmentLineCommand(purchaseOrderLineId, quantity)],
                "spine-inbound", "qa", "corr-spine-inbound",
                CarrierName: "Spine Forwarder", TrackingReference: "BL-SPINE-1", EtaDate: null));
    }

    public async Task PostReceiptAsync(long purchaseOrderId, long purchaseOrderLineId, decimal quantity)
    {
        long version;
        await using (var read = Context())
            version = await read.SupplierPurchaseOrders.Where(x => x.Id == purchaseOrderId)
                .Select(x => x.Version).SingleAsync();

        await using var context = Context();
        await new ProcurementApplicationService(context).PostGoodsReceiptAsync(
            new PostGoodsReceiptCommand(BusinessUnitId, purchaseOrderId, WarehouseId,
                "GRN-SPINE-1", Now, version, [new PostGoodsReceiptLine(purchaseOrderLineId, quantity)],
                "spine-receipt", "qa", "corr-spine-receipt"));
    }

    /// <summary>
    /// A confirmed sales order for the same product, in the same warehouse, so the hold can only
    /// be satisfied by the material the supplier just delivered.
    /// </summary>
    public async Task<(long OrderId, long OrderItemId)> RaiseSalesOrderAsync(decimal quantity)
    {
        await using var context = Context();
        var order = new Order
        {
            OrderNo = "SO-SPINE-1", CustomerId = CustomerId, BusinessUnitId = BusinessUnitId,
            StatusId = OrderStatusId, OrderDate = Now, TotalAmount = quantity * 100m,
            CreatedBy = "qa", CreatedOn = Now, IsActive = true
        };
        order.OrderItems.Add(new OrderItem
        {
            ProductId = ProductId, WarehouseId = WarehouseId, Quantity = quantity, UnitPrice = 100m,
            Discount = 0m, TaxAmount = 0m, TotalAmount = quantity * 100m,
            CreatedBy = "qa", CreatedDate = Now, IsActive = true
        });
        context.Orders.Add(order);
        await context.SaveChangesAsync();
        return (order.Id, order.OrderItems.Single().Id);
    }

    public async Task ReserveAsync(long orderId)
    {
        await using var context = Context();
        await Reservations(context).ReserveOrderAsync(BusinessUnitId, orderId, "qa");
    }

    /// <summary>
    /// The despatch. <c>ShipmentController.CreateShipment</c> owns this orchestration and there is
    /// no application service behind it, so the two calls it makes are reproduced here in the
    /// order and the transaction it uses. Anything the controller does that is NOT reproduced —
    /// the over-shipment ceiling, the SHIPPED status flip — is therefore untested by this file.
    /// </summary>
    public async Task<long> DespatchAsync(long orderId, long orderItemId, decimal quantity)
    {
        await using var context = Context();
        var order = await context.Orders.AsNoTracking().SingleAsync(x => x.Id == orderId);
        var shipment = new Shipment
        {
            ShipmentNo = "DN-SPINE-1", OrderId = orderId, BusinessUnitId = BusinessUnitId,
            StatusId = ShipmentStatusId, ShipmentDate = Now,
            ShippingAddress = "Second Industrial City",
            DeliveryStatus = DeliveryStatuses.Dispatched,
            DeliveryStatusChangedBy = "qa", DeliveryStatusChangedOn = Now,
            CreatedBy = "qa", CreatedOn = Now, IsActive = true
        };
        shipment.InheritCommercialIdentity(order);
        shipment.ShipmentItems.Add(new ShipmentItem
        {
            OrderItemId = orderItemId, Quantity = quantity,
            CreatedBy = "qa", CreatedOn = Now, IsActive = true
        });
        context.Set<Shipment>().Add(shipment);
        await context.SaveChangesAsync();

        var issue = await Reservations(context).ConsumeOrderLinesAsync(BusinessUnitId, orderId,
            new Dictionary<long, decimal> { [orderItemId] = quantity }, "qa", shipment.Id);
        Assert.False(issue.IsShort);
        return shipment.Id;
    }

    public async Task ConfirmDeliveryAsync(long shipmentId, decimal accepted)
    {
        long shipmentItemId;
        decimal despatched;
        await using (var read = Context())
        {
            var line = await read.ShipmentItems.SingleAsync(x => x.ShipmentId == shipmentId);
            shipmentItemId = line.Id;
            despatched = line.Quantity;
        }

        await using var context = Context();
        await new DeliveryConfirmationService(context,
            NullLogger<DeliveryConfirmationService>.Instance).ConfirmAsync(
            BusinessUnitId, shipmentId, "spine-pod",
            new ConfirmDeliveryCommand("Faisal Al-Harbi", "+966 55 000 0000", "Store Keeper", Now,
                null, null, null, null, null, null, null, null,
                [new ConfirmDeliveryLineCommand(shipmentItemId, accepted,
                    accepted < despatched ? DeliveryExceptionReasons.Rejected : null,
                    accepted < despatched ? "One carton refused at the door." : null, null)]),
            "driver");
    }

    // The production object graph, composed exactly as Program.cs composes it.
    private static IOrderStockReservationService Reservations(ErpRfqAutomationContext context)
        => InventoryServices.OrderStock(context);

    public void Dispose() => _procurement.Dispose();
}
