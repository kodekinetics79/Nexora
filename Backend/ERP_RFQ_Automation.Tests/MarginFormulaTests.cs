using ERP_RFQ_Automation.Services;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// Owner ruling 2026-09-27: margin is ON COST — price = landed cost × (1 + margin). The
/// supplier-award path used landed / (1 - margin) (margin on SALE), so the one award-priced line
/// in the pilot data (landed 1,870.8333 at 20%) went out at 2,338.54 instead of 2,245.00.
/// </summary>
public sealed class MarginFormulaTests
{
    [Fact]
    public void Twenty_percent_on_a_landed_cost_of_1870_8333_is_2245_00()
    {
        Assert.Equal(2_245.00m, MarginFormula.SaleFromCost(1_870.8333m, 20m));
        // The old margin-on-sale answer, which must never come back.
        Assert.NotEqual(2_338.54m, MarginFormula.SaleFromCost(1_870.8333m, 20m));
    }

    [Fact]
    public void The_price_is_rounded_to_the_currency_minor_unit_so_printed_qty_times_price_equals_the_line()
    {
        var unit = MarginFormula.SaleFromCost(1_870.8333m, 20m);
        Assert.Equal(2, unit.Scale);
        Assert.Equal(26_940.00m, 12m * unit);
    }

    [Theory]
    [InlineData(100, 0, 100)]
    [InlineData(100, 25, 125)]
    [InlineData(80, 150, 200)]
    public void Sale_is_cost_times_one_plus_margin(decimal cost, decimal margin, decimal expected)
        => Assert.Equal(expected, MarginFormula.SaleFromCost(cost, margin));

    [Fact]
    public void The_inverse_is_the_most_the_goods_may_cost_to_keep_the_margin()
    {
        // 2,245.00 / 1.2 — and 1,870.833333 priced again gives back 2,245.00.
        var maxLanded = MarginFormula.MaxCostForPrice(2_245.00m, 20m);
        Assert.Equal(1_870.833333m, maxLanded);
        Assert.Equal(2_245.00m, MarginFormula.SaleFromCost(maxLanded, 20m));
    }

    [Fact]
    public void Margin_is_measured_against_cost_and_is_null_without_one()
    {
        Assert.Equal(20.00m, MarginFormula.MarginOnCostPercent(100m, 120m));
        Assert.Equal(-10.00m, MarginFormula.MarginOnCostPercent(100m, 90m));
        Assert.Null(MarginFormula.MarginOnCostPercent(null, 120m));
        Assert.Null(MarginFormula.MarginOnCostPercent(0m, 120m));
    }
}
