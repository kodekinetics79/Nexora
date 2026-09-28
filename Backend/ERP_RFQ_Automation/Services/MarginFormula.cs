namespace ERP_RFQ_Automation.Services;

/// <summary>
/// THE margin formula. Owner ruling 2026-09-27: margin is ON COST, where cost is landed cost
/// (base price plus freight, duty and other captured expenses):
///
/// <code>sale price = landed cost × (1 + margin / 100)</code>
///
/// <para>Why one file. The stock path and the pricing sheet already priced on cost, while the
/// supplier-award path priced on SALE (<c>landed / (1 - margin)</c>) — so the same rep typing the
/// same "20%" got 2,245.00 on one screen and 2,338.54 on the other, and the one award-priced quote
/// in the pilot data went out 4.2% over what the owner's rule allows. Every call site now asks this
/// class, so the two can never drift apart again. The frontend twin is
/// <c>Frontend/src/utils/margin.ts</c>.</para>
///
/// <para>Customer prices are rounded to the currency's minor unit (2 dp) here, once, so the
/// printed quantity × printed unit price always equals the printed line total. The award path
/// used to store 6 dp (2,338.541625) and the PDF then printed 12 × 2,338.54 = 28,062.50.</para>
/// </summary>
public static class MarginFormula
{
    /// <summary>Decimal places of a customer unit price: the currency's minor unit.</summary>
    public const int PriceDecimals = 2;

    /// <summary>
    /// The customer's unit price for a landed cost and a margin on cost, rounded to 2 dp.
    /// 1,870.8333 at 20% → 2,245.00.
    /// </summary>
    public static decimal SaleFromCost(decimal landedCost, decimal marginOnCostPercent)
        => decimal.Round(landedCost * (1m + marginOnCostPercent / 100m), PriceDecimals,
            MidpointRounding.AwayFromZero);

    /// <summary>
    /// The inverse: the most one unit may cost (landed) for <paramref name="salePrice"/> to still
    /// carry <paramref name="marginOnCostPercent"/>: <c>price / (1 + margin / 100)</c>.
    /// </summary>
    public static decimal MaxCostForPrice(decimal salePrice, decimal marginOnCostPercent, int decimals = 6)
        => decimal.Round(salePrice / (1m + marginOnCostPercent / 100m), decimals, MidpointRounding.AwayFromZero);

    /// <summary>
    /// The margin on cost a price carries: <c>(price - cost) / cost × 100</c>, to 2 dp. Null when
    /// there is no cost to measure against — never a made-up zero.
    /// </summary>
    public static decimal? MarginOnCostPercent(decimal? landedCost, decimal? salePrice)
        => landedCost is > 0m && salePrice is not null
            ? decimal.Round((salePrice.Value - landedCost.Value) / landedCost.Value * 100m, 2,
                MidpointRounding.AwayFromZero)
            : null;
}
