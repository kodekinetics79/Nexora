using ERP_RFQ_Automation.CommercialCases.Lifecycle;
using ERP_RFQ_Automation.Delivery;
using ERP_RFQ_Automation.DTOs.QuoteDTOs;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.QuoteDelivery;
using ERP_RFQ_Automation.Repositories;
using ERP_RFQ_Automation.Services;
using ERP_RFQ_Automation.Services.Interfaces;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// Defects found by driving QUOTE → SEND → CLIENT PO → ORDER → SHIPMENT against a disposable
/// PostgreSQL stack (docs/audit/SCENARIOS-QUOTE-TO-CASH-2026-09-04.md). Each test was run
/// against the code as it stood and failed for the reason the audit records; the PostgreSQL-only
/// half of the revision defect lives in <see cref="QuoteRevisionPostgreSqlTests"/>.
/// </summary>
public sealed class QuoteToCashScenarioRegressionTests
{
    private const long Tenant = 99_300;
    private const long DraftStatusId = 99_301;
    private const long SentStatusId = 99_302;
    private const long RejectedStatusId = 99_303;
    private static readonly DateTime Now = new(2026, 9, 4, 9, 0, 0, DateTimeKind.Utc);

    // ------------------------------------------------------------------ revise after a dead delivery

    /// <summary>
    /// The readiness screen tells the rep of a dead-lettered delivery to "issue this quote as a
    /// new revision and send that". The quote is still DRAFT — the worker never reached
    /// FinalizeQuoteDeliveryAsync — and ReviseQuoteAsync refused every draft, so the only exit
    /// the product named was closed. The fixed delivery key means the draft itself can never be
    /// sent again.
    /// </summary>
    [Fact]
    public async Task A_draft_whose_delivery_ended_terminally_can_be_revised_because_that_is_its_only_way_out()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(Tenant);
        var quoteId = SeedQuote(context, DraftStatusId, "QT-DEAD-1");
        context.QuoteDeliveryRequests.Add(new QuoteDeliveryRequest
        {
            BusinessUnitId = Tenant, QuoteId = quoteId, IdempotencyKey = $"quote:{quoteId}:delivery:v1",
            RecipientEmail = "buyer@customer.test", Subject = "Quote", Body = "Quote", AttachmentFileName = "Quote.pdf",
            RequestedOn = Now, AvailableOn = Now, AttemptCount = 1, DeadLetteredOn = Now.AddMinutes(1),
            LastErrorCode = "DeliveryOutcomeUncertain:InvalidOperationException", Version = 2
        });
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        var service = NewQuoteService(context);

        Assert.True((await service.GetRevisionInfoAsync(quoteId, Tenant)).CanRevise);
        var revision = await service.ReviseQuoteAsync(quoteId, Tenant, "rep@nexora.invalid");

