import { describe, expect, it } from 'vitest';
import { SUPPLIER_TIERS, supplierTierLabel } from './supplierService';

describe('supplier tier', () => {
  it('offers exactly the three tiers FR-MDM-03 names', () => {
    expect(SUPPLIER_TIERS.map((tier) => tier.value)).toEqual([
      'TIER_1_PARTNER',
      'TIER_2_EXTENDED',
      'TIER_3_OUT_OF_NETWORK',
    ]);
  });

  it('reads "Not classified" for a supplier nobody has tiered yet', () => {
    // Absence of a tier is a real state. It is not Tier 3, and it must not display as one.
    expect(supplierTierLabel(null)).toBe('Not classified');
    expect(supplierTierLabel(undefined)).toBe('Not classified');
    expect(supplierTierLabel('')).toBe('Not classified');
  });

  it('never renders a raw enum value at a buyer', () => {
    expect(supplierTierLabel('TIER_1_PARTNER')).toBe('Tier 1 — Partner');
    expect(supplierTierLabel('TIER_2_EXTENDED')).toBe('Tier 2 — Extended network');
    expect(supplierTierLabel('TIER_3_OUT_OF_NETWORK')).toBe('Tier 3 — Out of network');
  });

  it('does not invent a label for a value the server has not agreed to', () => {
    expect(supplierTierLabel('TIER_4_UNKNOWN')).toBe('Not classified');
  });
});
