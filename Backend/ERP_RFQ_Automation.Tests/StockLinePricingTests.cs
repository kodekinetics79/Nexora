using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Services;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// Owner decisions 2026-09-16 for an RFQ line that stock covers: the price starts from the selling
/// price, else cost plus the company margin; nothing is held (ex stock, subject to prior sale); and
/// the rep sees what the company last quoted for the part and when it last won the order, or that it never has.
/// </summary>
public sealed class StockLinePricingTests
{
    [Fact]
    public async Task A_part_quoted_but_never_won_says_so()
    {
        using var fixture = new ProcurementScenario();
        await using (var setup = fixture.Context())
        {
            var customer = Seed.Customer(setup, 96_900, fixture.BusinessUnitId, "Khobar Power");
            var sent = Status(setup, 96_911, "QuoteStatus", "SENT", fixture.BusinessUnitId);
            AddQuote(setup, fixture, 96_921, "QT-SENT", sent.SetupId, customer.Id, 130m, new DateTime(2026, 9, 10));
            // This RFQ's own sent quote is not "your record": the line already shows it.
            AddQuote(setup, fixture, 96_923, "QT-THIS-RFQ", sent.SetupId, customer.Id, 99m, new DateTime(2026, 9, 15), fixture.RfqId);
            await setup.SaveChangesAsync();
        }
        await using var db = fixture.Context();
        var view = await new StockLinePricingService(db).GetAsync(fixture.BusinessUnitId, fixture.RfqId, fixture.RfqItemId, CancellationToken.None);

        Assert.Equal(130m, view!.TrackRecord.LastQuoted!.UnitPrice);
        Assert.Null(view.TrackRecord.LastWon);
        Assert.Equal((1, 0), (view.TrackRecord.TimesQuoted, view.TrackRecord.TimesWon));
        Assert.Empty(view.History);
        Assert.Equal(("QT-THIS-RFQ", "SENT"), (view.OnQuote?.QuoteNo, view.OnQuote?.State));
    }

    [Fact]
    public async Task Another_accepted_maker_in_stock_is_offered_by_its_part_number_and_nothing_else_is()
    {
        using var fixture = new ProcurementScenario();
        const long siemensId = 96_950, strangerId = 96_951;
        await using (var setup = fixture.Context())
        {
            var line = await setup.Rfqitems.SingleAsync(x => x.Id == fixture.RfqItemId);
            line.ExtraFields = """{"Approved manufacturers": "ABB ELECTRICAL INDUSTRIES CO. LTD (SA): P/N AF96-30-00-13; SIEMENS AG AUTOMATION AND DRIVE (DE): P/N 3RT2046-1AN20"}""";
            foreach (var (id, part) in new[] { (siemensId, "3rt2046-1an20"), (strangerId, "NOT-ACCEPTED-1") })
            {
                setup.Products.Add(new Product { Id = id, Buid = fixture.BusinessUnitId, PartNo = part, ProductName = part, QtyOnHand = 0, ReorderPoint = 0, IsActive = true, CreatedBy = "qa", CreatedOn = DateTime.UtcNow });
                setup.Set<Models.Inventory>().Add(new Models.Inventory { Id = id, Buid = fixture.BusinessUnitId, ProductId = id, WarehouseId = ProcurementTestData.Warehouse, PartNo = part, QtyOnHand = 30, ReorderPoint = 0, UnitCost = 410m, CreatedBy = "qa", CreatedOn = DateTime.UtcNow });
            }
            await setup.SaveChangesAsync();
        }

        await using var db = fixture.Context();
        var service = new StockLinePricingService(db);
        var other = Assert.Single(await service.OtherMakersInStockAsync(fixture.BusinessUnitId, fixture.RfqId, fixture.RfqItemId, CancellationToken.None));
        Assert.Equal((siemensId, "SIEMENS 3RT2046-1AN20", 30m), (other.ProductId, other.Label, other.Free));

        var view = await service.GetAsync(fixture.BusinessUnitId, fixture.RfqId, fixture.RfqItemId, CancellationToken.None, siemensId);
        Assert.Equal(30m, view!.Stock.OnHand);
        Assert.Equal(410m, view.Price.UnitCost);
        Assert.Equal("SIEMENS", view.Maker);
        Assert.Null(await service.GetAsync(fixture.BusinessUnitId, fixture.RfqId, fixture.RfqItemId, CancellationToken.None, strangerId));
    }

