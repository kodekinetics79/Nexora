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
  /**
   * The tenant's own units (`GET /api/Uom`), folded with `foldUnit`. Undefined while they are
   * still loading or could not be loaded — a unit is then never flagged, because "we do not
   * know your units yet" must not read as "this unit is wrong".
   */
  knownUnits?: ReadonlySet<string>;
}

export const documentAssertions = (
  lines: readonly CheckableLine[],
  knownUnits?: ReadonlySet<string>,
): DocumentAssertions => ({
  unitOfMeasure: lines.some((line) => !isBlank(line.unitOfMeasure)),
  partNumber: lines.some((line) => !isBlank(line.manufacturerPartNumber) || !isBlank(line.itemMaterialCode)),
  knownUnits,
});

/** A document with no lines asserts nothing; used when no context is supplied. */
const NOTHING_ASSERTED: DocumentAssertions = { unitOfMeasure: false, partNumber: false };

// ---------------------------------------------------------------------------
// UNIT OF MEASURE — "BANANAS" is not a unit
//
// A line whose unit is a word the business has never transacted in used to
// pass as "Verified: all required fields present", because the only question
// asked was "is the cell non-blank". The question that matters is "is this one
// of your units, or a spelling of one". The server's UomCanonicalizer
// (Backend/ERP_RFQ_Automation/Services/Uom/UomCanonicalizer.cs) is the source
// of truth for spellings; the list below mirrors its vocabulary so the review
// screen agrees with what the write path would accept. Anything not in the
// tenant's units and not in this list is a gap a human must close — the same
// severity as a blank quantity.
// ---------------------------------------------------------------------------

/** Upper-case, alphanumerics only: "Sq. Mtr" → "SQMTR", "m²" → "M2". */
export const foldUnit = (value: string): string =>
  value.normalize('NFKD').toUpperCase().replace(/[^A-Z0-9]/g, '');

/** Spellings the server maps to a canonical unit without asking anyone. */
const UNIT_SPELLINGS: ReadonlySet<string> = new Set([
  'EACH', 'EACHES', 'EA', 'EAS', 'PC', 'PCS', 'PCE', 'PCES', 'PIECE', 'PIECES', 'NO', 'NOS', 'NR', 'NUMBER', 'NUMBERS',
  'ITEM', 'ITEMS', 'UNIT', 'UNITS', 'SET', 'SETS', 'KIT', 'KITS', 'PAIR', 'PAIRS', 'PR', 'PRS', 'DOZEN', 'DOZENS', 'DOZ', 'DZ',
  'ACTIVUNIT', 'ACTIVITYUNIT', 'ACTIVITYUNITS', 'AU', 'LOT', 'LOTS', 'LS', 'LUMPSUM',
  'MM', 'MILLIMETER', 'MILLIMETRE', 'MILLIMETERS', 'MILLIMETRES', 'CM', 'CENTIMETER', 'CENTIMETRE', 'CENTIMETERS', 'CENTIMETRES',
  'M', 'MTR', 'MTRS', 'MTS', 'METER', 'METRE', 'METERS', 'METRES', 'LM', 'RM', 'LINEARMETER', 'LINEARMETRE', 'RUNNINGMETER', 'RUNNINGMETRE',
  'RMT', 'RMTS', 'LMT', 'LMTS', 'LINEARMETERS', 'LINEARMETRES', 'RUNNINGMETERS', 'RUNNINGMETRES',
  'KM', 'KILOMETER', 'KILOMETRE', 'KILOMETERS', 'KILOMETRES', 'IN', 'INCH', 'INCHES', 'FT', 'FOOT', 'FEET', 'YD', 'YARD', 'YARDS',
  'M2', 'SQM', 'SQMTR', 'SQUAREMETER', 'SQUAREMETRE', 'SQMT', 'SQMTRS', 'SQMETER', 'SQMETRE', 'SQMETERS', 'SQMETRES', 'SQUAREMETERS', 'SQUAREMETRES',
  'FT2', 'SQFT', 'SQUAREFOOT', 'SQUAREFEET', 'M3', 'CBM', 'CUM', 'CUBICMETER', 'CUBICMETRE', 'CUMTR', 'CUMTRS', 'CUMETER', 'CUMETRE', 'CUBICMETERS', 'CUBICMETRES',
  'L', 'LTR', 'LTRS', 'LITER', 'LITRE', 'LITERS', 'LITRES', 'ML', 'MILLILITER', 'MILLILITRE',
  'KG', 'KGS', 'KGM', 'KILO', 'KILOS', 'KILOGRAM', 'KILOGRAMS', 'KILOGRAMME', 'G', 'GM', 'GMS', 'GRAM', 'GRAMS',
  'MT', 'TONNE', 'TONNES', 'METRICTON', 'METRICTONS', 'LB', 'LBS', 'POUND', 'POUNDS',
  'HR', 'HRS', 'HOUR', 'HOURS', 'MANHOUR', 'MANHOURS', 'DAY', 'DAYS', 'MANDAY', 'MANDAYS', 'WK', 'WKS', 'WEEK', 'WEEKS',
  'MTH', 'MONTH', 'MONTHS', 'YR', 'YRS', 'YEAR', 'YEARS',
]);

