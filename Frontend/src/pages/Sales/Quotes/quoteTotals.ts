/**
 * The quote arithmetic, mirroring `QuoteService.CalculateQuoteTotals` on the server.
 *
 * This file exists because the create screen and the server disagreed. The screen took the header
 * discount off a tax-EXCLUSIVE subtotal and the server took it off a tax-INCLUSIVE one, so a rep
 * who entered 10% on a 10,000.00 quote was shown 10,500.00 and 10,350.00 was saved. Both are now
 * the same calculation; keeping it in one module is what stops them drifting apart again.
 *
 * The server is still the authority — it discards every amount the client sends and recomputes.
 * This is a preview, and its job is to predict the server exactly.
 */

/** Currency scale, half away from zero — the same rounding the server's `RoundCurrency` uses. */
export const roundCurrency = (value: number): number => {
  const scaled = value * 100;
  // Number.EPSILON compensates for binary representation error: 1.005 * 100 is 100.49999999999999
  // in IEEE-754, which would round DOWN and put the preview a halala away from the server.
  const rounded = Math.round(Math.abs(scaled) + Number.EPSILON) / 100;
  return value < 0 ? -rounded : rounded;
};

export type DiscountKind = 'PERCENTAGE' | 'FIXED' | null;

export interface PricedLineInput {
  quantity: number;
  unitPrice: number;
  /** Resolved discount kind for this line, or null when the line carries no discount. */
  discountKind: DiscountKind;
  discountValue: number;
  /** STANDARD, ZERO_RATED_EXPORT, EXEMPT or OUT_OF_SCOPE_RCM. Blank is read as STANDARD. */
  taxCategory?: string | null;
}

export interface PricedLine {
  /** round(qty x unitPrice), before any discount. */
  gross: number;
  /** The line's own discount, in money. */
  lineDiscount: number;
  /** gross - lineDiscount. The weight the header discount is shared out by. */
  net: number;
  /** This line's share of the quote-level discount. */
  headerDiscountAllocated: number;
  /** net - headerDiscountAllocated. What output tax is charged on. */
  taxableBase: number;
  /** Null means the tax could not be derived — the tenant has no output tax rate configured. */
  taxAmount: number | null;
}

export interface QuoteTotals {
  lines: PricedLine[];
  /** Sum of gross line values, before any discount. */
  grossSubTotal: number;
  totalLineDiscounts: number;
  /** The quote-level discount, taken on the tax-EXCLUSIVE net. */
  headerDiscount: number;
  /** Sum of taxable bases — the figure the printed line column adds up to. */
  netExcludingTax: number;
  totalTax: number;
  grandTotal: number;
  /** True when at least one standard-rated line could not be taxed, so the quote cannot be sent. */
  hasUnderivedTax: boolean;
}

const STANDARD = 'STANDARD';

const isTaxable = (taxCategory?: string | null): boolean =>
  (taxCategory ?? '').trim().toUpperCase() === STANDARD || !(taxCategory ?? '').trim();

/**
 * Splits `amount` across `weights` proportionally at currency scale, guaranteeing the parts sum
 * exactly back to `amount` (largest remainder). Mirrors `QuoteService.AllocateProRata`.
 */
export const allocateProRata = (amount: number, weights: number[]): number[] => {
  const allocations = weights.map(() => 0);
  if (weights.length === 0 || amount <= 0) return allocations;

  const totalWeight = weights.reduce((sum, w) => sum + (w > 0 ? w : 0), 0);
  if (totalWeight <= 0) return allocations;

  const remainders = weights.map((rawWeight, index) => {
    const weight = rawWeight > 0 ? rawWeight : 0;
    const exact = (amount * weight) / totalWeight;
    // Truncate toward zero at currency scale. `toFixed(6)` first, because Math.floor on raw
    // binary float would turn an exact 0.07 into 0.06 whenever it is represented as 0.0699…9.
    const floored = Math.floor(Number((exact * 100).toFixed(6))) / 100;
    allocations[index] = floored;
    return { index, remainder: exact - floored, weight };
  });

  let leftover = roundCurrency(amount - allocations.reduce((sum, a) => sum + a, 0));
  if (leftover <= 0) return allocations;

  const order = remainders
    .filter((r) => r.weight > 0)
    .sort((a, b) => b.remainder - a.remainder || b.weight - a.weight);

  for (const candidate of order) {
    if (leftover < 0.01) break;
    allocations[candidate.index] = roundCurrency(allocations[candidate.index] + 0.01);
    leftover = roundCurrency(leftover - 0.01);
  }

  return allocations;
};

