using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.OrderToCash;
using ERP_RFQ_Automation.Repositories;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Tests;

public sealed class CustomerAwardApplicationServiceTests
{
    [Fact]
    public async Task QuoteDetail_ExposesCommercialCaseAndRevisionForAwardCapture()
    {
        using var fixture = new CustomerAwardTestFixture();

        var quote = await new QuoteRepository(fixture.Context)
            .GetByIdAsync(fixture.QuoteId, fixture.BusinessUnitId);

        Assert.True(quote.CommercialCaseId > 0);
        Assert.Equal(1, quote.Version);
    }

    [Fact]
    public async Task CreateConfirmPartialAward_UpdatesBalancesAndPurchaseOrderStatus()
    {
        using var fixture = new CustomerAwardTestFixture();
        var purchaseOrder = await fixture.CreatePurchaseOrderAsync("po-create-partial", 10m);
        var award = await fixture.CreateAwardAsync(purchaseOrder, "award-create-partial", 4m);

        var confirmed = await fixture.Service.ConfirmAwardAsync(fixture.BusinessUnitId, award.Id,
            "award-confirm-partial", "corr-confirm-partial", new(award.Version), "tests");
        var projection = await fixture.Service.GetByQuoteAsync(fixture.BusinessUnitId, fixture.QuoteId);

        Assert.Equal(CustomerAwardStatuses.Confirmed, confirmed.Status);
        Assert.Equal(2, confirmed.Version);
        Assert.Equal(4m, projection.ConfirmedAwardQuantity);
        Assert.Equal(6m, projection.RemainingQuantity);
        Assert.Equal("PARTIALLY_AWARDED", projection.Outcome);
        fixture.Context.ChangeTracker.Clear();
        Assert.Equal(CustomerPurchaseOrderStatuses.PartiallyAwarded,
            (await fixture.Context.CustomerPurchaseOrders.SingleAsync(x => x.Id == purchaseOrder.Id)).Status);
    }

