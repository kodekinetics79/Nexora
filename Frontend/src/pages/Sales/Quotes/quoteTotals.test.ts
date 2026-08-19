import { describe, it, expect } from 'vitest';
import { allocateProRata, calculateQuoteTotals, roundCurrency } from './quoteTotals';

/**
 * These numbers are the SAME numbers asserted server-side in
 * `Backend/ERP_RFQ_Automation.Tests/QuoteHeaderDiscountTaxBaseTests.cs`. That is the whole point of
 * the file: the screen and the server used to disagree, and the only durable guard against them
 * drifting apart again is that both are pinned to one worked example.
 */
describe('quote totals — parity with QuoteService.CalculateQuoteTotals', () => {
  it('takes the header discount off the NET, and taxes what is left', () => {
    // 10 x 1,000.00, header 10%, VAT 15%.
    const totals = calculateQuoteTotals(
      [{ quantity: 10, unitPrice: 1000, discountKind: null, discountValue: 0 }],
      'PERCENTAGE',
      10,
      15,
    );

    expect(totals.headerDiscount).toBe(1000);   // was 1150 — 10% of a VAT-inclusive 11,500
    expect(totals.netExcludingTax).toBe(9000);
    expect(totals.totalTax).toBe(1350);         // was 1500 — VAT on the pre-discount base
    expect(totals.grandTotal).toBe(10350);      // the screen used to show 10,500
    expect(totals.lines[0].headerDiscountAllocated).toBe(1000);
    expect(totals.lines[0].taxableBase).toBe(9000);
  });

  it('leaves a quote without a header discount exactly as it was', () => {
    const totals = calculateQuoteTotals(
      [{ quantity: 10, unitPrice: 1000, discountKind: null, discountValue: 0 }],
      null,
      0,
      15,
    );

    expect(totals.headerDiscount).toBe(0);
    expect(totals.netExcludingTax).toBe(10000);
    expect(totals.totalTax).toBe(1500);
    expect(totals.grandTotal).toBe(11500);
  });

  it('allocates a header discount that does not divide cleanly, summing exactly', () => {
    const totals = calculateQuoteTotals(
      [
        { quantity: 1, unitPrice: 33.33, discountKind: null, discountValue: 0 },
        { quantity: 1, unitPrice: 33.33, discountKind: null, discountValue: 0 },
        { quantity: 1, unitPrice: 33.34, discountKind: null, discountValue: 0 },
      ],
      'PERCENTAGE',
      10,
      15,
    );

    const allocated = totals.lines.reduce((sum, l) => sum + l.headerDiscountAllocated, 0);
    // Three independently rounded shares would give 3.33 x 3 = 9.99 against a 10.00 discount.
    expect(roundCurrency(allocated)).toBe(10);
    expect(totals.netExcludingTax).toBe(90);
  });

  it('leaves standard-rated tax UNDERIVED when the tenant has stated no rate', () => {
    const totals = calculateQuoteTotals(
      [{ quantity: 1, unitPrice: 100, discountKind: null, discountValue: 0 }],
      null,
      0,
      null,
    );

    // Null, never zero: a standard-rated supply quoted at zero VAT is the defect the send gate
    // exists to refuse, so the screen must show the same nothing the server records.
    expect(totals.lines[0].taxAmount).toBeNull();
    expect(totals.hasUnderivedTax).toBe(true);
  });

  it('gives a zero-rated line its share of the discount and still derives no tax', () => {
    const totals = calculateQuoteTotals(
      [
        { quantity: 1, unitPrice: 1000, discountKind: null, discountValue: 0 },
        { quantity: 1, unitPrice: 1000, discountKind: null, discountValue: 0, taxCategory: 'ZERO_RATED_EXPORT' },
      ],
      'PERCENTAGE',
      10,
      15,
    );

    expect(totals.lines[0].headerDiscountAllocated).toBe(100);
    expect(totals.lines[1].headerDiscountAllocated).toBe(100);
    expect(totals.lines[0].taxAmount).toBe(135);
    expect(totals.lines[1].taxAmount).toBe(0);
    expect(totals.grandTotal).toBe(900 + 135 + 900);
  });

  it('applies a line discount before the header discount is shared out', () => {
    const totals = calculateQuoteTotals(
      [{ quantity: 2, unitPrice: 500, discountKind: 'PERCENTAGE', discountValue: 10 }],
      'FIXED',
      100,
      15,
    );

    expect(totals.grossSubTotal).toBe(1000);
    expect(totals.totalLineDiscounts).toBe(100);
    expect(totals.headerDiscount).toBe(100);
    expect(totals.netExcludingTax).toBe(800);
    expect(totals.totalTax).toBe(120);
    expect(totals.grandTotal).toBe(920);
  });

  it('never lets a discount larger than the quote produce a negative base', () => {
    const totals = calculateQuoteTotals(
      [{ quantity: 1, unitPrice: 100, discountKind: null, discountValue: 0 }],
      'FIXED',
      5000,
      15,
    );

    expect(totals.headerDiscount).toBe(100);
    expect(totals.netExcludingTax).toBe(0);
    expect(totals.totalTax).toBe(0);
    expect(totals.grandTotal).toBe(0);
  });
});

describe('allocateProRata', () => {
  it('returns zeros when there is nothing to apportion against', () => {
    expect(allocateProRata(10, [0, 0])).toEqual([0, 0]);
    expect(allocateProRata(0, [50, 50])).toEqual([0, 0]);
    expect(allocateProRata(10, [])).toEqual([]);
  });

  it('sums exactly to the amount across many awkward weights', () => {
    const weights = [17.11, 3.29, 88.4, 0.07, 251.03];
    const allocations = allocateProRata(37.77, weights);
    expect(roundCurrency(allocations.reduce((a, b) => a + b, 0))).toBe(37.77);
    // Every share stays within the line it belongs to.
    allocations.forEach((share, i) => expect(share).toBeLessThanOrEqual(weights[i]));
  });

  it('gives the leftover to the largest remainder, not to the first line', () => {
    // 10.00 over 33.33 / 33.33 / 33.34: floors are 3.33 each, one halala left over, and the
    // biggest remainder belongs to the 33.34 line.
    const allocations = allocateProRata(10, [33.33, 33.33, 33.34]);
    expect(allocations[2]).toBe(3.34);
    expect(roundCurrency(allocations.reduce((a, b) => a + b, 0))).toBe(10);
  });
});
