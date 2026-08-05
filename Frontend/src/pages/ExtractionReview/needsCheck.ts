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

/** Validation outcomes from the evidence ledger that a human must resolve. */
const BLOCKING_VALIDATION_STATUSES = new Set(['warning', 'invalid']);

const isBlank = (value: string | null | undefined): boolean => (value ?? '').trim().length === 0;

/**
 * Fields the quote-to-cash flow cannot proceed without. Deliberately short:
 * every entry here is a field the pilot's own data shows is normally present,
 * so a gap is a real signal and not noise.
 */
export const requiredLineFields = (line: CheckableLine): string[] => {
  const missing: string[] = [];
  if (isBlank(line.productShortDescription) && isBlank(line.productShortName)) missing.push('Description');
  if (line.quantity == null || !Number.isFinite(line.quantity)) missing.push('Quantity');
  if (isBlank(line.unitOfMeasure)) missing.push('Unit of measure');
  if (isBlank(line.manufacturerPartNumber) && isBlank(line.itemMaterialCode)) missing.push('Part number');
  return missing;
};

/**
 * Decides whether one line still needs a human look.
 *
 * @param flaggedFields validation statuses the evidence ledger recorded for
 *   this line, keyed by field name. Absent for documents whose path never wrote
 *   a ledger — the result then rests on completeness alone, which is stated
 *   rather than hidden.
 */
export const checkLine = (
  line: CheckableLine,
  flaggedFields?: ReadonlyMap<string, string>,
): LineCheckResult => {
  const reasons: string[] = [];

  if (line.isNew) {
    reasons.push('Added during this review — not present in the source document');
  }

  const missing = requiredLineFields(line);
  if (missing.length === 1) reasons.push(`${missing[0]} is blank`);
  else if (missing.length > 1) reasons.push(`${missing.slice(0, -1).join(', ')} and ${missing[missing.length - 1]} are blank`);

  if (flaggedFields) {
    for (const [field, status] of flaggedFields) {
      if (BLOCKING_VALIDATION_STATUSES.has((status ?? '').toLowerCase())) {
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
  flaggedByLine?: ReadonlyMap<number, ReadonlyMap<string, string>>,
): CheckSummary => {
  const needsCheckIds: number[] = [];
  for (const line of lines) {
    if (checkLine(line, flaggedByLine?.get(line.id)).state === 'needs-check') {
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