    [Fact]
    public async Task ConfirmAward_RejectsCumulativeOverAllocation()
    {
        using var fixture = new CustomerAwardTestFixture();
        var purchaseOrder = await fixture.CreatePurchaseOrderAsync("po-over", 10m);
        var first = await fixture.CreateAwardAsync(purchaseOrder, "award-over-1", 6m);
        await fixture.Service.ConfirmAwardAsync(fixture.BusinessUnitId, first.Id, "confirm-over-1",
            "corr-confirm-over-1", new(first.Version), "tests");

        fixture.Context.ChangeTracker.Clear();
        var currentPo = await fixture.Context.CustomerPurchaseOrders.AsNoTracking().SingleAsync(x => x.Id == purchaseOrder.Id);
        var second = await fixture.CreateAwardAsync(purchaseOrder with { Version = currentPo.Version }, "award-over-2", 5m);

        var error = await Assert.ThrowsAsync<CustomerAwardConflictException>(() =>
            fixture.Service.ConfirmAwardAsync(fixture.BusinessUnitId, second.Id, "confirm-over-2",
                "corr-confirm-over-2", new(second.Version), "tests"));
        Assert.Contains("remaining quantity", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CreatePurchaseOrder_ReplaysSameCommandAndRejectsChangedHash()
    {
        using var fixture = new CustomerAwardTestFixture();
        var command = fixture.PurchaseOrderCommand(" Customer PO / 42 ", 10m);

        var first = await fixture.Service.CreatePurchaseOrderAsync(fixture.BusinessUnitId, "po-replay",
            "corr-po-replay", command, "tests");
        var replay = await fixture.Service.CreatePurchaseOrderAsync(fixture.BusinessUnitId, "po-replay",
            "corr-po-replay-2", command, "tests");

        Assert.Equal(first.Id, replay.Id);
        Assert.Equal(first.InternalNumber, replay.InternalNumber);
        Assert.Equal(first.ExternalPoNumber, replay.ExternalPoNumber);
        Assert.Equal(first.Lines.Single(), replay.Lines.Single());
        Assert.Equal(1, await fixture.Context.CustomerPurchaseOrders.CountAsync());
        var changed = command with { ExternalPoNumber = "Customer PO / 43" };
        await Assert.ThrowsAsync<CustomerAwardConflictException>(() =>
            fixture.Service.CreatePurchaseOrderAsync(fixture.BusinessUnitId, "po-replay",
                "corr-po-replay-3", changed, "tests"));
    }

    [Fact]
    public async Task CancelConfirmedAward_ReleasesBalanceAndRejectsStaleVersion()
    {
        using var fixture = new CustomerAwardTestFixture();
        var purchaseOrder = await fixture.CreatePurchaseOrderAsync("po-cancel", 10m);
        var award = await fixture.CreateAwardAsync(purchaseOrder, "award-cancel", 7m);
        var confirmed = await fixture.Service.ConfirmAwardAsync(fixture.BusinessUnitId, award.Id,
            "confirm-cancel", "corr-confirm-cancel", new(award.Version), "tests");

        await Assert.ThrowsAsync<CustomerAwardConflictException>(() =>
            fixture.Service.CancelAwardAsync(fixture.BusinessUnitId, award.Id, "cancel-stale",
                "corr-cancel-stale", new(award.Version, "stale"), "tests"));

        var cancelled = await fixture.Service.CancelAwardAsync(fixture.BusinessUnitId, award.Id,
            "cancel-valid", "corr-cancel-valid", new(confirmed.Version, "Customer withdrew award"), "tests");
        var projection = await fixture.Service.GetByQuoteAsync(fixture.BusinessUnitId, fixture.QuoteId);

        Assert.Equal(CustomerAwardStatuses.Cancelled, cancelled.Status);
        Assert.Equal(0m, projection.ConfirmedAwardQuantity);
        Assert.Equal(10m, projection.RemainingQuantity);
        fixture.Context.ChangeTracker.Clear();
        Assert.Equal(CustomerPurchaseOrderStatuses.Confirmed,
            (await fixture.Context.CustomerPurchaseOrders.SingleAsync(x => x.Id == purchaseOrder.Id)).Status);
    }

    [Fact]
    public async Task ConvertToOrder_UsesOnlyAwardedLinesPreservesSourcesAndReplays()
    {
        using var fixture = new CustomerAwardTestFixture(twoQuoteLines: true);
        var purchaseOrder = await fixture.CreatePurchaseOrderAsync("po-convert", 10m);
        var award = await fixture.CreateAwardAsync(purchaseOrder, "award-convert", 3m);
        var confirmed = await fixture.Service.ConfirmAwardAsync(fixture.BusinessUnitId, award.Id,
            "confirm-convert", "corr-confirm-convert", new(award.Version), "tests");

        var order = await fixture.Service.ConvertToOrderAsync(fixture.BusinessUnitId, award.Id,
            "convert-replay", "corr-convert", new(confirmed.Version), "tests");
        var replay = await fixture.Service.ConvertToOrderAsync(fixture.BusinessUnitId, award.Id,
            "convert-replay", "corr-convert-replay", new(confirmed.Version), "tests");

        Assert.Equal(order, replay);
        fixture.Context.ChangeTracker.Clear();
        var persisted = await fixture.Context.Orders.Include(x => x.OrderItems).SingleAsync(x => x.Id == order.Id);
        var item = Assert.Single(persisted.OrderItems);
        Assert.Equal(OrderSourceTypes.CustomerAward, persisted.SourceType);
        Assert.Equal(award.Id, persisted.CustomerAwardId);
        Assert.Equal(3m, item.Quantity);
        Assert.Equal(award.Allocations.Single().Id, item.CustomerAwardLineAllocationId);
        Assert.Equal(1, await fixture.Context.Orders.CountAsync(x => x.CustomerAwardId == award.Id));
    }

    [Fact]
    public async Task FullAward_PreservesAcceptedQuoteHeaderDiscountInOrderTotal()
    {
        using var fixture = new CustomerAwardTestFixture();
        var quote = await fixture.Context.Quotes.Include(x => x.QuoteItems).SingleAsync(x => x.Id == fixture.QuoteId);
        quote.TotalAmount = 895.50m;
        await fixture.Context.SaveChangesAsync();
        var purchaseOrder = await fixture.CreatePurchaseOrderAsync("po-header-discount", 10m);
        var award = await fixture.CreateAwardAsync(purchaseOrder, "award-header-discount", 10m);

        var confirmed = await fixture.Service.ConfirmAwardAsync(fixture.BusinessUnitId, award.Id,
            "confirm-header-discount", "corr-header-discount", new(award.Version), "tests");
        var order = await fixture.Service.ConvertToOrderAsync(fixture.BusinessUnitId, award.Id,
            "convert-header-discount", "corr-convert-header-discount", new(confirmed.Version), "tests");

        fixture.Context.ChangeTracker.Clear();
        var persisted = await fixture.Context.Orders.Include(x => x.OrderItems).SingleAsync(x => x.Id == order.Id);
        Assert.Equal(895.50m, persisted.TotalAmount);
        Assert.Equal(109.50m, persisted.DiscountAmount);
    }

    [Fact]
    public async Task TenantIsolation_HidesProjectionAndMutations()
    {
        using var fixture = new CustomerAwardTestFixture();
        var purchaseOrder = await fixture.CreatePurchaseOrderAsync("po-tenant", 10m);
        var award = await fixture.CreateAwardAsync(purchaseOrder, "award-tenant", 2m);
        using var otherContext = fixture.Database.ContextFor(fixture.BusinessUnitId + 1);
        var otherService = new CustomerAwardApplicationService(otherContext);

        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            otherService.GetByQuoteAsync(fixture.BusinessUnitId + 1, fixture.QuoteId));
        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            otherService.ConfirmAwardAsync(fixture.BusinessUnitId + 1, award.Id, "other-confirm",
                "corr-other-confirm", new(award.Version), "other-user"));
        Assert.Empty(await otherContext.CustomerAwards.ToListAsync());
    }
}

