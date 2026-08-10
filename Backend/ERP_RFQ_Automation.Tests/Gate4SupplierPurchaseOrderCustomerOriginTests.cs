using ERP_RFQ_Automation.Agent.Models;
using ERP_RFQ_Automation.CommercialCases;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.MultiTenancy;
using ERP_RFQ_Automation.OrderToCash;
using ERP_RFQ_Automation.Procurement;
using ERP_RFQ_Automation.SupplierQuotes;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// FR-COM-07. A supplier purchase order must be able to state the customer it was bought for.
///
/// <para>The three customer keys existed on the entity, in the schema and in a read path, and
/// nothing wrote them — so every supplier order was reachable from a customer only by re-joining
/// through the RFQ, which is precisely the de-facto spine FR-COM-07 exists to invert. These tests
/// fail if the writer is removed again, and they fail from both ends: a customer-demand order that
/// carries no keys, and a stock order that carries any.</para>
/// </summary>
public sealed class Gate4SupplierPurchaseOrderCustomerOriginTests
{
    private static readonly DateTime Now = new(2026, 8, 9, 9, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// The proof of dependence: the three keys are read back from the row the service committed, not
    /// from the command. Delete the writer and this fails on the first assertion.
    /// </summary>
    [Fact]
    public async Task A_customer_demand_purchase_order_carries_the_customer_chain_it_was_bought_for()
    {
        using var fixture = new ProcurementScenario();
        await fixture.AllocateCommercialCaseAsync();
        var award = await fixture.CreateAwardAsync("origin-chain", quantity: 8m);
        var chain = await SeedCustomerChainAsync(fixture, award.Id, linkSourcingDecision: true);

        var draft = await fixture.Execute(service => service.CreatePurchaseOrderAsync(
            fixture.PurchaseOrder([award.Id], "origin-chain-po")));

        await using var verify = fixture.Context();
        var purchaseOrder = await verify.SupplierPurchaseOrders.SingleAsync(x => x.Id == draft.Id);
        Assert.Equal(SupplierPurchaseOrderDemandSources.CustomerDemand, purchaseOrder.DemandSource);
        Assert.Equal(chain.CustomerPurchaseOrderId, purchaseOrder.CustomerPurchaseOrderId);
        Assert.Equal(chain.CustomerOrderId, purchaseOrder.CustomerOrderId);
        Assert.Equal(chain.QuoteId, purchaseOrder.QuoteId);
        Assert.True(purchaseOrder.StatesCustomerOrigin);
    }

    /// <summary>
    /// The customer chain is not derived from the RFQ. A client PO, a sales order and a quotation
    /// all sit on the same RFQ here, and without the governed sourcing decision linking the award to
    /// the customer quote line, none of them may be claimed as this order's origin: a supplier order
    /// naming a customer it cannot prove is worse than one naming none.
    /// </summary>
    [Fact]
    public async Task An_order_whose_awards_reach_no_customer_quotation_states_no_customer_origin()
    {
        using var fixture = new ProcurementScenario();
        await fixture.AllocateCommercialCaseAsync();
        var award = await fixture.CreateAwardAsync("origin-unlinked", quantity: 8m);
        await SeedCustomerChainAsync(fixture, award.Id, linkSourcingDecision: false);

        var draft = await fixture.Execute(service => service.CreatePurchaseOrderAsync(
            fixture.PurchaseOrder([award.Id], "origin-unlinked-po")));

        await using var verify = fixture.Context();
        var purchaseOrder = await verify.SupplierPurchaseOrders.SingleAsync(x => x.Id == draft.Id);
        Assert.Null(purchaseOrder.CustomerPurchaseOrderId);
        Assert.Null(purchaseOrder.CustomerOrderId);
        Assert.Null(purchaseOrder.QuoteId);
        Assert.False(purchaseOrder.StatesCustomerOrigin);

        // The gap is named in the created event rather than left as three silent nulls, so an
        // auditor can tell "nothing linked it" from "the link was removed afterwards".
        var created = await verify.ProcurementEvents
            .SingleAsync(x => x.EventType == "SUPPLIER_PO_CREATED" && x.AggregateId == draft.Id);
        Assert.Contains("customerOriginUnresolved", created.PayloadJson, StringComparison.Ordinal);
        Assert.Contains("customer sourcing decision", created.PayloadJson, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The STOCK half of the asymmetry, enforced rather than described. Replenishment has no
    /// customer, so a customer key on a stock order can only have come from a mis-wired caller and
    /// is refused outright instead of being written and later disbelieved.
    /// </summary>
    [Fact]
    public void A_stock_replenishment_order_refuses_customer_keys()
    {
        var purchaseOrder = new SupplierPurchaseOrder
        {
            BusinessUnitId = 96_001,
            DemandSource = SupplierPurchaseOrderDemandSources.Stock
        };

        var failure = Assert.Throws<InvalidOperationException>(() =>
            purchaseOrder.AttachCustomerOrigin(96_001, 96_204, 96_208, 96_202));

        Assert.Contains("STOCK", failure.Message, StringComparison.Ordinal);
        Assert.Null(purchaseOrder.CustomerPurchaseOrderId);
        Assert.Null(purchaseOrder.CustomerOrderId);
        Assert.Null(purchaseOrder.QuoteId);
        Assert.False(purchaseOrder.StatesCustomerOrigin);

        // And a stock order with nothing to attach is not an error — it is the correct answer.
        purchaseOrder.AttachCustomerOrigin(96_001, null, null, null);
        Assert.False(purchaseOrder.StatesCustomerOrigin);
    }

    /// <summary>
    /// A customer PO id is a tenant-owned key. Carrying one across business units would put another
    /// tenant's commercial chain onto this tenant's supplier paperwork.
    /// </summary>
    [Fact]
    public void A_customer_origin_from_another_business_unit_is_refused()
    {
        var purchaseOrder = new SupplierPurchaseOrder { BusinessUnitId = 96_001 };

        var failure = Assert.Throws<InvalidOperationException>(() =>
            purchaseOrder.AttachCustomerOrigin(96_002, 96_204, 96_208, 96_202));

        Assert.Contains("business unit", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Null(purchaseOrder.CustomerPurchaseOrderId);
    }

    /// <summary>
    /// Re-pointing a committed supplier order at a different customer is not an edit, it is a
    /// different order. The second attempt is refused rather than applied.
    /// </summary>
    [Fact]
    public void A_customer_origin_cannot_be_replaced_once_written()
    {
        var purchaseOrder = new SupplierPurchaseOrder { BusinessUnitId = 96_001 };
        purchaseOrder.AttachCustomerOrigin(96_001, 96_204, 96_208, 96_202);

        var failure = Assert.Throws<InvalidOperationException>(() =>
            purchaseOrder.AttachCustomerOrigin(96_001, 96_304, 96_308, 96_302));

        Assert.Contains("cannot be replaced", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(96_204, purchaseOrder.CustomerPurchaseOrderId);
        Assert.Equal(96_208, purchaseOrder.CustomerOrderId);
        Assert.Equal(96_202, purchaseOrder.QuoteId);
    }

    /// <summary>
    /// A missing key surfaces as a named gap on the case a person actually looks at, never as three
    /// blanks that read like a loading state. The order still appears in the timeline — it declares
    /// the case correctly; what it cannot state is the customer behind it.
    /// </summary>
    [Fact]
    public async Task The_case_timeline_reports_a_customer_demand_order_that_names_no_customer_origin()
    {
        using var db = new TestDb();
        long caseId;
        await using (var seed = db.ContextFor(null))
        {
            var lead = Seed.Lead(seed, 96_401, 96_400);
            await seed.SaveChangesAsync();
            caseId = lead.CommercialCaseId;

            var currency = new Currency
            {
                Id = 96_402, BusinessUnitId = 96_400, Code = "SAR", CurrencyName = "Saudi Riyal",
                ExchangeRate = 1m, IsBaseCurrency = true, IsActive = true, CreatedBy = "qa", CreatedOn = Now
            };
            seed.Currencies.Add(currency);
            AgentSeed.Supplier(seed, 96_403, 96_400, "Gap Supplier", "gap-supplier@example.test");
            var rfq = AgentSeed.Rfq(seed, 96_404, 96_400, "RFQ-GAP");
            rfq.LeadId = lead.Id;
            rfq.InheritCommercialIdentity(lead);
            await seed.SaveChangesAsync();

            seed.SupplierPurchaseOrders.AddRange(
                GapOrder(96_405, "PO-NO-ORIGIN", caseId, rfq.NexoraSerial, quoteId: null),
                GapOrder(96_406, "PO-WITH-ORIGIN", caseId, rfq.NexoraSerial, quoteId: 96_407));
            await seed.SaveChangesAsync();
        }

        await using var context = db.ContextFor(96_400);
        var detail = await new CommercialCaseQueryService(context, new StubTenant(96_400))
            .GetAsync(96_400, caseId, CancellationToken.None);

        Assert.NotNull(detail);
        // Declaring the case is what puts it in the timeline; the missing origin does not remove it.
        Assert.Contains(detail!.Documents, d => d.DocumentType == "SupplierPO" && d.DocumentId == 96_405);
        var gap = Assert.Single(detail.TraceabilityGaps
            .Where(g => g.GapKind == CommercialCaseGapKinds.CustomerOriginMissing));
        Assert.Equal(96_405, gap.DocumentId);
        Assert.Equal("PO-NO-ORIGIN", gap.Reference);
        Assert.Contains("customer demand", gap.Detail, StringComparison.OrdinalIgnoreCase);
    }

    private static SupplierPurchaseOrder GapOrder(
        long id, string number, long caseId, string? serial, long? quoteId) => new()
    {
        Id = id, BusinessUnitId = 96_400, RfqId = 96_404, SupplierId = 96_403, CurrencyId = 96_402,
        PurchaseOrderNumber = number, Status = SupplierPurchaseOrderStatuses.Draft, TotalValue = 100m,
        DemandSource = SupplierPurchaseOrderDemandSources.CustomerDemand,
        CommercialCaseId = caseId, NexoraSerial = serial, QuoteId = quoteId,
        IdempotencyKey = $"gap-{id}", RequestHash = new string('a', 64),
        CreatedOn = Now, CreatedBy = "qa"
    };

    private sealed record CustomerChain(long QuoteId, long CustomerPurchaseOrderId, long CustomerOrderId);

    /// <summary>
    /// Builds the customer half of the spine on top of the procurement fixture: quotation, client
    /// PO, customer award and sales order, all against the RFQ the sourcing award was made on.
    ///
    /// <para><paramref name="linkSourcingDecision"/> controls the ONE row that makes the chain
    /// provable — the immutable bridge from the approved supplier award to the customer quote line.
    /// With it false every customer document still exists and still shares the RFQ, which is exactly
    /// the situation in which guessing would be tempting.</para>
    /// </summary>
    private static async Task<CustomerChain> SeedCustomerChainAsync(
        ProcurementScenario fixture, long sourcingAwardId, bool linkSourcingDecision)
    {
        await using var context = fixture.Context();
        var quotedItem = await context.SupplierQuotedItems.AsNoTracking()
            .SingleAsync(x => x.IsActive && x.BusinessUnitId == fixture.BusinessUnitId);

        Seed.Customer(context, 96_200, fixture.BusinessUnitId, "Customer Origin Tests");
        context.SetupMasters.AddRange(
            new SetupMaster
            {
                SetupId = 96_201, BusinessUnitId = fixture.BusinessUnitId, SetupType = "QuoteStatus",
                SetupCode = "SENT", SetupValue = "SENT", IsActive = true, CreatedBy = "qa", CreatedOn = Now
            },
            new SetupMaster
            {
                SetupId = 96_209, BusinessUnitId = fixture.BusinessUnitId, SetupType = "OrderStatus",
                SetupCode = "OPEN", SetupValue = "OPEN", IsActive = true, CreatedBy = "qa", CreatedOn = Now
            });
        await context.SaveChangesAsync();

        var quote = new Quote
        {
            Id = 96_202, QuoteNo = "QT-ORIGIN", Rfqid = fixture.RfqId, CustomerId = 96_200,
            BusinessUnitId = fixture.BusinessUnitId, QuoteDate = Now, StatusId = 96_201,
            CurrencyId = ProcurementTestData.Currency, TotalAmount = 1_000m, RevisionNo = 1,
            CreatedBy = "qa", CreatedDate = Now
        };
        quote.QuoteItems.Add(new QuoteItem
        {
            Id = 96_203, ProductId = ProcurementTestData.Product, ItemDescription = "Origin quote line",
            Quantity = 8m, UnitPrice = 100m, Discount = 0m, TaxAmount = 0m, TotalAmount = 800m,
            CreatedBy = "qa", CreatedDate = Now
        });
        context.Quotes.Add(quote);

        var purchaseOrder = new CustomerPurchaseOrder
        {
            Id = 96_204, BusinessUnitId = fixture.BusinessUnitId,
            CommercialCaseId = ProcurementTestData.CommercialCase, CustomerId = 96_200,
            InternalNumber = "CPO-ORIGIN", ExternalPoNumber = "EXT-ORIGIN",
            NormalizedExternalPoNumber = "EXTORIGIN", PoDate = Now, ReceivedOn = Now,
            CurrencyId = ProcurementTestData.Currency, Status = CustomerPurchaseOrderStatuses.FullyAwarded,
            Version = 1, CreatedOn = Now, CreatedBy = "qa", QuoteId = 96_202, RfqId = fixture.RfqId
        };
        purchaseOrder.Lines.Add(new CustomerPurchaseOrderLine
        {
            Id = 96_205, BusinessUnitId = fixture.BusinessUnitId, ExternalLineReference = "1",
            ProductId = ProcurementTestData.Product, Description = "Origin PO line",
            OrderedQuantity = 8m, UnitPrice = 100m, LineAmount = 800m, Version = 1
        });
        context.CustomerPurchaseOrders.Add(purchaseOrder);
        await context.SaveChangesAsync();

        var customerAward = new CustomerAward
        {
            Id = 96_206, BusinessUnitId = fixture.BusinessUnitId, AwardNumber = "CA-ORIGIN",
            CustomerPurchaseOrderId = 96_204, QuoteId = 96_202,
            CommercialCaseId = ProcurementTestData.CommercialCase, CustomerId = 96_200,
            CurrencyId = ProcurementTestData.Currency, Status = CustomerAwardStatuses.Ordered,
            ConfirmedOn = Now, ConfirmedBy = "qa", Version = 1, CreatedOn = Now, CreatedBy = "qa"
        };
        customerAward.LineAllocations.Add(new CustomerAwardLineAllocation
        {
            Id = 96_207, BusinessUnitId = fixture.BusinessUnitId, CustomerPurchaseOrderLineId = 96_205,
            QuoteItemId = 96_203, AwardedQuantity = 8m, UnitPriceSnapshot = 100m,
            DiscountSnapshot = 0m, TaxSnapshot = 0m, TotalSnapshot = 800m, Version = 1
        });
        context.CustomerAwards.Add(customerAward);
        await context.SaveChangesAsync();

        context.Set<Order>().Add(new Order
        {
            Id = 96_208, OrderNo = "SO-ORIGIN", CustomerId = 96_200,
            BusinessUnitId = fixture.BusinessUnitId, StatusId = 96_209, TotalAmount = 800m,
            OrderDate = Now, CreatedBy = "qa", CreatedOn = Now, IsActive = true,
            SourceType = OrderSourceTypes.CustomerAward, QuoteId = 96_202, CustomerAwardId = 96_206
        });
        await context.SaveChangesAsync();

        if (linkSourcingDecision)
        {
            context.CustomerQuoteSourcingDecisions.Add(new CustomerQuoteSourcingDecision
            {
                Id = 96_210, BusinessUnitId = fixture.BusinessUnitId, QuoteId = 96_202, QuoteItemId = 96_203,
                RfqId = fixture.RfqId, RfqItemId = fixture.RfqItemId,
                CommercialDemandLineId = quotedItem.CommercialDemandLineId!.Value,
                SourcingCaseId = quotedItem.SourcingCaseId!.Value, SourcingAwardId = sourcingAwardId,
                SupplierQuotedItemId = quotedItem.Id,
                SupplierQuoteId = quotedItem.SourceSupplierQuoteId!.Value,
                SupplierQuoteRevisionId = quotedItem.SourceSupplierQuoteRevisionId!.Value,
                SupplierQuoteLineId = quotedItem.SourceSupplierQuoteLineId!.Value,
                NexoraSerial = "NXR-ORIGIN", Quantity = 8m, SupplierLandedUnitCost = 12m,
                TargetMarginPercent = 20m, CustomerUnitPrice = 100m,
                CurrencyId = ProcurementTestData.Currency, IdempotencyKey = "origin-decision",
                RequestHash = new string('a', 64), Rationale = "Origin test sourcing decision",
                CreatedOn = Now, CreatedBy = "qa", CorrelationId = "corr-origin-decision"
            });
            await context.SaveChangesAsync();
        }

        return new CustomerChain(96_202, 96_204, 96_208);
    }
}