    [Fact]
    public async Task A_line_not_in_stock_is_priced_from_the_cheapest_valid_supplier_price()
    {
        using var fixture = new ProcurementScenario(); // the line wants 10, the shelf holds 2
        await using (var setup = fixture.Context())
        {
            var second = AgentSeed.Supplier(setup, 96_960, fixture.BusinessUnitId, "Second Supplier", "second@example.test");
            var expired = AgentSeed.Supplier(setup, 96_961, fixture.BusinessUnitId, "Expired Supplier", "expired@example.test");
            void Price(long id, long supplierId, decimal unitPrice, DateTime? validUntil, int lead) => setup.SupplierQuotedItems.Add(new Models.SupplierQuotedItem
            {
                Id = id, BusinessUnitId = fixture.BusinessUnitId, SupplierId = supplierId, RfqItemId = fixture.RfqItemId,
                ProductId = ProcurementTestData.Product, Quantity = 10m, UnitPrice = unitPrice, CurrencyId = ProcurementTestData.Currency,
                LeadTimeDays = lead, ValidUntil = validUntil, IsActive = true, CreatedBy = "qa", CreatedDate = DateTime.UtcNow
            });
            Price(96_970, ProcurementTestData.Supplier, 120m, DateTime.UtcNow.AddDays(20), 28);
            Price(96_971, second.Id, 100m, DateTime.UtcNow.AddDays(5), 14);
            Price(96_972, expired.Id, 80m, DateTime.UtcNow.AddDays(-3), 7);
            await setup.SaveChangesAsync();
        }

        await using var db = fixture.Context();
        var service = new StockLinePricingService(db);
        await service.SaveStandardMarginAsync(fixture.BusinessUnitId, 25m, "qa", CancellationToken.None);
        var view = await service.GetAsync(fixture.BusinessUnitId, fixture.RfqId, fixture.RfqItemId, CancellationToken.None);

        Assert.False(view!.CoveredByStock);
        Assert.Equal(("SUPPLIER_PLUS_MARGIN", 100m, 125m), (view.Price.Source, view.Price.UnitCost, view.Price.UnitPrice));
        Assert.Equal(["Second Supplier", "QA Supplier", "Expired Supplier"], view.SupplierPrices!.Select(x => x.SupplierName).ToArray());
        Assert.False(view.SupplierPrices!.Last().Valid);
    }

    [Fact]
    public async Task A_cheaper_supplier_price_in_another_currency_is_listed_but_never_becomes_the_cost()
    {
        using var fixture = new ProcurementScenario(); // wants 10, shelf holds 2
        await using (var setup = fixture.Context())
        {
            // Nothing in this window converts: a USD 30 ranked or blended against the quote's own
            // currency by its bare number would be a price nobody was quoted.
            setup.Currencies.Add(new Currency
            {
                Id = 96_990, BusinessUnitId = fixture.BusinessUnitId, Code = "USD", CurrencyName = "US Dollar",
                ExchangeRate = 3.75m, IsBaseCurrency = false, IsActive = true, CreatedBy = "qa", CreatedOn = DateTime.UtcNow
            });
            var foreign = AgentSeed.Supplier(setup, 96_991, fixture.BusinessUnitId, "Houston Valves", "houston@example.test");
            void Price(long id, long supplierId, decimal unitPrice, long currencyId) => setup.SupplierQuotedItems.Add(new Models.SupplierQuotedItem
            {
                Id = id, BusinessUnitId = fixture.BusinessUnitId, SupplierId = supplierId, RfqItemId = fixture.RfqItemId,
                ProductId = ProcurementTestData.Product, Quantity = 10m, UnitPrice = unitPrice, CurrencyId = currencyId,
                LeadTimeDays = 14, ValidUntil = DateTime.UtcNow.AddDays(20), IsActive = true, CreatedBy = "qa", CreatedDate = DateTime.UtcNow
            });
            Price(96_992, foreign.Id, 30m, 96_990);
            Price(96_993, ProcurementTestData.Supplier, 120m, ProcurementTestData.Currency);
            await setup.SaveChangesAsync();
        }

        await using var db = fixture.Context();
        var service = new StockLinePricingService(db);
        await service.SaveStandardMarginAsync(fixture.BusinessUnitId, 25m, "qa", CancellationToken.None);
        var view = await service.GetAsync(fixture.BusinessUnitId, fixture.RfqId, fixture.RfqItemId, CancellationToken.None);

        Assert.Contains(view!.SupplierPrices!, x => x.SupplierName == "Houston Valves" && x.CurrencyCode == "USD");
        // The cost is the quote-currency supplier's 120, blended with the shelf, never the USD 30.
        Assert.NotNull(view.Price.UnitCost);
        Assert.True(view.Price.UnitCost > 30m, $"cost was {view.Price.UnitCost}");
    }