/**
 * Words the server deliberately refuses to map: packaging ("Pack", "Pallet"), a shape ("Length",
 * "Coil") or an ambiguous token ("ST", "Ton"). They are units on the page but not counts of the
 * thing being bought, so a person must say what one of them holds.
 */
const UNIT_REFUSALS: ReadonlySet<string> = new Set([
  'PACK', 'PACKS', 'PK', 'PKS', 'PKT', 'PACKET', 'PACKETS', 'PACKAGE', 'PACKAGES', 'PKG', 'PALLET', 'PALLETS', 'PLT',
  'BOX', 'BOXES', 'CARTON', 'CARTONS', 'CTN', 'CASE', 'CASES', 'CRATE', 'CRATES', 'DRUM', 'DRUMS', 'BAG', 'BAGS', 'SACK', 'SACKS',
  'BUNDLE', 'BUNDLES', 'BDL', 'CONTAINER', 'CONTAINERS', 'BOTTLE', 'BOTTLES', 'CAN', 'CANS', 'TIN', 'TINS',
  'LENGTH', 'LENGTHS', 'PIPE', 'PIPES', 'ROLL', 'ROLLS', 'REEL', 'REELS', 'COIL', 'COILS', 'SPOOL', 'SPOOLS', 'ROD', 'RODS', 'BAR', 'BARS', 'SHEET', 'SHEETS',
  'ST', 'TON', 'TONS', 'T', 'GAL', 'GALLON', 'GALLONS', 'OZ', 'OUNCE', 'OUNCES',
]);

/**
 * Why this line's unit needs a person, or null when it is one of the tenant's units (or a
 * spelling of one). Never flags while the tenant's units are unknown.
 */
export const unitCheckReason = (unit: string | null | undefined, knownUnits?: ReadonlySet<string>): string | null => {
  if (!knownUnits || isBlank(unit)) return null;
  const shown = unit!.trim();
  const key = foldUnit(shown.replace(/^\d+(?:[.,]\d+)?\s*/, ''));
  if (key.length === 0) return `Unit '${shown}' is not a unit — pick one`;
  if (knownUnits.has(key) || UNIT_SPELLINGS.has(key)) return null;
  if (UNIT_REFUSALS.has(key)) return `Unit '${shown}' is packaging or a shape, not a count — confirm what one holds and pick a unit`;
  return `Unit '${shown}' is not one of your units — pick one`;
};

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

  const unitReason = unitCheckReason(line.unitOfMeasure, assertions.knownUnits);
  if (unitReason) reasons.push(unitReason);

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
  knownUnits?: ReadonlySet<string>,
): CheckSummary => {
  const assertions = documentAssertions(lines, knownUnits);
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
