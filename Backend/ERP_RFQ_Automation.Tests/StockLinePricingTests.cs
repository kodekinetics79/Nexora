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
            await setup.SaveChangesAsync();
        }
        await using var db = fixture.Context();
        var view = await new StockLinePricingService(db).GetAsync(fixture.BusinessUnitId, fixture.RfqId, fixture.RfqItemId, CancellationToken.None);

        Assert.Equal(130m, view!.TrackRecord.LastQuoted!.UnitPrice);
        Assert.Null(view.TrackRecord.LastWon);
        Assert.Equal((1, 0), (view.TrackRecord.TimesQuoted, view.TrackRecord.TimesWon));
        Assert.Empty(view.History);
    }

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
        long statusId, long customerId, decimal price, DateTime on)
    {
        var quote = new Quote
        {
            Id = id, QuoteNo = number, BusinessUnitId = fixture.BusinessUnitId, CustomerId = customerId, StatusId = statusId,
            CurrencyId = ProcurementTestData.Currency, QuoteDate = on, CreatedBy = "qa", CreatedDate = on
        };
        quote.QuoteItems.Add(new QuoteItem
        {
            ProductId = ProcurementTestData.Product, Quantity = 10m, UnitPrice = price, TotalAmount = price * 10m, CreatedBy = "qa"
        });
        db.Quotes.Add(quote);
    }
}
