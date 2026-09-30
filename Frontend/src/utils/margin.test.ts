import { describe, expect, it } from 'vitest';
import { isBelowCost, marginOnCostPercent, roundPrice, saleFromCost } from './margin';

describe('margin on cost (owner ruling 2026-09-27)', () => {
  it('prices 1,870.8333 at 20% as 2,245.00, not the margin-on-sale 2,338.54', () => {
    expect(saleFromCost(1870.8333, 20)).toBe(2245);
    expect(saleFromCost(1870.8333, 20)).not.toBe(2338.54);
  });

  it('is cost × (1 + margin)', () => {
    expect(saleFromCost(100, 25)).toBe(125);
    expect(saleFromCost(80, 150)).toBe(200);
    expect(saleFromCost(100, 0)).toBe(100);
  });

  it('rounds half away from zero to 2 dp like the server', () => {
    expect(roundPrice(1.005)).toBe(1.01);
    expect(roundPrice(2.675)).toBe(2.68);
    expect(roundPrice(-1.005)).toBe(-1.01);
  });

  it('measures margin against cost and says nothing without a cost', () => {
    expect(marginOnCostPercent(100, 120)).toBe(20);
    expect(marginOnCostPercent(100, 90)).toBe(-10);
    expect(marginOnCostPercent(0, 120)).toBeNull();
    expect(marginOnCostPercent(null, 120)).toBeNull();
  });

  it('flags a price below a known cost only', () => {
    expect(isBelowCost(100, 99.99)).toBe(true);
    expect(isBelowCost(100, 100)).toBe(false);
    expect(isBelowCost(null, 5)).toBe(false);
    expect(isBelowCost(0, 5)).toBe(false);
  });
});