internal sealed class CustomerAwardTestFixture : IDisposable
{
    private const long CustomerId = 880_002;
    private const long CurrencyId = 880_003;
    private const long ProductOneId = 880_004;
    private const long ProductTwoId = 880_005;
    private const long QuoteStatusId = 880_006;
    private const long OrderStatusId = 880_007;
    private const long PaymentStatusId = 880_008;
    private const long LeadId = 880_009;
    private const long RfqId = 880_010;
    private const long QuoteItemOneId = 880_012;
    private const long QuoteItemTwoId = 880_013;
    private static readonly DateTime Now = new(2026, 7, 23, 12, 0, 0, DateTimeKind.Utc);

    public CustomerAwardTestFixture(bool twoQuoteLines = false, TestDb? database = null, long businessUnitId = 880_001)
    {
        Database = database ?? new TestDb();
        OwnsDatabase = database is null;
        BusinessUnitId = businessUnitId;
        Context = Database.ContextFor(BusinessUnitId);
        SeedGraph(Context, BusinessUnitId, twoQuoteLines);
        Service = new CustomerAwardApplicationService(Context);
    }

    public TestDb Database { get; }
    public bool OwnsDatabase { get; }
    public long BusinessUnitId { get; }
    public long QuoteId => 880_011;
    public long QuoteItemId => QuoteItemOneId;
    public ErpRfqAutomationContext Context { get; }
    public CustomerAwardApplicationService Service { get; }

    public CreateCustomerPurchaseOrderCommand PurchaseOrderCommand(string externalNumber, decimal quantity) => new(
        QuoteId, Context.Leads.IgnoreQueryFilters().Single(x => x.Id == LeadId).CommercialCaseId,
        CustomerId, CurrencyId, externalNumber, Now.Date, Now.Date, 0,
        [new("1", ProductOneId, "Awarded widget", quantity, null, 100m, quantity * 100m)]);

    public Task<CustomerPurchaseOrderView> CreatePurchaseOrderAsync(string key, decimal quantity) =>
        Service.CreatePurchaseOrderAsync(BusinessUnitId, key, $"corr-{key}",
            PurchaseOrderCommand($"PO-{key}" , quantity), "tests");

