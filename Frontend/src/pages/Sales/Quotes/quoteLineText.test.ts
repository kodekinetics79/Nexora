import { describe, expect, it } from 'vitest';
import { DRAFT_REVIEW_PLACEHOLDER, buyerIdentityLine, buyerNotes, lineTotalExVat, pricingStatusText } from './quoteLineText';

/** The quote screen says what the customer's PDF says for the same line (pilot audit UX-04, CB-13, UX-03). */
describe('quote line text', () => {
  it('uses the ex-VAT taxable base as the line total, not the VAT-inclusive stored total', () => {
    // QuoteItems 56 on the audit copy: 2 x 1,250.50 = 2,501.00 net, 375.15 VAT, 2,876.15 stored.
    expect(lineTotalExVat({ totalAmount: 2876.15, taxAmount: 375.15, taxableBase: 2501 })).toBe(2501);
    expect(lineTotalExVat({ totalAmount: 2876.15, taxAmount: 375.15 })).toBeCloseTo(2501, 2);
  });

  it('names the buyer material, maker and part number, stored first and requested as the fallback', () => {
    expect(buyerIdentityLine({
      customerMaterialCode: '905750742', manufacturerName: 'SAFT', manufacturerPartNumber: 'LS14500-AX',
      requestedManufacturerName: 'IGNORED',
    })).toBe('Material: 905750742 · Make: SAFT · Part no.: LS14500-AX');
    expect(buyerIdentityLine({ requestedItemMaterialCode: '902507285', requestedManufacturerName: 'ABB' }))
      .toBe('Material: 902507285 · Make: ABB');
    expect(buyerIdentityLine({ itemDescription: '902507285', customerMaterialCode: '902507285' })).toBeNull();
    expect(buyerIdentityLine({})).toBeNull();
  });

  it('says what the PDF says for a line that is not simply priced', () => {
    expect(pricingStatusText({ pricingStatus: 'NOT_QUOTED', pricingNote: 'Discontinued by manufacturer' }))
      .toBe('Not quoted: Discontinued by manufacturer');
    expect(pricingStatusText({ pricingStatus: 'TO_FOLLOW' })).toBe('Price to follow');
    expect(pricingStatusText({ pricingStatus: 'ESTIMATE' })).toBe('Estimate, subject to confirmation');
    expect(pricingStatusText({ pricingStatus: null })).toBeNull();
  });

  it('never shows the internal draft marker as a note to the customer', () => {
    expect(buyerNotes(DRAFT_REVIEW_PLACEHOLDER)).toBeNull();
    expect(buyerNotes('  ')).toBeNull();
    expect(buyerNotes('Delivery within 4 weeks, DAP Dammam')).toBe('Delivery within 4 weeks, DAP Dammam');
  });
});
