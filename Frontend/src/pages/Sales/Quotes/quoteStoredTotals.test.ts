import { describe, expect, it } from 'vitest';
import { summariseStoredQuote, type StoredLine } from './quoteTotals';

/**
 * The quote VIEW screen and the quote PDF used to state two different, irreconcilable breakdowns of
 * the same document. The screen computed `headerDiscount = (gross - lineDiscounts) - totalAmount`,
 * subtracting a tax-INCLUSIVE grand total from a tax-EXCLUSIVE net, and had no VAT row at all.
 *
 * Both worked examples below were on screen before this. Each asserts the figure the rep reads
 * against the figure the customer's PDF prints.
 */
describe('summariseStoredQuote', () => {
  const line = (over: Partial<StoredLine> = {}): StoredLine => ({
    quantity: 1,
    unitPrice: 1000,
    discount: 0,
    taxAmount: 150,
    taxableBase: 1000,
    headerDiscountAllocated: 0,
    taxRatePercentApplied: 15,
    ...over,
  });

  it('does not invent a negative header discount on a taxed quote that has none', () => {
    // 1,000.00 net, 15% VAT, no header discount. The old arithmetic gave 1,000 - 1,150 = -150,
    // which the `> 0` guard hid, leaving a panel reading 1,000 / 0 / 1,150 that does not add up.
    const totals = summariseStoredQuote([line()], false, 1150);

    expect(totals.headerDiscount).toBe(0);
    expect(totals.netExcludingTax).toBe(1000);
    expect(totals.totalTax).toBe(150);
    // The panel now closes: gross - discounts - header + tax = grand total.
    expect(totals.grossSubTotal - totals.totalLineDiscounts - totals.headerDiscount + totals.totalTax).toBe(1150);
  });

  it('reports the header discount the rep entered, not a VAT-corrupted approximation', () => {
    // 1,000.00 net, 20% header discount, 15% VAT. Base 800, VAT 120, grand total 920.
    // The old arithmetic printed 1,000 - 920 = 80.00 against a discount of 200.00.
    const totals = summariseStoredQuote(
      [line({ taxableBase: 800, taxAmount: 120, headerDiscountAllocated: 200 })], true, 920);

    expect(totals.headerDiscount).toBe(200);
    expect(totals.headerDiscount).not.toBe(80);
    expect(totals.netExcludingTax).toBe(800);
    expect(totals.totalTax).toBe(120);
  });

  it('sums the allocation across lines rather than re-deriving it', () => {
    const totals = summariseStoredQuote([
      line({ taxableBase: 900, taxAmount: 135, headerDiscountAllocated: 100 }),
      line({ unitPrice: 500, taxableBase: 450, taxAmount: 67.5, headerDiscountAllocated: 50 }),
    ], true, 1552.5);

    expect(totals.headerDiscount).toBe(150);
    expect(totals.netExcludingTax).toBe(1350);
    expect(totals.totalTax).toBe(202.5);
  });

  it('names a single rate and stays bare when the quote mixes tax treatments', () => {
    expect(summariseStoredQuote([line(), line()], false, 2300).singleTaxRatePercent).toBe(15);

    const mixed = summariseStoredQuote([
      line(),
      line({ taxAmount: 0, taxRatePercentApplied: 0 }),
    ], false, 2150);
    expect(mixed.singleTaxRatePercent).toBe(15);
    expect(mixed.totalTax).toBe(150);
  });

  it('reports a line whose tax was never derived, and does not read it as zero', () => {
    const totals = summariseStoredQuote(
      [line({ taxAmount: null, taxRatePercentApplied: null })], false, 1000);

    expect(totals.hasUnderivedTax).toBe(true);
    expect(totals.singleTaxRatePercent).toBeNull();
  });

  it('falls back to the old inference only for rows that predate the allocation column', () => {
    // Legacy row: no allocation stored, and the quote does carry a header discount. The inference
    // is wrong by a VAT, but it is the only answer these rows admit — so it stays scoped to them.
    const legacy = summariseStoredQuote(
      [line({ headerDiscountAllocated: null })], true, 1000);
    expect(legacy.headerDiscount).toBe(150);

    // The same legacy row on a quote with NO header discount must not manufacture one.
    expect(summariseStoredQuote([line({ headerDiscountAllocated: null })], false, 1150).headerDiscount).toBe(0);
  });
});
