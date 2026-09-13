import type { LeadDecisionLineDTO } from '../../../api/services/leadDecisionService';

/**
 * What the decision screen can honestly say about one line's unit of measure.
 *
 * The server keeps the customer's own word and maps it (nos → EA) or refuses it (Pack, Roll)
 * with UomCanonicalizer; it never invents a unit the customer did not write. These helpers only
 * READ that result so the screen can show it: a mapped spelling is pre-selected and labelled
 * with the word the customer used, a refused or unknown word is shown beside an empty picker,
 * and a missing unit says so. Nothing here chooses a unit for anyone.
 */

export interface UnitOption { code: string; label: string }

const fold = (value: string): string => value.replace(/[^\p{L}\p{N}]/gu, '').toUpperCase();

/** "10 Nos" is a quantity that leaked into the unit text; the word is "Nos". Mirrors the server. */
const withoutLeadingQuantity = (value: string): string => value.replace(/^[\d\s.,]+/, '').trim() || value.trim();

/** The tenant's own spelling of a unit code, matched without regard to case; undefined when it has none. */
export const tenantUnitCode = (value: string | null | undefined, options: UnitOption[]): string | undefined => {
  const wanted = value?.trim().toUpperCase();
  if (!wanted) return undefined;
  return options.find((option) => option.code.trim().toUpperCase() === wanted)?.code;
};

/** The unit word exactly as the customer wrote it, when Nexora kept it; null when the request stated none. */
export const unitAsWritten = (line: Pick<LeadDecisionLineDTO, 'sourceFields' | 'unitOfMeasure'>): string | null => {
  const cited = line.sourceFields?.find((field) =>
    ['UNITOFMEASURE', 'UOM'].includes(fold(field.field ?? '')) && Boolean(field.rawValue?.trim()));
  if (cited) return withoutLeadingQuantity(cited.rawValue);
  return line.unitOfMeasure?.trim() || null;
};

export type UnitReading =
  /** The request states no unit. */
  | { kind: 'absent' }
  /** The request's unit is one of the tenant's units. `asWritten` is set only when the customer spelled it differently. */
  | { kind: 'mapped'; code: string; asWritten: string | null }
  /** The request states a unit the tenant cannot quote in as written (Pack, Roll, or a code the tenant lacks). */
  | { kind: 'unrecognised'; asWritten: string };

export const readUnit = (
  line: Pick<LeadDecisionLineDTO, 'sourceFields' | 'unitOfMeasure' | 'normalizedUom'>,
  unitCodes: ReadonlySet<string>,
): UnitReading => {
  const stored = line.normalizedUom?.trim() || line.unitOfMeasure?.trim() || '';
  const written = unitAsWritten(line);
  if (!stored && !written) return { kind: 'absent' };
  if (stored && unitCodes.has(stored.toUpperCase())) {
    const code = stored.toUpperCase();
    return { kind: 'mapped', code, asWritten: written && fold(written) !== fold(code) ? written : null };
  }
  return { kind: 'unrecognised', asWritten: written ?? stored };
};

/**
 * The short line under a unit picker. Null when there is nothing worth saying: the customer
 * wrote the code itself and it is one of the tenant's units.
 */
export const unitCaption = (reading: UnitReading, chosen?: string | null, instruct = true): string | null => {
  switch (reading.kind) {
    case 'absent': return 'not stated in the request';
    // The instruction only where there is a picker to act on; a read-only or unquoted line just says the word.
    case 'unrecognised': return instruct ? `as written: ${reading.asWritten} — choose the unit you quote in` : `as written: ${reading.asWritten}`;
    case 'mapped':
      if (!reading.asWritten) return null;
      return chosen?.trim().toUpperCase() === reading.code
        ? `as written: ${reading.asWritten} (read as ${reading.code})`
        : `as written: ${reading.asWritten}`;
    default: return null;
  }
};
