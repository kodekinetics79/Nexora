/**
 * The words a quote line shows the rep, matching what the customer's PDF prints for the same line
 * (Backend `QuoteDocumentText`). The quote screen used to show a VAT-inclusive line total, "Pricing
 * Pending" for a line the PDF called "Not quoted", and none of the buyer's material, maker or part
 * number — so the rep read one thing and the buyer another (pilot audit UX-04, CB-13, UX-03).
 */

export interface QuoteLineLike {
  itemDescription?: string | null;
  totalAmount?: number | null;
  taxAmount?: number | null;
  taxableBase?: number | null;
  pricingStatus?: string | null;
  pricingNote?: string | null;
  customerMaterialCode?: string | null;
  manufacturerName?: string | null;
  manufacturerPartNumber?: string | null;
  requestedItemMaterialCode?: string | null;
  requestedManufacturerName?: string | null;
  requestedManufacturerPartNumber?: string | null;
}

/** The internal marker a draft is born with. It is Nexora's review state, not a note to the buyer. */
export const DRAFT_REVIEW_PLACEHOLDER =
  'Commercial Review Required: pricing, inventory, lead time, tax, freight and validity remain pending.';

const clean = (value?: string | null): string | null => {
  const trimmed = value?.trim();
  return trimmed ? trimmed : null;
};

/** The rep's notes as the customer reads them, or null for empty text or the internal draft marker. */
export const buyerNotes = (remarks?: string | null): string | null => {
  const text = clean(remarks);
  if (!text) return null;
  return text.replace(/\s+/g, ' ') === DRAFT_REVIEW_PLACEHOLDER ? null : text;
};

/**
 * "Material: 905750742 · Make: SAFT · Part no.: LS14500-AX". What the quote line stores first; a
 * line written before those values were stored falls back to the linked RFQ line. Null when none.
 */
export const buyerIdentityLine = (line: QuoteLineLike): string | null => {
  const material = clean(line.customerMaterialCode) ?? clean(line.requestedItemMaterialCode);
  const maker = clean(line.manufacturerName) ?? clean(line.requestedManufacturerName);
  const part = clean(line.manufacturerPartNumber) ?? clean(line.requestedManufacturerPartNumber);
  const parts: string[] = [];
  if (material && material.toLowerCase() !== clean(line.itemDescription)?.toLowerCase()) parts.push(`Material: ${material}`);
  if (maker) parts.push(`Make: ${maker}`);
  if (part) parts.push(`Part no.: ${part}`);
  return parts.length ? parts.join(' · ') : null;
};

/** A line sent without a price by choice: "To follow" or "Not quoted". */
export const isUnpricedByChoice = (line: QuoteLineLike): boolean =>
  line.pricingStatus === 'TO_FOLLOW' || line.pricingStatus === 'NOT_QUOTED';

/** The sentence the PDF prints under a line that is not simply priced, or null. */
export const pricingStatusText = (line: QuoteLineLike): string | null => {
  const note = clean(line.pricingNote);
  const suffix = note ? `: ${note}` : '';
  switch (line.pricingStatus) {
    case 'NOT_QUOTED': return `Not quoted${suffix}`;
    case 'TO_FOLLOW': return `Price to follow${suffix}`;
    case 'ESTIMATE': return `Estimate, subject to confirmation${suffix}`;
    default: return null;
  }
};

/**
 * The line total the customer's PDF prints: tax EXCLUDED. The stored `totalAmount` carries the
 * line's VAT (2,876.15 where the PDF says 2,501.00), and VAT is stated once, in the totals.
 */
export const lineTotalExVat = (line: QuoteLineLike): number => {
  if (typeof line.taxableBase === 'number' && Number.isFinite(line.taxableBase)) return line.taxableBase;
  return (line.totalAmount ?? 0) - (line.taxAmount ?? 0);
};
