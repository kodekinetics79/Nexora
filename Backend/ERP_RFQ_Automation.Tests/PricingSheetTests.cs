using System.Security.Claims;
using ERP_RFQ_Automation.Controllers;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// The Pricing sheet (owner ruling 2026-09-27): a manager keeps one landed cost and one sale price
/// per part, in a currency they choose, and the RFQ pricing window reads those figures before the
/// stock record's stale copy. Nothing converts currency.
/// </summary>
public sealed class PricingSheetTests
{
    private static PricingSheetController Controller(ErpRfqAutomationContext db, long tenant) => new(db)
    {
        ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(
                    [new Claim("businessUnitId", tenant.ToString()), new Claim(ClaimTypes.Email, "manager@example.test")], "test"))
            }
        }
    };

    [Fact]
    public async Task Saving_a_row_writes_the_landed_cost_the_sale_price_and_their_currency()
    {
        using var fixture = new ProcurementScenario();
        await using (var db = fixture.Context())
        {
            var result = await Controller(db, fixture.BusinessUnitId).Save(new PricingSheetController.SaveCommand(
                [new PricingSheetController.PriceChange(ProcurementTestData.Product, 90m, 120m, ProcurementTestData.Currency)]),
                CancellationToken.None);
            Assert.IsType<OkObjectResult>(result);
        }

        await using var check = fixture.Context();
        var product = await check.Products.SingleAsync(p => p.Id == ProcurementTestData.Product);
        Assert.Equal((90m, 120m, ProcurementTestData.Currency), (product.UnitCost, product.SellingPrice, product.PriceCurrencyId));
        Assert.Equal("manager@example.test", product.ModifiedBy);
    }

    [Fact]
    public async Task A_price_without_a_currency_or_another_companys_part_is_refused_and_nothing_is_saved()
    {
        using var fixture = new ProcurementScenario();
        await using (var db = fixture.Context())
        {
            var controller = Controller(db, fixture.BusinessUnitId);
            var noCurrency = await controller.Save(new PricingSheetController.SaveCommand(
                [new PricingSheetController.PriceChange(ProcurementTestData.Product, 90m, 120m, null)]), CancellationToken.None);
            var otherTenant = await controller.Save(new PricingSheetController.SaveCommand(
                [new PricingSheetController.PriceChange(fixture.OtherProductId, 90m, 120m, ProcurementTestData.Currency)]), CancellationToken.None);
            var belowZero = await controller.Save(new PricingSheetController.SaveCommand(
                [new PricingSheetController.PriceChange(ProcurementTestData.Product, -1m, 120m, ProcurementTestData.Currency)]), CancellationToken.None);

            Assert.Contains("choose the currency", Detail(noCurrency));
            Assert.Contains("not one of your company's parts", Detail(otherTenant));
            Assert.Contains("landed cost must be above 0", Detail(belowZero));
        }

        await using var check = fixture.Context(fixture.OtherBusinessUnitId);
        var others = await check.Products.IgnoreQueryFilters().SingleAsync(p => p.Id == fixture.OtherProductId);
        Assert.Null(others.PriceCurrencyId);
    }

    [Fact]
    public async Task The_sheet_lists_only_the_companys_own_parts_and_counts_the_ones_missing_a_price()
    {
        using var fixture = new ProcurementScenario();
        await using var db = fixture.Context();
        var result = await Controller(db, fixture.BusinessUnitId).Get(null, missingOnly: true, ct: CancellationToken.None);
        var page = Assert.IsType<PricingSheetController.PricingPage>(Assert.IsType<OkObjectResult>(result.Result).Value);

        Assert.Contains(page.Rows, r => r.ProductId == ProcurementTestData.Product);
        Assert.DoesNotContain(page.Rows, r => r.ProductId == fixture.OtherProductId);
        Assert.True(page.MissingCount >= 1);
        Assert.Contains(page.Currencies, c => c.Id == ProcurementTestData.Currency);
    }

    [Fact]
    public async Task The_pricing_window_takes_the_sheets_landed_cost_over_the_stock_records_stale_copy()
    {
        using var fixture = new ProcurementScenario(); // wants 10, shelf holds 2
        await using (var setup = fixture.Context())
        {
            (await setup.Set<Models.Inventory>().SingleAsync()).UnitCost = 5m; // copied when the stock was counted
            var product = await setup.Products.SingleAsync(p => p.Id == ProcurementTestData.Product);
            product.UnitCost = 7m; // corrected on the sheet since
            product.SellingPrice = null;
            product.PriceCurrencyId = ProcurementTestData.Currency;
            await setup.SaveChangesAsync();
        }
        await using var db = fixture.Context();
        var service = new StockLinePricingService(db);
        await service.SaveStandardMarginAsync(fixture.BusinessUnitId, 20m, "qa", CancellationToken.None);

        var view = await service.GetAsync(fixture.BusinessUnitId, fixture.RfqId, fixture.RfqItemId, CancellationToken.None);

        Assert.Equal(("PRICE_SHEET", 7m, 8.4m), (view!.CostSource, view.Price.UnitCost, view.Price.UnitPrice));
    }

    [Fact]
    public async Task A_sheet_price_in_another_currency_is_shown_but_never_used_as_the_quotes_own()
    {
        using var fixture = new ProcurementScenario();
        await using (var setup = fixture.Context())
        {
            setup.Currencies.Add(new Currency
            {
                Id = 97_100, BusinessUnitId = fixture.BusinessUnitId, Code = "USD", CurrencyName = "US Dollar",
                ExchangeRate = 3.75m, IsBaseCurrency = false, IsActive = true, CreatedBy = "qa", CreatedOn = DateTime.UtcNow
            });
            var product = await setup.Products.SingleAsync(p => p.Id == ProcurementTestData.Product);
            product.UnitCost = 30m;
            product.SellingPrice = 50m;
            product.PriceCurrencyId = 97_100;
            (await setup.Set<Models.Inventory>().SingleAsync()).UnitCost = null;
            await setup.SaveChangesAsync();
        }
        await using var db = fixture.Context();
        var view = await new StockLinePricingService(db).GetAsync(fixture.BusinessUnitId, fixture.RfqId, fixture.RfqItemId, CancellationToken.None);

        Assert.Equal(("USD", 30m, 50m, false), (view!.Sheet!.CurrencyCode, view.Sheet.LandedCost, view.Sheet.SalePrice, view.Sheet.Usable));
        Assert.NotEqual("SELLING_PRICE", view.Price.Source);
        Assert.Null(view.Price.UnitCost);
        Assert.Null(view.CostSource);
    }

    private static async Task<long> SeedHandBuiltQuoteAsync(ProcurementScenario fixture, params (long? ProductId, decimal Price)[] lines)
    {
        await using var setup = fixture.Context();
        var quote = new Quote
        {
            Id = 97_200, QuoteNo = "QT-HAND-1", Rfqid = fixture.RfqId, BusinessUnitId = fixture.BusinessUnitId,
            QuoteDate = DateTime.UtcNow, RevisionNo = 1, LifecycleVersion = 1, CreatedBy = "qa", CreatedDate = DateTime.UtcNow
        };
        // Typed by hand and attached to the RFQ: no quote line points at an RFQ line.
        foreach (var (productId, price) in lines)
            quote.QuoteItems.Add(new QuoteItem
            {
                ProductId = productId, ItemDescription = "Hand-typed line", Quantity = 10m, UnitOfMeasure = "EA", UnitPrice = price,
                TaxCategory = ERP_RFQ_Automation.OrderToCash.QuoteLineTaxCategories.Standard, CreatedBy = "qa", CreatedDate = DateTime.UtcNow
            });
        setup.Quotes.Add(quote);
        await setup.SaveChangesAsync();
        return quote.Id;
    }

    [Fact]
    public async Task The_pricing_window_finds_a_hand_built_quotes_line_for_the_same_part()
    {
        using var fixture = new ProcurementScenario();
        await SeedHandBuiltQuoteAsync(fixture, (ProcurementTestData.Product, 150m));

        await using var db = fixture.Context();
        var view = await new StockLinePricingService(db).GetAsync(fixture.BusinessUnitId, fixture.RfqId, fixture.RfqItemId, CancellationToken.None);

        // Before, this line said "Starts the quote draft" while the customer already had SAR 150.
        Assert.Equal(("QT-HAND-1", 150m), (view!.OnQuote!.QuoteNo, view.OnQuote.UnitPrice));
    }

    [Fact]
    public async Task A_price_save_links_the_hand_built_line_for_the_part_and_never_guesses_between_two()
    {
        using var fixture = new ProcurementScenario();
        var quoteId = await SeedHandBuiltQuoteAsync(fixture, (ProcurementTestData.Product, 150m), (null, 20m));

        await using (var db = fixture.Context())
        {
            var quote = await db.Quotes.Include(q => q.QuoteItems).SingleAsync(q => q.Id == quoteId);
            var rfqLine = await db.Rfqitems.SingleAsync(x => x.Id == fixture.RfqItemId);
            var line = await QuoteService.HandBuiltLineForAsync(db, quote, rfqLine, CancellationToken.None);
            Assert.Equal((150m, fixture.RfqItemId), (line!.UnitPrice, line.RfqitemId!.Value));
        }

        using var ambiguous = new ProcurementScenario();
        var twoLinesId = await SeedHandBuiltQuoteAsync(ambiguous, (ProcurementTestData.Product, 150m), (ProcurementTestData.Product, 140m));
        await using var check = ambiguous.Context();
        var twoLines = await check.Quotes.Include(q => q.QuoteItems).SingleAsync(q => q.Id == twoLinesId);
        var sameLine = await check.Rfqitems.SingleAsync(x => x.Id == ambiguous.RfqItemId);
        Assert.Null(await QuoteService.HandBuiltLineForAsync(check, twoLines, sameLine, CancellationToken.None));
    }

    private static async Task SeedRevisedQuoteAsync(ProcurementScenario fixture, bool revisionSent)
    {
        await using var setup = fixture.Context();
        var original = new Quote
        {
            Id = 97_300, QuoteNo = "QT-REV-1", Rfqid = fixture.RfqId, BusinessUnitId = fixture.BusinessUnitId, CurrencyId = ProcurementTestData.Currency,
            QuoteDate = DateTime.UtcNow, SentOn = DateTime.UtcNow.AddDays(-20), TotalAmount = 100m, RevisionNo = 1, LifecycleVersion = 1,
            CreatedBy = "qa", CreatedDate = DateTime.UtcNow.AddDays(-21)
        };
        var revision = new Quote
        {
            Id = 97_301, QuoteNo = "QT-REV-1-R2", Rfqid = fixture.RfqId, BusinessUnitId = fixture.BusinessUnitId, CurrencyId = ProcurementTestData.Currency,
            QuoteDate = DateTime.UtcNow, SentOn = revisionSent ? DateTime.UtcNow.AddDays(-1) : null, TotalAmount = 100m,
            RevisionNo = 2, RevisionOfQuoteId = 97_300, LifecycleVersion = 1, CreatedBy = "qa", CreatedDate = DateTime.UtcNow.AddDays(-2)
        };
        setup.Quotes.AddRange(original, revision);
        await setup.SaveChangesAsync();
    }

    [Fact]
    public async Task A_quote_whose_revision_was_sent_reads_superseded_in_the_list_and_is_not_stale()
    {
        using var fixture = new ProcurementScenario();
        await SeedRevisedQuoteAsync(fixture, revisionSent: true);
        await using var db = fixture.Context();

        var (rows, _) = await new ERP_RFQ_Automation.Repositories.QuoteRepository(db).GetAllAsync(fixture.BusinessUnitId, 1, 50);

        var original = rows.Single(r => r.QuoteNo == "QT-REV-1");
        Assert.Equal(("QT-REV-1-R2", false), (original.SupersededByQuoteNo, original.IsStale));
        Assert.Null(rows.Single(r => r.QuoteNo == "QT-REV-1-R2").SupersededByQuoteNo);
    }

    [Fact]
    public async Task A_revision_still_in_draft_supersedes_nothing_the_customer_still_holds_the_original()
    {
        using var fixture = new ProcurementScenario();
        await SeedRevisedQuoteAsync(fixture, revisionSent: false);
        await using var db = fixture.Context();

        var (rows, _) = await new ERP_RFQ_Automation.Repositories.QuoteRepository(db).GetAllAsync(fixture.BusinessUnitId, 1, 50);

        Assert.Null(rows.Single(r => r.QuoteNo == "QT-REV-1").SupersededByQuoteNo);
    }

    [Fact]
    public async Task The_dashboard_counts_a_revised_quote_once()
    {
        using var fixture = new ProcurementScenario();
        await SeedRevisedQuoteAsync(fixture, revisionSent: true);
        await using var db = fixture.Context();

        var data = await new ERP_RFQ_Automation.Repositories.DashboardRepository(db).GetDashboardDataAsync(fixture.BusinessUnitId);

        // Two rows, one offer: one quote worth 100, not two worth 200.
        var component = Assert.Single(data.Stats.QuoteValueFx!.ByCurrency);
        Assert.Equal((1, 100m), (component.RowCount, component.Subtotal));
    }

    private static string Detail(IActionResult result) =>
        Assert.IsType<ProblemDetails>(Assert.IsType<BadRequestObjectResult>(result).Value).Detail ?? "";
}
