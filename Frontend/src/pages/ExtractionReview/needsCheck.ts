// ---------------------------------------------------------------------------
// "Needs check" — the honest replacement for the extraction-confidence score.
//
// The confidence numbers this product used to render were not measured. On the
// structured path they are literals (1.0 when a cell parsed, 0.2 when it did
// not); on the model path they are the model's own self-report against a rubric
// written into its prompt, with no logprobs read and the per-field values
// discarded before persistence. Rendering them as red/amber/green percentages
// asserted an accuracy the platform has never measured.
//
// What we CAN state without inventing anything is which lines a human still has
// to look at, and why — derived only from facts already persisted: whether the
// fields the downstream flow depends on are present, and whether the evidence
// ledger flagged the value it extracted. A count of those lines is denser
// information than a percentage, and it is actionable.
//
// ---------------------------------------------------------------------------
// ABSENT FROM THE DOCUMENT vs. FAILED TO READ
//
// A signal that fires on every document carries no information. This one used
// to: it demanded a unit of measure on every line, and an inbound RFQ is a
// REQUEST — the buyer states a quantity and asks the supplier for the unit
// price, the currency and the lead time. Every line of every correctly-read
// document was flagged, and the genuinely broken lines were buried among them.
//
// The two cases are separated here by evidence, never by guesswork:
//
//   ABSENT   The document states the field NOWHERE — no line of it carries a
//            value, and the ledger recorded no source text. Nothing was misread,
//            so nothing is flagged.
//   MISREAD  The document states the field on some other line but not this one,
//            or the ledger holds source text for it that did not yield a value.
//            Both still flag, because both are gaps a human must close.
//
// So a price sheet that prices seven of eight lines still flags the eighth, and
// an RFQ that prices none of them flags nothing.
// ---------------------------------------------------------------------------

export type LineCheckState = 'needs-check' | 'verified';

export interface CheckableLine {
  id: number;
  isNew?: boolean;
  productShortDescription?: string | null;
  productShortName?: string | null;
  quantity?: number | null;
  unitOfMeasure?: string | null;
  manufacturerPartNumber?: string | null;
  itemMaterialCode?: string | null;
}

export interface LineCheckResult {
  state: LineCheckState;
  /** Plain-language reasons, ready to show in a tooltip. Empty when verified. */
  reasons: string[];
}

/**
 * One field's outcome in the evidence ledger, as the review screen receives it.
 *
 * `rawValue` is what separates a ledger warning that means "the document said
 * something here we could not interpret" from one that means "there was nothing
 * here". Both arrive as `Warning`; only the first is a reading failure.
 */
export interface FieldCheckSignal {
  status?: string | null;
  rawValue?: string | null;
}

const isBlank = (value: string | null | undefined): boolean => (value ?? '').trim().length === 0;

/**
 * Whether a ledger outcome is a human's problem.
 *
 * `Invalid` always is — the ledger positively rejected a value. `Warning` is
 * where the judgement lives: it is written for an optional field that produced
 * no value, which covers BOTH "the buyer left it for the supplier to fill in"
 * and "there was text here we could not parse". The recorded raw text tells the
 * two apart, so we ask it rather than treating every warning as a defect.
 */
export const isBlockingSignal = (signal: FieldCheckSignal | string | null | undefined): boolean => {
  const resolved: FieldCheckSignal = typeof signal === 'string' ? { status: signal } : (signal ?? {});
  const status = (resolved.status ?? '').toLowerCase();
  if (status === 'invalid') return true;
  if (status !== 'warning') return false;
  return !isBlank(resolved.rawValue);
};

/**
 * Which optional fields THIS document states, read off the document's own lines.
 * A field no line carries is a field the document does not contain, and a value
 * the buyer never stated is not a reading failure.
 */
export interface DocumentAssertions {
  unitOfMeasure: boolean;
  partNumber: boolean;
}

export const documentAssertions = (lines: readonly CheckableLine[]): DocumentAssertions => ({
  unitOfMeasure: lines.some((line) => !isBlank(line.unitOfMeasure)),
  partNumber: lines.some((line) => !isBlank(line.manufacturerPartNumber) || !isBlank(line.itemMaterialCode)),
});