    [Fact]
    public async Task Partly_in_stock_blends_the_stock_cost_with_the_supplier_price()
    {
        using var fixture = new ProcurementScenario(); // wants 10, shelf holds 2
        await using (var setup = fixture.Context())
        {
            (await setup.Set<Models.Inventory>().SingleAsync()).UnitCost = 5m;
            setup.SupplierQuotedItems.Add(new Models.SupplierQuotedItem
            {
                Id = 96_980, BusinessUnitId = fixture.BusinessUnitId, SupplierId = ProcurementTestData.Supplier, RfqItemId = fixture.RfqItemId,
                ProductId = ProcurementTestData.Product, Quantity = 8m, UnitPrice = 6.5m, CurrencyId = ProcurementTestData.Currency,
                LeadTimeDays = 14, ValidUntil = DateTime.UtcNow.AddDays(30), IsActive = true, CreatedBy = "qa", CreatedDate = DateTime.UtcNow
            });
            await setup.SaveChangesAsync();
        }
        await using var db = fixture.Context();
        var service = new StockLinePricingService(db);
        await service.SaveStandardMarginAsync(fixture.BusinessUnitId, 25m, "qa", CancellationToken.None);

        var view = await service.GetAsync(fixture.BusinessUnitId, fixture.RfqId, fixture.RfqItemId, CancellationToken.None);

        Assert.Equal((2m, 8m, 5m), (view!.Partial!.FromStock, view.Partial.ToOrder, view.Partial.StockUnitCost));
        // (2 × 5 + 8 × 6.50) / 10 = 6.20 each, + 25% = 7.75
        Assert.Equal(("BLENDED_PLUS_MARGIN", 6.2m, 7.75m), (view.Price.Source, view.Price.UnitCost, view.Price.UnitPrice));
    }

    [Theory]
    [InlineData(7, "1 week")]
    [InlineData(28, "4 weeks")]
    [InlineData(10, "10 days")]
    [InlineData(1, "1 day")]
    public void Delivery_prints_in_weeks_when_it_divides(int days, string text) =>
        Assert.Equal(text, QuoteService.DeliveryText(days));

    [Theory]
    [InlineData(null, "DRAFT")]
    [InlineData("DRAFT", "DRAFT")]
    [InlineData("SENT", "SENT")]
    [InlineData("ACCEPTED", "DECIDED")]
    [InlineData("REJECTED", "DECIDED")]
    public void A_sent_quote_is_revised_and_a_decided_one_is_final(string? status, string state) =>
        Assert.Equal(state, StockLinePricingService.QuoteState(status));

    [Fact]
    public void Selling_price_wins_then_cost_plus_company_margin_then_cost_alone()
    {
        Assert.Equal(("SELLING_PRICE", 180m), Pick(StockLinePricingService.Suggest(180m, 100m, 25m)));
        Assert.Equal(("COST_PLUS_MARGIN", 125m), Pick(StockLinePricingService.Suggest(null, 100m, 25m)));
        Assert.Equal(("COST_PLUS_MARGIN", 112.35m), Pick(StockLinePricingService.Suggest(0m, 89.88m, 25m)));
        Assert.Equal(("COST_ONLY", 100m), Pick(StockLinePricingService.Suggest(null, 100m, null)));
        Assert.Equal("NONE", StockLinePricingService.Suggest(null, null, 25m).Source);
        Assert.Null(StockLinePricingService.Suggest(null, null, 25m).UnitPrice);

        static (string, decimal?) Pick(StockPriceSuggestion s) => (s.Source, s.UnitPrice);
    }

