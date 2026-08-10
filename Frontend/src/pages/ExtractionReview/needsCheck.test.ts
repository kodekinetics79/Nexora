import { describe, it, expect } from 'vitest';
import {
  checkLine,
  summariseChecks,
  checkHeadline,
  requiredLineFields,
  documentAssertions,
  isBlockingSignal,
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

// The shape of the 120-document pilot corpus: a Word table of
// `Item | Description | Qty | Notes`. There is no unit column and no price
// column anywhere in the document, so no line carries a unit of measure.
const corpusLine = (id: number): CheckableLine => ({
  id,
  productShortDescription: 'Ball valve, 2in, class 150',
  quantity: 4,
  manufacturerPartNumber: 'SKU-2244',
});

/** Everything a complete line asserts, for the single-line cases below. */
const stated = documentAssertions([completeLine()]);

describe('documentAssertions', () => {
  it('reports what the document states, not what the schema allows', () => {
    expect(documentAssertions([corpusLine(1), corpusLine(2)])).toEqual({
      unitOfMeasure: false,
      partNumber: true,
    });
  });

  it('counts a field stated on any one line as stated by the document', () => {
    const lines = [corpusLine(1), { ...corpusLine(2), unitOfMeasure: 'M' }];
    expect(documentAssertions(lines).unitOfMeasure).toBe(true);
  });

  it('treats whitespace as unstated', () => {
    expect(documentAssertions([{ ...corpusLine(1), unitOfMeasure: '   ' }]).unitOfMeasure).toBe(false);
  });

  it('accepts an item material code in place of a manufacturer part number', () => {
    const line: CheckableLine = { id: 1, itemMaterialCode: 'MAT-0091' };
    expect(documentAssertions([line]).partNumber).toBe(true);
  });
});

describe('requiredLineFields', () => {
  it('reports nothing missing on a complete line', () => {
    expect(requiredLineFields(completeLine(), stated)).toEqual([]);
  });

  it('accepts productShortName in place of a description', () => {
    const line = completeLine({ productShortDescription: '', productShortName: 'Junction box' });
    expect(requiredLineFields(line, stated)).toEqual([]);
  });

  it('accepts itemMaterialCode in place of a manufacturer part number', () => {
    const line = completeLine({ manufacturerPartNumber: '', itemMaterialCode: 'MAT-0091' });
    expect(requiredLineFields(line, stated)).toEqual([]);
  });

  it('treats whitespace-only values as blank when the document states the field', () => {
    expect(requiredLineFields(completeLine({ unitOfMeasure: '   ' }), stated)).toEqual(['Unit of measure']);
  });

  it('treats a zero quantity as present — zero is a real customer instruction', () => {
    expect(requiredLineFields(completeLine({ quantity: 0 }), stated)).toEqual([]);
  });

  it('treats a null quantity as missing', () => {
    expect(requiredLineFields(completeLine({ quantity: null }), stated)).toEqual(['Quantity']);
  });

  it('lists every missing field the document does state', () => {
    const bare: CheckableLine = { id: 2 };
    expect(requiredLineFields(bare, stated)).toEqual(['Description', 'Quantity', 'Unit of measure', 'Part number']);
  });

  // ---- absent from the document vs. failed to read -----------------------

  it('does not demand a unit of measure the buyer never stated', () => {
    // Reverting the fix makes this list ['Unit of measure'] — the flag that
    // fired on all 641 lines of the 120-document corpus.
    const corpus = [corpusLine(1), corpusLine(2), corpusLine(3)];
    for (const line of corpus) {
      expect(requiredLineFields(line, documentAssertions(corpus))).toEqual([]);
    }
  });

  it('still demands a unit of measure the document states on another line', () => {
    const mixed = [{ ...corpusLine(1), unitOfMeasure: 'M' }, corpusLine(2)];
    const assertions = documentAssertions(mixed);
    expect(requiredLineFields(mixed[0], assertions)).toEqual([]);
    expect(requiredLineFields(mixed[1], assertions)).toEqual(['Unit of measure']);
  });

  it('does not demand a part number in a document that carries none', () => {
    const noParts = [{ id: 1, productShortDescription: 'Cable tray', quantity: 3 }];
    expect(requiredLineFields(noParts[0], documentAssertions(noParts))).toEqual([]);
  });

  it('assumes the document states nothing when no context is supplied', () => {
    // A caller with no document context can only ever flag FEWER lines.
    expect(requiredLineFields(corpusLine(1))).toEqual([]);
  });
});

describe('isBlockingSignal', () => {
  it('blocks on an invalid value however it was recorded', () => {
    expect(isBlockingSignal({ status: 'Invalid' })).toBe(true);
    expect(isBlockingSignal({ status: 'invalid', rawValue: null })).toBe(true);
  });

  it('blocks on a warning that carries source text — the document said something we could not read', () => {
    expect(isBlockingSignal({ status: 'Warning', rawValue: '9 weeks' })).toBe(true);
  });

  it('does not block on a warning with no source text — nothing was there to read', () => {
    // Reverting the fix makes this true, and every solicited price, currency
    // and lead time on an inbound RFQ becomes a defect the reviewer must clear.
    expect(isBlockingSignal({ status: 'Warning', rawValue: '' })).toBe(false);
    expect(isBlockingSignal({ status: 'warning', rawValue: null })).toBe(false);
    expect(isBlockingSignal({ status: 'warning' })).toBe(false);
  });

  it('does not block on Valid, Unvalidated or nothing at all', () => {
    expect(isBlockingSignal({ status: 'Valid', rawValue: 'EA' })).toBe(false);
    expect(isBlockingSignal({ status: 'Unvalidated', rawValue: 'EA' })).toBe(false);
    expect(isBlockingSignal(undefined)).toBe(false);
  });
});

describe('checkLine', () => {
  it('verifies a complete line with no ledger flags', () => {
    expect(checkLine(completeLine(), undefined, stated)).toEqual({ state: 'verified', reasons: [] });
  });

  it('flags a line the reviewer added, because nothing was extracted for it', () => {
    const result = checkLine(completeLine({ isNew: true }), undefined, stated);
    expect(result.state).toBe('needs-check');
    expect(result.reasons[0]).toContain('Added during this review');
  });

  it('names a single blank field in the singular', () => {
    const result = checkLine(completeLine({ unitOfMeasure: '' }), undefined, stated);
    expect(result.state).toBe('needs-check');
    expect(result.reasons).toContain('Unit of measure is blank');
  });

  it('joins several blank fields into one readable clause', () => {
    const result = checkLine(completeLine({ unitOfMeasure: '', quantity: null }), undefined, stated);
    expect(result.reasons).toContain('Quantity and Unit of measure are blank');
  });

  it('flags a line whose evidence ledger rejected a value it could read', () => {
    const flags = new Map([['quantity', { status: 'Warning', rawValue: 'two dozen' }]]);
    const result = checkLine(completeLine(), flags, stated);
    expect(result.state).toBe('needs-check');
    expect(result.reasons).toContain('Source check flagged quantity');
  });

  it('flags a line whose evidence ledger recorded an invalid value', () => {
    const flags = new Map([['unitPrice', { status: 'invalid' }]]);
    expect(checkLine(completeLine(), flags, stated).state).toBe('needs-check');
  });

  it('does not flag a ledger warning for a field the buyer left for the supplier', () => {
    const flags = new Map([
      ['unitPrice', { status: 'Warning', rawValue: null }],
      ['currency', { status: 'Warning', rawValue: '' }],
      ['leadTimeDays', { status: 'Warning' }],
    ]);
    expect(checkLine(completeLine(), flags, stated)).toEqual({ state: 'verified', reasons: [] });
  });

  it('does not flag Valid or Unvalidated ledger statuses', () => {
    const flags = new Map([
      ['quantity', { status: 'Valid', rawValue: '12' }],
      ['currency', { status: 'Unvalidated', rawValue: 'SAR' }],
    ]);
    expect(checkLine(completeLine(), flags, stated)).toEqual({ state: 'verified', reasons: [] });
  });

  it('accepts a bare status string from a caller that holds nothing else', () => {
    const flags = new Map([['quantity', 'Invalid']]);
    expect(checkLine(completeLine(), flags, stated).state).toBe('needs-check');
  });

  it('rests on completeness alone when the document has no ledger', () => {
    // PDF/OCR extractions never persisted word boxes, so no flags arrive. The
    // verdict must still be produced rather than defaulting to "needs check".
    expect(checkLine(completeLine(), undefined, stated).state).toBe('verified');
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

  it('clears a correctly-read corpus document instead of flagging every line', () => {
    // The measured symptom: an 8-line document from the pilot corpus rendered
    // "8 of 8 lines need a check" with nothing misread.
    const document = [1, 2, 3, 4, 5, 6, 7, 8].map(corpusLine);
    const summary = summariseChecks(document);
    expect(summary.needsCheck).toBe(0);
    expect(checkHeadline(summary)).toBe('All 8 lines look complete');
  });

  it('applies per-line ledger flags to the right line', () => {
    const lines: CheckableLine[] = [completeLine({ id: 20 }), completeLine({ id: 21 })];
    const flagged = new Map([[21, new Map([['quantity', { status: 'Warning', rawValue: 'twelve' }]])]]);
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
