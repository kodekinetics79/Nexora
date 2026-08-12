import { describe, expect, it } from 'vitest';
import {
  DEFAULT_DISPATCH_TIERS,
  DISPATCH_TIER_OPTIONS,
  dispatchTierOf,
  dispatchTierQueryHint,
  filterSuppliersByTier,
  suppliersHiddenByTier,
  toggleDispatchTier,
  UNCLASSIFIED_TIER,
} from './supplierTierFilter';

const supplier = (id: number, tier?: string | null) => ({ id, tier });

const partner = supplier(1, 'TIER_1_PARTNER');
const extended = supplier(2, 'TIER_2_EXTENDED');
const spot = supplier(3, 'TIER_3_OUT_OF_NETWORK');
const untiered = supplier(4, null);

describe('the tiers a buyer starts with', () => {
  it('pre-selects Tier 1 and Tier 2 and leaves Tier 3 to be turned on', () => {
    expect(DEFAULT_DISPATCH_TIERS).toContain('TIER_1_PARTNER');
    expect(DEFAULT_DISPATCH_TIERS).toContain('TIER_2_EXTENDED');
    expect(DEFAULT_DISPATCH_TIERS).not.toContain('TIER_3_OUT_OF_NETWORK');
  });

  it('keeps every supplier nobody has tiered yet in the default list', () => {
    // This is the state every supplier in the system is in today. A filter whose first act is to
    // hide the whole supplier master is a filter that looks broken.
    expect(DEFAULT_DISPATCH_TIERS).toContain(UNCLASSIFIED_TIER);
    expect(filterSuppliersByTier([untiered], DEFAULT_DISPATCH_TIERS)).toEqual([untiered]);
  });

  it('offers Tier 3 as a control on screen, not as something to go and configure', () => {
    expect(DISPATCH_TIER_OPTIONS.map((option) => option.value)).toEqual([
      'TIER_1_PARTNER',
      'TIER_2_EXTENDED',
      'TIER_3_OUT_OF_NETWORK',
      UNCLASSIFIED_TIER,
    ]);
  });
});

describe('filterSuppliersByTier', () => {
  const all = [partner, extended, spot, untiered];

  it('shortens the list to the tiers the buyer is looking at', () => {
    expect(filterSuppliersByTier(all, DEFAULT_DISPATCH_TIERS)).toEqual([partner, extended, untiered]);
  });

  it('brings the spot suppliers back the moment Tier 3 is turned on', () => {
    const withSpot = toggleDispatchTier(DEFAULT_DISPATCH_TIERS, 'TIER_3_OUT_OF_NETWORK');
    expect(filterSuppliersByTier(all, withSpot)).toEqual(all);
  });

  it('shows everyone when no tier is selected rather than nobody', () => {
    // Turning every button off is a buyer asking to stop narrowing, not asking for an empty screen.
    expect(filterSuppliersByTier(all, [])).toEqual(all);
  });

  it('treats a tier value nobody agreed on as not classified, never as Tier 3', () => {
    const odd = supplier(5, 'TIER_9_SOMETHING');
    expect(dispatchTierOf(odd)).toBe(UNCLASSIFIED_TIER);
    expect(filterSuppliersByTier([odd], ['TIER_3_OUT_OF_NETWORK'])).toEqual([]);
    expect(filterSuppliersByTier([odd], [UNCLASSIFIED_TIER])).toEqual([odd]);
  });

  it('counts what it is holding back, so the buyer is told rather than left guessing', () => {
    expect(suppliersHiddenByTier(all, DEFAULT_DISPATCH_TIERS)).toBe(1);
    expect(suppliersHiddenByTier(all, [])).toBe(0);
  });
});

describe('toggleDispatchTier', () => {
  it('adds and removes one tier without disturbing the others', () => {
    const withSpot = toggleDispatchTier(DEFAULT_DISPATCH_TIERS, 'TIER_3_OUT_OF_NETWORK');
    expect(withSpot).toEqual([
      'TIER_1_PARTNER',
      'TIER_2_EXTENDED',
      'TIER_3_OUT_OF_NETWORK',
      UNCLASSIFIED_TIER,
    ]);
    expect(toggleDispatchTier(withSpot, 'TIER_3_OUT_OF_NETWORK')).toEqual(DEFAULT_DISPATCH_TIERS);
  });
});

describe('dispatchTierQueryHint', () => {
  it('asks the server for nothing while untiered suppliers are wanted', () => {
    // "Not classified" is not a tier, so a server given a list of tiers cannot keep those
    // suppliers in the answer — and today that is every supplier in the system.
    expect(dispatchTierQueryHint(DEFAULT_DISPATCH_TIERS)).toBeUndefined();
    expect(dispatchTierQueryHint([])).toBeUndefined();
  });

  it('narrows the query only once the buyer has excluded untiered suppliers', () => {
    expect(dispatchTierQueryHint(['TIER_1_PARTNER', 'TIER_3_OUT_OF_NETWORK'])).toEqual([
      'TIER_1_PARTNER',
      'TIER_3_OUT_OF_NETWORK',
    ]);
  });
});