/**
 * Prices a whole quote. `outputTaxRatePercent` null means the tenant has stated no rate: standard
 * rated lines are then left UNDERIVED rather than taxed at zero, exactly as the server does, so the
 * screen shows the same nothing the send gate will refuse on.
 */
export const calculateQuoteTotals = (
  items: PricedLineInput[],
  headerDiscountKind: DiscountKind,
  headerDiscountValue: number,
  outputTaxRatePercent: number | null,
): QuoteTotals => {
  // Pass 1 — line nets. Tax cannot be derived yet: the taxable base is not known until the header
  // discount has been shared out.
  const nets: number[] = [];
  const partial = items.map((item) => {
    const gross = roundCurrency((item.quantity || 0) * (item.unitPrice || 0));
    let lineDiscount = 0;
    if (item.discountKind === 'PERCENTAGE') lineDiscount = gross * ((item.discountValue || 0) / 100);
    else if (item.discountKind === 'FIXED') lineDiscount = item.discountValue || 0;
    lineDiscount = roundCurrency(lineDiscount);
    if (lineDiscount > gross) lineDiscount = gross;
    const net = gross - lineDiscount;
    nets.push(net);
    return { gross, lineDiscount, net };
  });

  const netSubTotal = nets.reduce((sum, n) => sum + n, 0);

  // The header discount is taken on the tax-EXCLUSIVE net, which is what the rep means by "10%".
  let headerDiscount = 0;
  if (headerDiscountKind === 'PERCENTAGE') headerDiscount = netSubTotal * ((headerDiscountValue || 0) / 100);
  else if (headerDiscountKind === 'FIXED') headerDiscount = headerDiscountValue || 0;
  headerDiscount = roundCurrency(headerDiscount);
  if (headerDiscount > netSubTotal) headerDiscount = netSubTotal;
  if (headerDiscount < 0) headerDiscount = 0;

  const allocations = allocateProRata(headerDiscount, nets);

  // Pass 2 — taxable base and tax, on the net the customer actually pays for each line.
  let hasUnderivedTax = false;
  const lines: PricedLine[] = partial.map((line, index) => {
    const allocated = allocations[index];
    const taxableBase = Math.max(0, roundCurrency(line.net - allocated));
    const taxable = isTaxable(items[index].taxCategory);
    let taxAmount: number | null;
    if (!taxable) {
      taxAmount = 0;
    } else if (outputTaxRatePercent === null || outputTaxRatePercent === undefined) {
      taxAmount = null;
      hasUnderivedTax = true;
    } else {
      taxAmount = roundCurrency((taxableBase * outputTaxRatePercent) / 100);
    }
    return {
      gross: line.gross,
      lineDiscount: line.lineDiscount,
      net: line.net,
      headerDiscountAllocated: allocated,
      taxableBase,
      taxAmount,
    };
  });

  const grossSubTotal = roundCurrency(lines.reduce((sum, l) => sum + l.gross, 0));
  const totalLineDiscounts = roundCurrency(lines.reduce((sum, l) => sum + l.lineDiscount, 0));
  const netExcludingTax = roundCurrency(lines.reduce((sum, l) => sum + l.taxableBase, 0));
  const totalTax = roundCurrency(lines.reduce((sum, l) => sum + (l.taxAmount ?? 0), 0));

  return {
    lines,
    grossSubTotal,
    totalLineDiscounts,
    headerDiscount,
    netExcludingTax,
    totalTax,
    grandTotal: roundCurrency(netExcludingTax + totalTax),
    hasUnderivedTax,
  };
};

