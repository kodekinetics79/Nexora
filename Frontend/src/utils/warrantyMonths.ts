/**
 * The warranty a supplier offered, as a number the comparison can rank.
 *
 * There are two warranty facts on a supplier quote line and they do different jobs. The free text
 * is what the supplier wrote — "24 months on parts, 12 on labour, excluding consumables" — and it
 * carries the conditions a buyer has to read. The number is the single period FR-QTM-03's fourth
 * criterion is scored from. Neither is derived from the other in either direction: nothing here
 * parses the wording, and nothing writes the wording from the number. A person types both.
 *
 * The distinction this module exists to protect is blank versus zero. Blank means nobody recorded a
 * period, and the scorer answers "Cannot score — warranty missing". Zero means the supplier offered
 * no warranty at all, and that is a real, scoreable, and usually losing offer. Hydrating a blank
 * field to "0" would quietly turn the first claim into the second.
 */

/**
 * The ceiling the server enforces (CK_supplier_quote_lines_Values). Refusing it here means the
 * buyer is told in the field rather than by a rejected save that names a database constraint.
 */
export const MAXIMUM_WARRANTY_MONTHS = 600;

/** "24 months", "1 month", and null — never "0 months" — when no period was captured. */
export const formatWarrantyMonths = (months?: number | null): string | null =>
  months == null ? null : `${months} ${Math.abs(months) === 1 ? "month" : "months"}`;

/**
 * What a "Warranty (months)" input holds when a line is loaded. An uncaptured warranty hydrates to
 * an empty field, so the buyer sees that nobody has answered rather than a stated zero.
 */
export const warrantyMonthsFieldValue = (months?: number | null): string =>
  months == null ? "" : String(months);

/**
 * What every "Warranty (months)" box says about itself.
 *
 * Held here because two screens capture the same field — the Sourcing workbench response dialog and
 * the Supplier Quote inbox capture dialog — and a buyer who meets one and then the other must be
 * told the same thing about the same number. When each page wrote its own sentence they drifted
 * within a single release, and a field that means one thing on one screen and something slightly
 * different on the next is a field people stop trusting.
 */
export const WARRANTY_MONTHS_HELPER =
  "How long the warranty runs, in months. This is the number the offer comparison ranks on, and a longer warranty scores higher. Leave it blank if the quote states no period — blank is not zero months.";

/**
 * Added on the workbench, whose response command carries the period and nothing else. Said out loud
 * so an operator who has seen the inbox's "Warranty (as quoted)" box does not read its absence here
 * as wording this screen threw away.
 */
export const WARRANTY_WORDING_NOT_CAPTURED_HERE =
  "The supplier's own warranty wording is not captured here; it stays on the quote document.";

/** The free-text box beside it, on the one screen whose command carries both facts. */
export const WARRANTY_WORDING_HELPER =
  "The supplier's own wording, including conditions and exclusions. Kept as written and never read as a number — put the period in Warranty (months).";

/**
 * The parsed field. `value` is what gets sent — null for "not captured" — and `error` is the plain
 * sentence shown under the input. They are never both meaningful: an input with an error sends
 * nothing.
 */
export interface WarrantyMonthsInput {
  value: number | null;
  error: string | null;
}

const NOT_CAPTURED: WarrantyMonthsInput = { value: null, error: null };

/**
 * Reads the typed field. Blank is accepted and means not captured; a negative period is refused
 * outright, because there is no offer a negative warranty could describe.
 */
export const parseWarrantyMonthsInput = (raw: string): WarrantyMonthsInput => {
  const trimmed = raw.trim();
  if (trimmed.length === 0) return NOT_CAPTURED;

  const parsed = Number(trimmed);
  if (!Number.isFinite(parsed))
    return {
      value: null,
      error: "Enter the warranty as a number of months, or leave it blank if the quote does not state one.",
    };
  if (parsed < 0)
    return {
      value: null,
      error: "A warranty cannot be a negative number of months. Leave it blank if the quote does not state one.",
    };
  if (!Number.isInteger(parsed))
    return { value: null, error: "Enter the warranty in whole months." };
  if (parsed > MAXIMUM_WARRANTY_MONTHS)
    return {
      value: null,
      error: `A warranty longer than ${MAXIMUM_WARRANTY_MONTHS} months is not accepted. Record the period the quote actually states.`,
    };
  return { value: parsed, error: null };
};