    [Fact]
    public async Task The_window_shows_the_real_shelf_the_company_margin_price_and_the_last_sold_and_won_prices()
    {
        using var fixture = new ProcurementScenario();
        await using (var setup = fixture.Context())
        {
            var inventory = await setup.Set<Models.Inventory>().SingleAsync();
            inventory.QtyOnHand = 40m;
            inventory.UnitCost = 100m;
            var customer = Seed.Customer(setup, 96_900, fixture.BusinessUnitId, "Khobar Power");
            var accepted = Status(setup, 96_910, "QuoteStatus", "ACCEPTED", fixture.BusinessUnitId);
            var sent = Status(setup, 96_911, "QuoteStatus", "SENT", fixture.BusinessUnitId);
            var draft = Status(setup, 96_912, "QuoteStatus", "DRAFT", fixture.BusinessUnitId);
            var delivered = Status(setup, 96_913, "OrderStatus", "DELIVERED", fixture.BusinessUnitId);
            AddQuote(setup, fixture, 96_920, "QT-WON", accepted.SetupId, customer.Id, 150m, new DateTime(2026, 9, 1));
            AddQuote(setup, fixture, 96_921, "QT-SENT", sent.SetupId, customer.Id, 130m, new DateTime(2026, 9, 10));
            AddQuote(setup, fixture, 96_922, "QT-DRAFT", draft.SetupId, customer.Id, 999m, new DateTime(2026, 9, 12));
            var order = new Order
            {
                Id = 96_930, OrderNo = "SO-1", CustomerId = customer.Id, BusinessUnitId = fixture.BusinessUnitId,
                StatusId = delivered.SetupId, CurrencyId = ProcurementTestData.Currency, OrderDate = new DateTime(2026, 8, 28),
                TotalAmount = 1140m, CreatedBy = "qa", CreatedOn = DateTime.UtcNow, IsActive = true
            };
            order.OrderItems.Add(new OrderItem
            {
                ProductId = ProcurementTestData.Product, Quantity = 8m, UnitPrice = 142.5m, TotalAmount = 1140m, CreatedBy = "qa"
            });
            setup.Orders.Add(order);
            await setup.SaveChangesAsync();
        }

        await using var db = fixture.Context();
        var service = new StockLinePricingService(db);
        await service.SaveStandardMarginAsync(fixture.BusinessUnitId, 25m, "admin@qa", CancellationToken.None);

        var view = await service.GetAsync(fixture.BusinessUnitId, fixture.RfqId, fixture.RfqItemId, CancellationToken.None);

        Assert.NotNull(view);
        Assert.Equal(40m, view!.Stock.OnHand);
        Assert.Equal("QA Warehouse", Assert.Single(view.Stock.Places).Warehouse);
        Assert.Equal("COST_PLUS_MARGIN", view.Price.Source);
        Assert.Equal(125m, view.Price.UnitPrice);
        // The company's own record on the part: last quoted to anyone, last won, never a draft.
        Assert.Equal(("QT-SENT", 130m), (view.TrackRecord.LastQuoted!.Reference, view.TrackRecord.LastQuoted.UnitPrice));
        Assert.Equal(("QT-WON", 150m), (view.TrackRecord.LastWon!.Reference, view.TrackRecord.LastWon.UnitPrice));
        Assert.Equal(2, view.TrackRecord.TimesQuoted);
        Assert.Equal(2, view.TrackRecord.TimesWon); // the accepted quote + an order that came from no quote
        Assert.Equal(["SO-1"], view.History.Select(x => x.Reference).ToArray());
        Assert.DoesNotContain(view.History, x => x.Reference == "QT-DRAFT");

        Assert.Null(await service.GetAsync(fixture.OtherBusinessUnitId, fixture.RfqId, fixture.RfqItemId, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            service.SaveStandardMarginAsync(fixture.BusinessUnitId, -5m, "admin@qa", CancellationToken.None));
    }

    private static SetupMaster Status(ErpRfqAutomationContext db, long id, string type, string code, long tenant)
    {
        var row = new SetupMaster
        {
            SetupId = id, SetupType = type, SetupCode = code, SetupValue = code, BusinessUnitId = tenant,
            IsActive = true, CreatedBy = "qa", CreatedOn = DateTime.UtcNow
        };
        db.SetupMasters.Add(row);
        return row;
    }

    private static void AddQuote(ErpRfqAutomationContext db, ProcurementScenario fixture, long id, string number,
        long statusId, long customerId, decimal price, DateTime on, long? rfqId = null)
    {
        var quote = new Quote
        {
            Id = id, QuoteNo = number, BusinessUnitId = fixture.BusinessUnitId, CustomerId = customerId, StatusId = statusId,
            CurrencyId = ProcurementTestData.Currency, QuoteDate = on, CreatedBy = "qa", CreatedDate = on, Rfqid = rfqId
        };
        quote.QuoteItems.Add(new QuoteItem
        {
            RfqitemId = rfqId is null ? null : fixture.RfqItemId,
            ProductId = ProcurementTestData.Product, Quantity = 10m, UnitPrice = price, TotalAmount = price * 10m, CreatedBy = "qa"
        });
        db.Quotes.Add(quote);
    }
}
