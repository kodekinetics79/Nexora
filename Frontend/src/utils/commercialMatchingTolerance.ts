import type { CommercialPolicyDTO } from '../api/services/commercialPolicyService';

/**
 * FR-COM-04. The client-side mirror of `CommercialMatchingTolerance` (backend,
 * `OrderToCash/CommercialMatchingTolerance.cs`): is the number the buyer wrote close enough to the
 * number we quoted to be rounding rather than a different commercial deal?
 *
 * Why this file exists
 * --------------------
 * The tenant's tolerances were honoured by the server and by the review screen, and ignored by the
 * capture screen — the one place a human can still act cheaply. `CustomerAwardWorkspace` compared
 * against a hardcoded `EPSILON = 0.000001`, so a manager who set 2% on the policy screen still had
 * an operator shown "Price differs" on every sub-halalah rounding difference. That is the defect
 * `CommercialMatchingTolerance` was written to kill, alive one layer up: a control the user
 * configures, that one screen obeys and the next contradicts, is a control nobody believes.
 *
 * These functions take the SAME arguments in the SAME order as the server's, so the two can be read
 * side by side. The capture screen's chips must answer the server's question and no other one.
 */

/** The policy fields this comparison depends on. A subset of the tenant's commercial policy. */
export type CommercialMatchingTolerances = Pick<
  CommercialPolicyDTO,
  'priceTolerancePercent' | 'priceToleranceMinimumAmount' | 'quantityTolerancePercent'
>;

/**
 * The defaults declared on the server entity (`CommercialMatchingPolicy`), used only while the
 * policy request is still in flight or has failed.
 *
 * These are the SERVER's defaults for a tenant that has never saved a policy, so a screen falling
 * back to them shows what an unconfigured tenant actually runs on. It is not a second opinion about
 * what the tolerance should be.
 */
export const DEFAULT_COMMERCIAL_TOLERANCES: CommercialMatchingTolerances = {
  priceTolerancePercent: 2,
  priceToleranceMinimumAmount: 0,
  quantityTolerancePercent: 0,
};

/**
 * Whether `actual` is within tolerance of `reference`.
 *
 * The allowance is `max(percent x |reference|, minimumAmount)` — both together, because a
 * percentage alone misbehaves at small values while an absolute floor alone would absorb real money
 * on a large line. Symmetric on magnitude: rounding has no direction.
 *
 * Zero percent and zero minimum reproduce exact equality, which is what the quantity tolerance
 * defaults to. Negative policy values are clamped, exactly as the server clamps them, so a row
 * edited by direct SQL cannot turn a tolerance into a trigger.
 */
export const withinTolerance = (
  actual: number,
  reference: number,
  percent: number,
  minimumAmount: number,
): boolean => {
  if (!Number.isFinite(actual) || !Number.isFinite(reference)) return false;
  const difference = Math.abs(actual - reference);
  if (difference === 0) return true;
  const allowance = Math.max(
    (Math.max(0, percent) / 100) * Math.abs(reference),
    Math.max(0, minimumAmount),
  );
  return difference <= allowance;
};

/**
 * Whether the unit price on the buyer's PO line matches what we quoted. The percentage is taken
 * against the QUOTED price — the stable side of the comparison, and the figure the manager is
 * thinking of when they type 2%.
 */
export const priceMatches = (
  orderedUnitPrice: number,
  quotedUnitPrice: number,
  policy: CommercialMatchingTolerances,
): boolean => withinTolerance(
  orderedUnitPrice,
  quotedUnitPrice,
  policy.priceTolerancePercent,
  policy.priceToleranceMinimumAmount,
);

/**
 * Whether the quantity WE are awarding matches the quantity the BUYER ordered, within the tenant's
 * quantity tolerance and against the ordered quantity as the reference.
 *
 * That pairing is the server's question (`QuantityMatches(awardedQuantity, orderedQuantity)`), and
 * it is not the one the capture screen used to ask. The chip compared the buyer's ordered quantity
 * against the quotation's REMAINING quantity — "is the buyer ordering less than is left on the
 * quote?", which is a partial award and a perfectly ordinary thing — while the server asks "are we
 * accepting a different quantity from the one they ordered?". Two different questions under one
 * label, so the chip fired on healthy orders and stayed silent on the case it was named for.
 */
export const quantityMatches = (
  awardedQuantity: number,
  orderedQuantity: number,
  policy: CommercialMatchingTolerances,
): boolean => withinTolerance(awardedQuantity, orderedQuantity, policy.quantityTolerancePercent, 0);