        Assert.Equal(2, revision.Version);
        Assert.Equal("QT-DEAD-1-R2", revision.QuoteNo);
        context.ChangeTracker.Clear();
        var row = await context.Quotes.AsNoTracking().SingleAsync(q => q.Id == revision.Id);
        Assert.Equal(quoteId, row.RevisionOfQuoteId);
        Assert.Equal(2, row.RevisionNo);
        Assert.Equal(DraftStatusId, row.StatusId);
    }

    [Fact]
    public async Task A_plain_draft_is_still_edited_rather_than_revised()
    {
        // THE CONTROL: the relaxation is scoped to a terminal delivery, not to every draft.
        using var db = new TestDb();
        await using var context = db.ContextFor(Tenant);
        var quoteId = SeedQuote(context, DraftStatusId, "QT-DRAFT-1");
        context.ChangeTracker.Clear();
        var service = NewQuoteService(context);

        Assert.False((await service.GetRevisionInfoAsync(quoteId, Tenant)).CanRevise);
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.ReviseQuoteAsync(quoteId, Tenant, "rep@nexora.invalid"));
        Assert.Contains("still a draft", refused.Message);
    }

    // ------------------------------------------------------------------ SENT by hand stamps SentOn

    /// <summary>
    /// POST /api/Quote/{id}/status with SENT — a rep recording a quote emailed from Outlook or
    /// handed over on paper — moved the status and nothing else. SentOn stayed null, so
    /// DaysSinceSent was null, IsStale false for ever, and the follow-up sweep never saw the quote.
    /// </summary>
    [Fact]
    public async Task Marking_a_quote_SENT_by_hand_stamps_SentOn_so_staleness_and_follow_up_can_start()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(Tenant);
        var quoteId = SeedQuote(context, DraftStatusId, "QT-HAND-1");
        context.ChangeTracker.Clear();

        var result = await new LifecycleApplicationService(context).TransitionQuoteAsync(
            Tenant, quoteId, new LifecycleActor("rep@nexora.invalid", "AuthenticatedUser"),
            new LifecycleTransitionCommand("SENT", 1, null, "sent by hand", "Api", "corr-hand-1",
                $"quote-{quoteId}", "hand-sent-1"), false, default);

        Assert.Equal("SENT", result.NewStatusCode);
        context.ChangeTracker.Clear();
        var quote = await context.Quotes.AsNoTracking().SingleAsync(q => q.Id == quoteId);
        Assert.NotNull(quote.SentOn);
        Assert.InRange(quote.SentOn!.Value, DateTime.UtcNow.AddMinutes(-2), DateTime.UtcNow.AddMinutes(1));
    }

    [Fact]
    public async Task Other_transitions_leave_SentOn_alone()
    {
        // THE CONTROL: only SENT means "the customer has it".
        using var db = new TestDb();
        await using var context = db.ContextFor(Tenant);
        var quoteId = SeedQuote(context, DraftStatusId, "QT-HAND-2");
        context.ChangeTracker.Clear();

        await new LifecycleApplicationService(context).TransitionQuoteAsync(
            Tenant, quoteId, new LifecycleActor("rep@nexora.invalid", "AuthenticatedUser"),
            new LifecycleTransitionCommand("REJECTED", 1, "NO_BID", "declined", "Api", "corr-hand-2",
                $"quote-{quoteId}", "hand-rejected-2"), false, default);

        context.ChangeTracker.Clear();
        Assert.Null((await context.Quotes.AsNoTracking().SingleAsync(q => q.Id == quoteId)).SentOn);
    }

    // ------------------------------------------------------------------ one quote per RFQ, said plainly

    /// <summary>
    /// The database keeps one quote per RFQ per tenant (UX_Quotes_BusinessUnitID_RFQID). A
    /// second POST /api/Quote on the same RFQ answered 500 "An unexpected error occurred" with a
    /// correlation id — the only refusal on the spine that named nothing.
    /// </summary>
    [Fact]
    public async Task A_second_quote_on_an_RFQ_is_refused_naming_the_quote_that_already_exists()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(Tenant);
        var rfqId = SeedRfq(context);
        context.ChangeTracker.Clear();
        var service = NewQuoteService(context);

        var first = await service.CreateQuoteAsync(NewQuoteRequest(rfqId, "QT-FIRST"));
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.CreateQuoteAsync(NewQuoteRequest(rfqId, "QT-SECOND")));

        Assert.Contains(first.QuoteNo, refused.Message);
        Assert.Contains("one RFQ carries one quote", refused.Message);
        context.ChangeTracker.Clear();
        Assert.Equal(1, await context.Quotes.AsNoTracking().CountAsync(q => q.Rfqid == rfqId));
    }

    // ------------------------------------------------------------------ shipment lists carry their lines

    /// <summary>
    /// GET /api/Shipment and GET /api/Shipment/order/{id} answered <c>items: []</c> for every
    /// shipment while GET /api/Shipment/{id} answered the lines. OrderViewPage sums the by-order
    /// lines to decide whether anything is left to ship, so a fully despatched order kept offering
    /// "Create Shipment" — and any consumer of the list saw a despatch note with nothing on it.
    /// </summary>
    [Fact]
    public async Task The_shipment_list_and_the_by_order_read_carry_the_despatched_lines()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(Tenant);
        var (orderId, shipmentId) = SeedShipment(context);
        context.ChangeTracker.Clear();
        var repository = new ShipmentRepository(context);

        // Each read on a cleared tracker, and asserted before the next: the detail read includes
        // the lines, and EF's identity fix-up would otherwise hang them on the list's instances
        // after the fact — which is exactly how this test passed against the old code once.
        var listed = Assert.Single(await repository.GetAllShipmentsAsync(Tenant), s => s.Id == shipmentId);
        var line = Assert.Single(listed.ShipmentItems);
        Assert.Equal(4m, line.Quantity);
        Assert.Equal("Gate valve DN50", line.OrderItem.Product.ProductName);

        context.ChangeTracker.Clear();
        var byOrder = Assert.Single(await repository.GetShipmentsByOrderIdAsync(orderId, Tenant));
        Assert.Single(byOrder.ShipmentItems);

        context.ChangeTracker.Clear();
        var detail = await repository.GetShipmentByIdAsync(shipmentId, Tenant);
        Assert.Equal(Assert.Single(detail!.ShipmentItems).Id, line.Id);
    }

    // ------------------------------------------------------------------ award-backed order is invoiceable

    /// <summary>
    /// A Client PO confirmed and converted (CustomerAwardApplicationService.ConvertToOrder) raises a
    /// DRAFT order; the first shipment locks that order against every edit, status included
    /// (OrderService: "Order cannot be modified as a shipment has been created"); delivery moves it
    /// to DELIVERED only when EVERY line is fully accepted. One short line and finance was refused
    /// for ever: "The order must be confirmed, completed, shipped, or backed by an accepted customer
    /// quote before invoicing." — on an order the customer had accepted in writing. A confirmed
    /// Client PO is that acceptance. The control that a manual DRAFT is still refused lives in
    /// InvoiceCurrencyGateTests.A_draft_manual_order_is_still_refused_until_it_is_confirmed.
    /// </summary>
    [Fact]
    public async Task An_order_raised_from_a_confirmed_client_PO_is_invoiceable_while_still_DRAFT()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(Tenant);
        var orderId = SeedAwardBackedDraftOrder(context);
        context.ChangeTracker.Clear();
        var finance = new ERP_RFQ_Automation.CommercialFinance.CommercialFinanceApplicationService(context);

        var draft = await finance.CreateInvoiceAsync(Tenant, orderId, "award-backed-invoice",
            new ERP_RFQ_Automation.CommercialFinance.CreateInvoiceRequest(null, null, null), "finance@nexora.invalid");

        Assert.Equal(ERP_RFQ_Automation.CommercialFinance.ReceivableDocumentStatuses.Draft, draft.Status);
        Assert.Equal(orderId, draft.OrderId);
        Assert.Equal(99_350, draft.CurrencyId);
        Assert.Equal(400m, draft.TotalAmount);
    }

    // ------------------------------------------------------------------ plumbing

    /// <summary>
    /// The exact row shape ConvertToOrder writes: SourceType CUSTOMER_AWARD with both QuoteId and
    /// CustomerAwardId (CK_Orders_SourceIdentity), status DRAFT, the award's currency.
    /// </summary>
    private static long SeedAwardBackedDraftOrder(ErpRfqAutomationContext context)
    {
        const long currencyId = 99_350, purchaseOrderId = 99_351, poLineId = 99_352, awardId = 99_353,
            orderId = 99_354, orderItemId = 99_355, draftStatusId = 99_356, warehouseId = 99_357, productId = 99_358;
        var quoteId = SeedQuote(context, SentStatusId, "QT-AWARDED-1");
        var quote = context.Quotes.Include(q => q.QuoteItems).Single(q => q.Id == quoteId);
        context.Currencies.Add(new Currency
        {
            Id = currencyId, BusinessUnitId = Tenant, Code = "USD", CurrencyName = "US Dollar", Symbol = "$",
            ExchangeRate = 1m, IsBaseCurrency = true, IsActive = true, CreatedBy = "tests", CreatedOn = Now
        });
        context.SetupMasters.Add(new SetupMaster
        {
            SetupId = draftStatusId, SetupType = "OrderStatus", SetupCode = "DRAFT", SetupValue = "Draft",
            BusinessUnitId = Tenant, IsActive = true, CreatedBy = "tests", CreatedOn = Now
        });
        context.Warehouses.Add(new Warehouse
        {
            Id = warehouseId, BusinessUnitId = Tenant, WarehouseCode = "WH-AWD", WarehouseName = "Award warehouse",
            IsActive = true, CreatedBy = "tests", CreatedOn = Now
        });
        context.Products.Add(new Product
        {
            Id = productId, Buid = Tenant, PartNo = "PART-AWD", ProductName = "Gasket spiral wound", WarehouseId = warehouseId,
            QtyOnHand = 0m, ReorderPoint = 0m, IsActive = true, CreatedBy = "tests", CreatedOn = Now
        });
        context.SaveChanges();
        var purchaseOrder = new ERP_RFQ_Automation.OrderToCash.CustomerPurchaseOrder
        {
            Id = purchaseOrderId, BusinessUnitId = Tenant, CommercialCaseId = quote.CommercialCaseId!.Value, CustomerId = 99_311,
            InternalNumber = "CPO-AWD", ExternalPoNumber = "EXT-AWD", NormalizedExternalPoNumber = "EXTAWD",
            PoDate = Now, ReceivedOn = Now, CurrencyId = currencyId,
            Status = ERP_RFQ_Automation.OrderToCash.CustomerPurchaseOrderStatuses.FullyAwarded,
            Version = 1, CreatedOn = Now, CreatedBy = "tests", QuoteId = quoteId, RfqId = quote.Rfqid
        };
        purchaseOrder.Lines.Add(new ERP_RFQ_Automation.OrderToCash.CustomerPurchaseOrderLine
        {
            Id = poLineId, BusinessUnitId = Tenant, ExternalLineReference = "1", ProductId = productId,
            Description = "Gasket spiral wound", OrderedQuantity = 4m, UnitPrice = 100m, LineAmount = 400m, Version = 1
        });
        context.CustomerPurchaseOrders.Add(purchaseOrder);
        context.SaveChanges();
        var award = new ERP_RFQ_Automation.OrderToCash.CustomerAward
        {
            Id = awardId, BusinessUnitId = Tenant, AwardNumber = "CA-AWD", CustomerPurchaseOrderId = purchaseOrderId,
            QuoteId = quoteId, CommercialCaseId = quote.CommercialCaseId!.Value, CustomerId = 99_311, CurrencyId = currencyId,
            Status = ERP_RFQ_Automation.OrderToCash.CustomerAwardStatuses.Ordered, ConfirmedOn = Now, ConfirmedBy = "tests",
            Version = 1, CreatedOn = Now, CreatedBy = "tests"
        };
        award.LineAllocations.Add(new ERP_RFQ_Automation.OrderToCash.CustomerAwardLineAllocation
        {
            Id = 99_359, BusinessUnitId = Tenant, CustomerPurchaseOrderLineId = poLineId, QuoteItemId = quote.QuoteItems.Single().Id,
            AwardedQuantity = 4m, UnitPriceSnapshot = 100m, DiscountSnapshot = 0m, TaxSnapshot = 0m, TotalSnapshot = 400m, Version = 1
        });
        context.CustomerAwards.Add(award);
        context.SaveChanges();
        var order = new Order
        {
            Id = orderId, OrderNo = "SO-AWD-1", CustomerId = 99_311, BusinessUnitId = Tenant, StatusId = draftStatusId,
            CurrencyId = currencyId, SourceType = OrderSourceTypes.CustomerAward, QuoteId = quoteId, CustomerAwardId = awardId,
            Rfqid = quote.Rfqid, SubTotal = 400m, DiscountAmount = 0m, TaxAmount = 0m, TotalAmount = 400m, BalanceAmount = 400m,
            OrderDate = Now, CreatedBy = "tests", CreatedOn = Now, IsActive = true
        };
        order.InheritCommercialIdentity(quote);
        order.OrderItems.Add(new OrderItem
        {
            Id = orderItemId, ProductId = productId, WarehouseId = warehouseId, Quantity = 4m, UnitPrice = 100m,
            Discount = 0m, TaxAmount = 0m, TotalAmount = 400m, CreatedBy = "tests", CreatedDate = Now, IsActive = true
        });
        context.Set<Order>().Add(order);
        context.SaveChanges();
        return orderId;
    }

    private static QuoteService NewQuoteService(ErpRfqAutomationContext context)
        => new(context, new SilentEmailService(), null!);

    private static void SeedStatuses(ErpRfqAutomationContext context)
    {
        Seed.EnsureBusinessUnit(context, Tenant);
        foreach (var (id, code, value) in new[]
                 {
                     (DraftStatusId, "DRAFT", "Draft"),
                     (SentStatusId, "SENT", "Sent"),
                     (RejectedStatusId, "REJECTED", "Rejected")
                 })
        {
            if (context.SetupMasters.Local.Any(x => x.SetupId == id) || context.SetupMasters.Any(x => x.SetupId == id)) continue;
            context.SetupMasters.Add(new SetupMaster
            {
                SetupId = id, BusinessUnitId = Tenant, SetupType = "QuoteStatus", SetupCode = code,
                SetupValue = value, IsActive = true, CreatedBy = "tests", CreatedOn = Now
            });
        }
        context.SaveChanges();
    }

    /// <summary>A lead → RFQ pair with a real commercial case, the identity every quote inherits.</summary>
    private static long SeedRfq(ErpRfqAutomationContext context)
    {
        SeedStatuses(context);
        var lead = Seed.Lead(context, 99_310, Tenant);
        Seed.Customer(context, 99_311, Tenant, "Scenario Customer");
        // The RFQ inherits the lead's customer; a lead with no resolved identity has none to give.
        lead.ResolveCommercialIdentity(99_311, null, "MATCHED");
        context.SaveChanges();
        var rfq = new Rfq
        {
            Id = 99_312, Rfqno = "RFQ-SCN-1", RecDate = Now, LeadId = lead.Id, CustomerId = 99_311,
            BusinessUnitId = Tenant, CreatedBy = "tests", CreatedDate = Now
        };
        rfq.InheritCommercialIdentity(lead);
        context.Rfqs.Add(rfq);
        context.SaveChanges();
        return rfq.Id;
    }

    private static long SeedQuote(ErpRfqAutomationContext context, long statusId, string quoteNo)
    {
        var rfqId = SeedRfq(context);
        var rfq = context.Rfqs.Single(x => x.Id == rfqId);
        var quote = new Quote
        {
            Id = 99_320, QuoteNo = quoteNo, Rfqid = rfqId, CustomerId = 99_311, BusinessUnitId = Tenant,
            StatusId = statusId, QuoteDate = Now, ValidUntil = Now.AddDays(30), TotalAmount = 115m,
            LifecycleVersion = 1, RevisionNo = 1, CreatedBy = "tests", CreatedDate = Now
        };
        quote.InheritCommercialIdentity(rfq);
        quote.QuoteItems.Add(new QuoteItem
        {
            Id = 99_321, ItemDescription = "Gasket spiral wound", Quantity = 10m, UnitOfMeasure = "EA",
            UnitPrice = 10m, TaxAmount = 15m, TaxRatePercentApplied = 15m, TotalAmount = 115m,
            TaxCategory = ERP_RFQ_Automation.OrderToCash.QuoteLineTaxCategories.Standard,
            CreatedBy = "tests", CreatedDate = Now
        });
        context.Quotes.Add(quote);
        context.SaveChanges();
        return quote.Id;
    }

    private static QuoteCreateRequestDTO NewQuoteRequest(long rfqId, string quoteNo) => new()
    {
        QuoteNo = quoteNo, RfqId = rfqId, CustomerId = 99_311, BusinessUnitId = Tenant, QuoteDate = Now,
        ValidUntil = Now.AddDays(30), CreatedBy = "tests",
        QuoteItems = [new QuoteItemCreateRequestDTO { ItemDescription = "Gasket", Quantity = 1m, UnitPrice = 10m, TotalAmount = 10m }]
    };

    private static (long OrderId, long ShipmentId) SeedShipment(ErpRfqAutomationContext context)
    {
        const long orderId = 99_330, orderItemId = 99_331, shipmentId = 99_332, shipmentItemId = 99_333;
        Seed.EnsureBusinessUnit(context, Tenant);
        Seed.Customer(context, 99_311, Tenant, "Scenario Customer");
        context.SetupMasters.AddRange(
            new SetupMaster
            {
                SetupId = 99_334, SetupType = "OrderStatus", SetupCode = "CONFIRMED", SetupValue = "Confirmed",
                BusinessUnitId = Tenant, IsActive = true, CreatedBy = "tests", CreatedOn = Now
            },
            new SetupMaster
            {
                SetupId = 99_335, SetupType = "ShipmentStatus", SetupCode = "DISPATCHED", SetupValue = "Dispatched",
                BusinessUnitId = Tenant, IsActive = true, CreatedBy = "tests", CreatedOn = Now
            });
        context.Warehouses.Add(new Warehouse
        {
            Id = 99_336, BusinessUnitId = Tenant, WarehouseCode = "WH-SCN", WarehouseName = "Scenario warehouse",
            IsActive = true, CreatedBy = "tests", CreatedOn = Now
        });
        context.Products.Add(new Product
        {
            Id = 99_337, Buid = Tenant, PartNo = "PART-SCN", ProductName = "Gate valve DN50", WarehouseId = 99_336,
            QtyOnHand = 0m, ReorderPoint = 0m, IsActive = true, CreatedBy = "tests", CreatedOn = Now
        });
        context.Set<Order>().Add(new Order
        {
            Id = orderId, OrderNo = "SO-SCN-1", CustomerId = 99_311, BusinessUnitId = Tenant, StatusId = 99_334,
            TotalAmount = 400m, OrderDate = Now, CreatedBy = "tests", CreatedOn = Now, IsActive = true
        });
        context.Set<OrderItem>().Add(new OrderItem
        {
            Id = orderItemId, OrderId = orderId, ProductId = 99_337, WarehouseId = 99_336, Quantity = 4m,
            UnitPrice = 100m, Discount = 0m, TaxAmount = 0m, TotalAmount = 400m, CreatedBy = "tests",
            CreatedDate = Now, IsActive = true
        });
        context.Set<Shipment>().Add(new Shipment
        {
            Id = shipmentId, ShipmentNo = "DN-SCN-1", OrderId = orderId, BusinessUnitId = Tenant, StatusId = 99_335,
            ShipmentDate = Now, ShippingAddress = "Scenario dock", DeliveryStatus = DeliveryStatuses.Dispatched,
            DeliveryStatusChangedBy = "tests", DeliveryStatusChangedOn = Now, CreatedBy = "tests", CreatedOn = Now,
            IsActive = true
        });
        context.Set<ShipmentItem>().Add(new ShipmentItem
        {
            Id = shipmentItemId, ShipmentId = shipmentId, OrderItemId = orderItemId, Quantity = 4m,
            CreatedBy = "tests", CreatedOn = Now, IsActive = true
        });
        context.SaveChanges();
        return (orderId, shipmentId);
    }

    private sealed class SilentEmailService : IEmailService
    {
        public Task<MailboxPollReport> FetchAndSaveLeadsAsync(long? businessUnitId = null)
            => Task.FromResult(MailboxPollReport.Empty);

        public Task SendEmailAsync(string to, string subject, string body,
            List<(string FileName, byte[] FileContent, string ContentType)> attachments = null!,
            string fromEmail = null!, long? businessUnitId = null) => Task.CompletedTask;
    }
}
