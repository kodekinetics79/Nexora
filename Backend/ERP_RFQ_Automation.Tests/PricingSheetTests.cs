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

    private static string Detail(IActionResult result) =>
        Assert.IsType<ProblemDetails>(Assert.IsType<BadRequestObjectResult>(result).Value).Detail ?? "";
}
