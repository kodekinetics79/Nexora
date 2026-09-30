/**
 * THE margin formula on the client. Owner ruling 2026-09-27: margin is ON COST, where cost is
 * landed cost (base price plus expenses):
 *
 *   sale price = landed cost × (1 + margin / 100)
 *
 * The backend twin is `Backend/ERP_RFQ_Automation/Services/MarginFormula.cs`; a screen that
 * previews a price must use this so the preview and the stored price are the same number.
 */

/** Round to the currency's minor unit (2 dp), half away from zero, like the server. */
export const roundPrice = (value: number): number => {
  const scaled = Math.abs(value) * 100;
  // Guard against binary noise (1.005 * 100 = 100.49999…) before rounding half away from zero.
  const rounded = Math.round(Number(scaled.toFixed(6)));
  return (Math.sign(value) * rounded) / 100;
};

/** The customer's unit price for a landed cost and a margin on cost. 1,870.8333 at 20% → 2,245.00. */
export const saleFromCost = (landedCost: number, marginOnCostPercent: number): number =>
  roundPrice(landedCost * (1 + marginOnCostPercent / 100));

/** The margin on cost a price carries, in percent; null without a cost to measure against. */
export const marginOnCostPercent = (landedCost: number | null | undefined, salePrice: number | null | undefined): number | null =>
  landedCost != null && landedCost > 0 && salePrice != null && Number.isFinite(salePrice)
    ? Math.round(((salePrice - landedCost) / landedCost) * 10000) / 100
    : null;

/** True when a price is below what the goods cost us. False when the cost is unknown. */
export const isBelowCost = (landedCost: number | null | undefined, salePrice: number | null | undefined): boolean =>
  landedCost != null && landedCost > 0 && salePrice != null && salePrice < landedCost;