/**
 * One line of a quote as the SERVER stored it, which is a different thing from a line being edited.
 * Every figure here has already been through `QuoteService.CalculateQuoteTotals`.
 */
export interface StoredLine {
  quantity: number;
  unitPrice: number;
  /** The line's own discount, in money. */
  discount?: number | null;
  /** Server-derived output tax. Null means it was never derived — not that it is zero. */
  taxAmount?: number | null;
  /** `totalAmount - taxAmount`: what the tax was charged on. */
  taxableBase?: number | null;
  /** This line's stored share of the header discount. Null on rows predating the column. */
  headerDiscountAllocated?: number | null;
  taxRatePercentApplied?: number | null;
}

export interface StoredQuoteSummary {
  grossSubTotal: number;
  totalLineDiscounts: number;
  headerDiscount: number;
  netExcludingTax: number;
  totalTax: number;
  /** The single rate every taxed line shares, or null when the quote mixes tax treatments. */
  singleTaxRatePercent: number | null;
  /** A standard-rated line the server could not tax: the tenant states no output tax rate. */
  hasUnderivedTax: boolean;
}

/**
 * The breakdown of a quote that has ALREADY been priced and saved — read off the stored line
 * values, never recomputed from the header discount.
 *
 * `calculateQuoteTotals` above predicts what the server WILL store while a rep is typing.
 * This answers the different question a view screen asks: what did the server actually store?
 * Re-running the prediction there would need the tenant's tax rate and would silently disagree
 * with the row the moment a quote was priced under an older rate.
 *
 * The header discount is READ from the per-line allocation for the reason
 * `QuoteItem.HeaderDiscountAllocated` documents at length: reconstructing it as
 * `net - grandTotal` subtracts a tax-INCLUSIVE figure from a tax-EXCLUSIVE one and lands a whole
 * VAT out. On 1,000.00 at 15% with no header discount that reconstruction yields -150.00; with a
 * 200.00 header discount it yields 80.00. Both were on screen.
 *
 * `storedGrandTotal` is used ONLY for lines that predate the allocation column, where the broken
 * inference is the only answer that exists — scoped exactly as `QuoteService`'s PDF builder scopes
 * it, and only when the quote actually carries a header discount.
 */
export const summariseStoredQuote = (
  lines: StoredLine[],
  hasHeaderDiscount: boolean,
  storedGrandTotal: number,
): StoredQuoteSummary => {
  const grossSubTotal = roundCurrency(lines.reduce((sum, l) => sum + (l.quantity || 0) * (l.unitPrice || 0), 0));
  const totalLineDiscounts = roundCurrency(lines.reduce((sum, l) => sum + (l.discount ?? 0), 0));
  const totalTax = roundCurrency(lines.reduce((sum, l) => sum + (l.taxAmount ?? 0), 0));
  const netExcludingTax = roundCurrency(lines.reduce((sum, l) => sum + (l.taxableBase ?? 0), 0));

  const allocated = roundCurrency(lines.reduce((sum, l) => sum + (l.headerDiscountAllocated ?? 0), 0));
  const isLegacyUnallocated =
    allocated === 0 && lines.length > 0 && lines.every((l) => l.headerDiscountAllocated == null);
  const headerDiscount = isLegacyUnallocated && hasHeaderDiscount
    ? Math.max(0, roundCurrency(grossSubTotal - totalLineDiscounts + totalTax - storedGrandTotal))
    : allocated;

  const rates = Array.from(new Set(
    lines.filter((l) => (l.taxRatePercentApplied ?? 0) > 0).map((l) => l.taxRatePercentApplied as number)));

  return {
    grossSubTotal,
    totalLineDiscounts,
    headerDiscount,
    netExcludingTax,
    totalTax,
    singleTaxRatePercent: rates.length === 1 ? rates[0] : null,
    hasUnderivedTax: lines.some((l) => l.taxAmount == null),
  };
};