/** A document with no lines asserts nothing; used when no context is supplied. */
const NOTHING_ASSERTED: DocumentAssertions = { unitOfMeasure: false, partNumber: false };

/**
 * Fields the quote-to-cash flow cannot proceed without.
 *
 * Description and quantity are unconditional: a line without them is not a line.
 * Unit of measure and part number are conditional on the document stating them
 * somewhere, because the buyer decides whether to state them at all — the pilot
 * corpus's Word tables carry `Item | Description | Qty | Notes` and no unit
 * column, and inventing a gap there flagged all 641 of its lines.
 */
export const requiredLineFields = (
  line: CheckableLine,
  assertions: DocumentAssertions = NOTHING_ASSERTED,
): string[] => {
  const missing: string[] = [];
  if (isBlank(line.productShortDescription) && isBlank(line.productShortName)) missing.push('Description');
  if (line.quantity == null || !Number.isFinite(line.quantity)) missing.push('Quantity');
  if (assertions.unitOfMeasure && isBlank(line.unitOfMeasure)) missing.push('Unit of measure');
  if (assertions.partNumber && isBlank(line.manufacturerPartNumber) && isBlank(line.itemMaterialCode)) {
    missing.push('Part number');
  }
  return missing;
};

/**
 * Decides whether one line still needs a human look.
 *
 * @param flaggedFields validation outcomes the evidence ledger recorded for this
 *   line, keyed by field name. Absent for documents whose path never wrote a
 *   ledger — the result then rests on completeness alone, which is stated rather
 *   than hidden. A plain status string is accepted for callers that hold nothing
 *   else, and is then treated as having no recorded raw text.
 * @param assertions what the whole document states. Defaults to "nothing", so a
 *   caller that does not supply it can only ever flag FEWER lines, never more.
 */
export const checkLine = (
  line: CheckableLine,
  flaggedFields?: ReadonlyMap<string, FieldCheckSignal | string>,
  assertions: DocumentAssertions = NOTHING_ASSERTED,
): LineCheckResult => {
  const reasons: string[] = [];

  if (line.isNew) {
    reasons.push('Added during this review — not present in the source document');
  }

  const missing = requiredLineFields(line, assertions);
  if (missing.length === 1) reasons.push(`${missing[0]} is blank`);
  else if (missing.length > 1) reasons.push(`${missing.slice(0, -1).join(', ')} and ${missing[missing.length - 1]} are blank`);

  if (flaggedFields) {
    for (const [field, signal] of flaggedFields) {
      if (isBlockingSignal(signal)) {
        reasons.push(`Source check flagged ${field}`);
      }
    }
  }

  return { state: reasons.length > 0 ? 'needs-check' : 'verified', reasons };
};

export interface CheckSummary {
  total: number;
  needsCheck: number;
  /** Row ids that need a check, in grid order — drives the "Next" jump. */
  needsCheckIds: number[];
}

export const summariseChecks = (
  lines: readonly CheckableLine[],
  flaggedByLine?: ReadonlyMap<number, ReadonlyMap<string, FieldCheckSignal | string>>,
): CheckSummary => {
  const assertions = documentAssertions(lines);
  const needsCheckIds: number[] = [];
  for (const line of lines) {
    if (checkLine(line, flaggedByLine?.get(line.id), assertions).state === 'needs-check') {
      needsCheckIds.push(line.id);
    }
  }
  return { total: lines.length, needsCheck: needsCheckIds.length, needsCheckIds };
};

/**
 * The headline sentence. Says the denominator every time, and never implies a
 * measured accuracy.
 */
export const checkHeadline = (summary: CheckSummary): string => {
  if (summary.total === 0) return 'No lines extracted';
  if (summary.needsCheck === 0) {
    return summary.total === 1 ? 'The 1 line looks complete' : `All ${summary.total} lines look complete`;
  }
  const noun = summary.total === 1 ? 'line' : 'lines';
  const verb = summary.needsCheck === 1 ? 'needs' : 'need';
  return `${summary.needsCheck} of ${summary.total} ${noun} ${verb} a check`;
};
