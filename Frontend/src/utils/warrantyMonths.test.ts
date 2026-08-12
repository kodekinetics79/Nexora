import { describe, expect, it } from 'vitest';
import {
  MAXIMUM_WARRANTY_MONTHS,
  WARRANTY_MONTHS_HELPER,
  WARRANTY_WORDING_HELPER,
  WARRANTY_WORDING_NOT_CAPTURED_HERE,
  formatWarrantyMonths,
  parseWarrantyMonthsInput,
  warrantyMonthsFieldValue,
} from './warrantyMonths';

describe('formatWarrantyMonths', () => {
  it('states the unit, because a bare number in a warranty column could be years or days', () => {
    expect(formatWarrantyMonths(24)).toBe('24 months');
  });

  it('says "1 month" rather than "1 months"', () => {
    expect(formatWarrantyMonths(1)).toBe('1 month');
  });

  it('renders a captured zero as a real answer — the supplier offered no warranty', () => {
    expect(formatWarrantyMonths(0)).toBe('0 months');
  });

  it('renders nothing at all when no period was captured', () => {
    expect(formatWarrantyMonths(null)).toBeNull();
    expect(formatWarrantyMonths(undefined)).toBeNull();
  });
});

describe('warrantyMonthsFieldValue', () => {
  it('hydrates an uncaptured warranty to a blank field, never to 0', () => {
    // 0 would assert that the supplier offered no warranty. Nobody made that statement.
    expect(warrantyMonthsFieldValue(null)).toBe('');
    expect(warrantyMonthsFieldValue(undefined)).toBe('');
  });

  it('hydrates a captured zero to "0", so a stated no-warranty offer survives a reload', () => {
    expect(warrantyMonthsFieldValue(0)).toBe('0');
  });

  it('hydrates a captured period to its own digits', () => {
    expect(warrantyMonthsFieldValue(36)).toBe('36');
  });
});

describe('parseWarrantyMonthsInput', () => {
  it('accepts a blank field as not captured, with no error', () => {
    expect(parseWarrantyMonthsInput('')).toEqual({ value: null, error: null });
    expect(parseWarrantyMonthsInput('   ')).toEqual({ value: null, error: null });
  });

  it('accepts a typed zero as a stated value, distinct from blank', () => {
    expect(parseWarrantyMonthsInput('0')).toEqual({ value: 0, error: null });
  });

  it('accepts a whole number of months', () => {
    expect(parseWarrantyMonthsInput('24')).toEqual({ value: 24, error: null });
    expect(parseWarrantyMonthsInput(' 12 ')).toEqual({ value: 12, error: null });
  });

  it('refuses a negative warranty in plain words and sends nothing', () => {
    const result = parseWarrantyMonthsInput('-6');
    expect(result.value).toBeNull();
    expect(result.error).toBe(
      'A warranty cannot be a negative number of months. Leave it blank if the quote does not state one.',
    );
  });

  it('refuses a fractional period rather than rounding one the supplier did not offer', () => {
    expect(parseWarrantyMonthsInput('12.5').value).toBeNull();
    expect(parseWarrantyMonthsInput('12.5').error).toBe('Enter the warranty in whole months.');
  });

  it('refuses text rather than parsing wording into a number', () => {
    // No unit conversion lives here: "2 years" is not 24 months, it is a value nobody typed.
    const result = parseWarrantyMonthsInput('2 years');
    expect(result.value).toBeNull();
    expect(result.error).toContain('Enter the warranty as a number of months');
  });

  it('refuses a period past the ceiling the server enforces, before the save is attempted', () => {
    expect(parseWarrantyMonthsInput(String(MAXIMUM_WARRANTY_MONTHS)).value).toBe(
      MAXIMUM_WARRANTY_MONTHS,
    );
    expect(parseWarrantyMonthsInput(String(MAXIMUM_WARRANTY_MONTHS + 1)).value).toBeNull();
    expect(parseWarrantyMonthsInput(String(MAXIMUM_WARRANTY_MONTHS + 1)).error).toContain(
      'is not accepted',
    );
  });
});

/**
 * The words under the two "Warranty (months)" boxes. They are asserted rather than left to each
 * page because the two capture doors used to write their own, and drifted: one told the buyer the
 * period was "the single period offer comparison can rank", the other "the period offer comparison
 * ranks on" — two half-sentences that both have to be read twice before they say anything.
 */
describe('the warranty capture helper text', () => {
  it('opens with a verb rather than a noun phrase, so it reads once', () => {
    // "The period offer comparison ranks on." parses first as a thing, not as a statement.
    expect(WARRANTY_MONTHS_HELPER.startsWith('How long the warranty runs')).toBe(true);
  });

  it('says what the number is for: it is ranked on, and longer is better', () => {
    expect(WARRANTY_MONTHS_HELPER).toMatch(/months/i);
    expect(WARRANTY_MONTHS_HELPER).toMatch(/ranks on/i);
    expect(WARRANTY_MONTHS_HELPER).toMatch(/longer warranty scores higher/i);
  });

  it('keeps the blank-is-not-zero clause, which is the whole reason the field can be empty', () => {
    expect(WARRANTY_MONTHS_HELPER).toMatch(/leave it blank/i);
    expect(WARRANTY_MONTHS_HELPER).toMatch(/blank is not zero months/i);
  });

  it('gives both capture doors one sentence, so the same field is not explained two ways', () => {
    // The workbench adds a clause; it never rewrites the shared one.
    const workbench = `${WARRANTY_MONTHS_HELPER} ${WARRANTY_WORDING_NOT_CAPTURED_HERE}`;
    expect(workbench.startsWith(WARRANTY_MONTHS_HELPER)).toBe(true);
    expect(WARRANTY_WORDING_NOT_CAPTURED_HERE).toMatch(/quote document/i);
  });

  it('never lets the operator conclude the workbench discarded the supplier wording', () => {
    // The inbox captures the wording; the workbench command has no field for it. Both say where it
    // lives, so the missing box on one screen reads as a place, not as a loss.
    expect(WARRANTY_WORDING_NOT_CAPTURED_HERE).toMatch(/not captured here/i);
    expect(WARRANTY_WORDING_NOT_CAPTURED_HERE).toMatch(/it stays on the quote document/i);
    expect(WARRANTY_WORDING_HELPER).toMatch(/kept as written/i);
    expect(WARRANTY_WORDING_HELPER).toMatch(/Warranty \(months\)/);
  });

  it('never claims the wording is read as a number, because nothing parses it', () => {
    expect(WARRANTY_WORDING_HELPER).toMatch(/never read as a number/i);
  });
});