    public Task<CustomerAwardView> CreateAwardAsync(CustomerPurchaseOrderView purchaseOrder, string key, decimal quantity)
        => Service.CreateAwardAsync(BusinessUnitId, key, $"corr-{key}", new(
            purchaseOrder.Id, QuoteId, 0, purchaseOrder.Version, 1,
            [new(purchaseOrder.Lines.Single().Id, QuoteItemOneId, quantity)]), "tests");

    public static void SeedGraph(ErpRfqAutomationContext context, long businessUnitId, bool twoQuoteLines = false)
    {
        if (context.BusinessUnits.IgnoreQueryFilters().Any(x => x.Id == businessUnitId)) return;
        var lead = Seed.Lead(context, LeadId, businessUnitId);
        context.SaveChanges();
        Seed.Customer(context, CustomerId, businessUnitId, "Customer Award Tests");
        context.SaveChanges();
        lead.ResolveCommercialIdentity(CustomerId, null, "VERIFIED");
        context.Currencies.Add(new Currency
        {
            Id = CurrencyId, BusinessUnitId = businessUnitId, Code = "USD", CurrencyName = "US Dollar",
            ExchangeRate = 1m, IsActive = true, CreatedBy = "tests", CreatedOn = Now
        });
        context.Products.AddRange(
            NewProduct(ProductOneId, businessUnitId, "Award Widget 1"),
            NewProduct(ProductTwoId, businessUnitId, "Award Widget 2"));
        context.SetupMasters.AddRange(
            Setup(QuoteStatusId, businessUnitId, "QuoteStatus", "SENT"),
            Setup(OrderStatusId, businessUnitId, "OrderStatus", "DRAFT"),
            Setup(PaymentStatusId, businessUnitId, "PaymentStatus", "UNPAID"));
        var rfq = new Rfq
        {
            Id = RfqId, Rfqno = $"RFQ-{RfqId}", RecDate = Now, LeadId = lead.Id,
            CustomerId = CustomerId, BusinessUnitId = businessUnitId, CreatedBy = "tests", CreatedDate = Now
        };
        rfq.InheritCommercialIdentity(lead);
        context.Rfqs.Add(rfq);
        var quote = new Quote
        {
            Id = 880_011, QuoteNo = "QT-AWARD-TEST", Rfqid = RfqId, CustomerId = CustomerId,
            BusinessUnitId = businessUnitId, QuoteDate = Now, StatusId = QuoteStatusId, CurrencyId = CurrencyId,
            TotalAmount = twoQuoteLines ? 2_000m : 1_000m, CreatedBy = "tests", CreatedDate = Now, RevisionNo = 1
        };
        quote.InheritCommercialIdentity(rfq);
        quote.QuoteItems.Add(NewQuoteItem(QuoteItemOneId, ProductOneId, 10m, 100m));
        if (twoQuoteLines) quote.QuoteItems.Add(NewQuoteItem(QuoteItemTwoId, ProductTwoId, 10m, 100m));
        context.Quotes.Add(quote);
        context.SaveChanges();
    }

    private static Product NewProduct(long id, long businessUnitId, string name) => new()
    {
        Id = id, Buid = businessUnitId, ProductName = name, PartNo = $"PART-{id}", QtyOnHand = 100m,
        ReorderPoint = 1m, IsActive = true, CreatedBy = "tests", CreatedOn = Now
    };

    private static SetupMaster Setup(long id, long businessUnitId, string type, string code) => new()
    {
        SetupId = id, BusinessUnitId = businessUnitId, SetupType = type, SetupCode = code,
        SetupValue = code, IsActive = true, CreatedBy = "tests", CreatedOn = Now
    };

    private static QuoteItem NewQuoteItem(long id, long productId, decimal quantity, decimal unitPrice) => new()
    {
        Id = id, ProductId = productId, ItemDescription = $"Quote line {id}", Quantity = quantity,
        UnitPrice = unitPrice, Discount = 10m, TaxAmount = 5m,
        TotalAmount = quantity * unitPrice - 10m + 5m, CreatedBy = "tests", CreatedDate = Now
    };

    public void Dispose()
    {
        Context.Dispose();
        if (OwnsDatabase) Database.Dispose();
    }
}
