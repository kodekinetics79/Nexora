/**
 * FR-QTM-01 · choosing which suppliers to send an RFQ to, by the tier the customer set.
 *
 * The tier narrows the list a buyer looks at. It is not permission to trade and it is not a
 * refusal: nothing here removes a supplier from the system, blocks a dispatch or feeds an
 * eligibility check. Tier 3 is where the spot suppliers sit — the people a trader calls for an
 * obsolete part or a single-source item — so it is one click away at all times and the count of
 * what the filter is holding back is always on screen.
 *
 * Suppliers nobody has classified yet are included by default. That is the state every supplier in
 * the system is in on the day this ships, and a filter that starts by hiding the entire supplier
 * master is a filter that looks broken.
 */
import { SUPPLIER_TIERS } from '../api/services/supplierService';

/** Not a tier. The bucket for suppliers whose tier has never been set. */
export const UNCLASSIFIED_TIER = 'UNCLASSIFIED';

export type DispatchTier =
  | 'TIER_1_PARTNER'
  | 'TIER_2_EXTENDED'
  | 'TIER_3_OUT_OF_NETWORK'
  | typeof UNCLASSIFIED_TIER;

/** Short labels, because these sit on filter buttons beside a search box, not in a form. */
export const DISPATCH_TIER_OPTIONS: { value: DispatchTier; label: string }[] = [
  { value: 'TIER_1_PARTNER', label: 'Tier 1' },
  { value: 'TIER_2_EXTENDED', label: 'Tier 2' },
  { value: 'TIER_3_OUT_OF_NETWORK', label: 'Tier 3' },
  { value: UNCLASSIFIED_TIER, label: 'Not classified' },
];

/**
 * Tier 1 and Tier 2 pre-selected, and every supplier nobody has tiered yet. Tier 3 starts off and
 * is added by one visible button — pre-selection, never a gate.
 */
export const DEFAULT_DISPATCH_TIERS: DispatchTier[] = [
  'TIER_1_PARTNER',
  'TIER_2_EXTENDED',
  UNCLASSIFIED_TIER,
];

/** A value the server has not agreed to is treated as no classification, never as a tier of its own. */
export const dispatchTierOf = (supplier: { tier?: string | null }): DispatchTier =>
  SUPPLIER_TIERS.some((option) => option.value === supplier.tier)
    ? (supplier.tier as DispatchTier)
    : UNCLASSIFIED_TIER;

/**
 * An empty selection shows everyone. Turning every button off is a buyer saying "stop narrowing
 * this", and answering it with an empty list would be the filter refusing to show suppliers that
 * are perfectly available to quote.
 */
export const filterSuppliersByTier = <T extends { tier?: string | null }>(
  suppliers: T[],
  selected: DispatchTier[],
): T[] =>
  selected.length === 0
    ? suppliers
    : suppliers.filter((supplier) => selected.includes(dispatchTierOf(supplier)));

/** How many suppliers the filter is holding back, so the number is never a surprise. */
export const suppliersHiddenByTier = <T extends { tier?: string | null }>(
  suppliers: T[],
  selected: DispatchTier[],
): number => suppliers.length - filterSuppliersByTier(suppliers, selected).length;

/**
 * The narrowing the server can safely be asked for — and nothing more.
 *
 * "Not classified" is not a tier, so a server asked for a list of tiers has no honest way to keep
 * those suppliers in the answer. Every supplier that exists today is unclassified, so asking the
 * server to narrow while they are wanted risks losing the entire supplier master to a filter that
 * is only ever meant to shorten a list. So the ask goes out only when the buyer has already said
 * they do not want unclassified suppliers; otherwise the screen fetches everything and narrows the
 * candidates itself, which it does in either case.
 */
export const dispatchTierQueryHint = (selected: DispatchTier[]): string[] | undefined =>
  selected.length === 0 || selected.includes(UNCLASSIFIED_TIER)
    ? undefined
    : selected.filter((tier) => tier !== UNCLASSIFIED_TIER);

/** Toggling one tier on or off, keeping the buttons in their fixed left-to-right order. */
export const toggleDispatchTier = (
  selected: DispatchTier[],
  tier: DispatchTier,
): DispatchTier[] =>
  DISPATCH_TIER_OPTIONS.map((option) => option.value).filter((value) =>
    value === tier ? !selected.includes(value) : selected.includes(value),
  );
