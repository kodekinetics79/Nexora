import { describe, it, expect } from 'vitest';
import {
  checkLine,
  summariseChecks,
  checkHeadline,
  requiredLineFields,
  type CheckableLine,
} from './needsCheck';

// A line the pilot's own data says is normal: description, quantity, unit of
// measure and a part number all present.
const completeLine = (overrides: Partial<CheckableLine> = {}): CheckableLine => ({
  id: 1,
  productShortDescription: 'Explosion-proof junction box',
  quantity: 12,
  unitOfMeasure: 'EA',
  manufacturerPartNumber: 'GUAL-16',
  ...overrides,
});

describe('requiredLineFields', () => {
  it('reports nothing missing on a complete line', () => {
    expect(requiredLineFields(completeLine())).toEqual([]);
  });

  it('accepts productShortName in place of a description', () => {
    const line = completeLine({ productShortDescription: '', productShortName: 'Junction box' });
    expect(requiredLineFields(line)).toEqual([]);
  });

  it('accepts itemMaterialCode in place of a manufacturer part number', () => {
    const line = completeLine({ manufacturerPartNumber: '', itemMaterialCode: 'MAT-0091' });
    expect(requiredLineFields(line)).toEqual([]);
  });

  it('treats whitespace-only values as blank', () => {
    expect(requiredLineFields(completeLine({ unitOfMeasure: '   ' }))).toEqual(['Unit of measure']);
  });

  it('treats a zero quantity as present — zero is a real customer instruction', () => {
    expect(requiredLineFields(completeLine({ quantity: 0 }))).toEqual([]);
  });

  it('treats a null quantity as missing', () => {
    expect(requiredLineFields(completeLine({ quantity: null }))).toEqual(['Quantity']);
  });

  it('lists every missing field', () => {
    const bare: CheckableLine = { id: 2 };
    expect(requiredLineFields(bare)).toEqual(['Description', 'Quantity', 'Unit of measure', 'Part number']);
  });
});

describe('checkLine', () => {
  it('verifies a complete line with no ledger flags', () => {
    expect(checkLine(completeLine())).toEqual({ state: 'verified', reasons: [] });
  });

  it('flags a line the reviewer added, because nothing was extracted for it', () => {
    const result = checkLine(completeLine({ isNew: true }));
    expect(result.state).toBe('needs-check');
    expect(result.reasons[0]).toContain('Added during this review');
  });

  it('names a single blank field in the singular', () => {
    const result = checkLine(completeLine({ unitOfMeasure: '' }));
    expect(result.state).toBe('needs-check');
    expect(result.reasons).toContain('Unit of measure is blank');
  });

  it('joins several blank fields into one readable clause', () => {
    const result = checkLine(completeLine({ unitOfMeasure: '', quantity: null }));
    expect(result.reasons).toContain('Quantity and Unit of measure are blank');
  });

  it('flags a line whose evidence ledger recorded a warning', () => {
    const flags = new Map([['quantity', 'Warning']]);
    const result = checkLine(completeLine(), flags);
    expect(result.state).toBe('needs-check');
    expect(result.reasons).toContain('Source check flagged quantity');
  });

  it('flags a line whose evidence ledger recorded an invalid value', () => {
    const flags = new Map([['unitPrice', 'invalid']]);
    expect(checkLine(completeLine(), flags).state).toBe('needs-check');
  });

  it('does not flag Valid or Unvalidated ledger statuses', () => {
    const flags = new Map([['quantity', 'Valid'], ['currency', 'Unvalidated']]);
    expect(checkLine(completeLine(), flags)).toEqual({ state: 'verified', reasons: [] });
  });

  it('rests on completeness alone when the document has no ledger', () => {
    // PDF/OCR extractions never persisted word boxes, so no flags arrive. The
    // verdict must still be produced rather than defaulting to "needs check".
    expect(checkLine(completeLine(), undefined).state).toBe('verified');
  });
});

describe('summariseChecks', () => {
  it('counts lines needing a check and keeps them in grid order', () => {
    const lines: CheckableLine[] = [
      completeLine({ id: 10 }),
      completeLine({ id: 11, unitOfMeasure: '' }),
      completeLine({ id: 12 }),
      completeLine({ id: 13, quantity: null }),
    ];
    const summary = summariseChecks(lines);
    expect(summary.total).toBe(4);
    expect(summary.needsCheck).toBe(2);
    expect(summary.needsCheckIds).toEqual([11, 13]);
  });

  it('applies per-line ledger flags to the right line', () => {
    const lines: CheckableLine[] = [completeLine({ id: 20 }), completeLine({ id: 21 })];
    const flagged = new Map([[21, new Map([['quantity', 'Warning']])]]);
    expect(summariseChecks(lines, flagged).needsCheckIds).toEqual([21]);
  });

  it('handles an empty document', () => {
    expect(summariseChecks([])).toEqual({ total: 0, needsCheck: 0, needsCheckIds: [] });
  });
});

describe('checkHeadline', () => {
  it('always states the denominator', () => {
    expect(checkHeadline({ total: 40, needsCheck: 12, needsCheckIds: [] })).toBe('12 of 40 lines need a check');
  });

  it('never claims accuracy when everything is complete', () => {
    const headline = checkHeadline({ total: 40, needsCheck: 0, needsCheckIds: [] });
    expect(headline).toBe('All 40 lines look complete');
    expect(headline).not.toMatch(/%/);
  });

  it('says nothing was extracted rather than showing a zero score', () => {
    expect(checkHeadline({ total: 0, needsCheck: 0, needsCheckIds: [] })).toBe('No lines extracted');
  });

  it('agrees in number for a single line', () => {
    expect(checkHeadline({ total: 1, needsCheck: 1, needsCheckIds: [1] })).toBe('1 of 1 line needs a check');
    expect(checkHeadline({ total: 1, needsCheck: 0, needsCheckIds: [] })).toBe('The 1 line looks complete');
  });

  it('never renders a percentage in any state', () => {
    const states = [
      { total: 0, needsCheck: 0, needsCheckIds: [] },
      { total: 1, needsCheck: 1, needsCheckIds: [1] },
      { total: 2966, needsCheck: 37, needsCheckIds: [] },
    ];
    for (const state of states) expect(checkHeadline(state)).not.toMatch(/%/);
  });
});
