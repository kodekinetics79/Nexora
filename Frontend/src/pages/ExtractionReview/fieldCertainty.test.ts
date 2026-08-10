import { describe, it, expect } from 'vitest';
import { describeCertainty } from './fieldCertainty';

describe('describeCertainty', () => {
  it('shows nothing when the path recorded no certainty', () => {
    // The model path writes no evidence ledger, and its per-field confidences
    // were removed from the prompt because they were self-reported. Nothing is
    // invented to fill the gap.
    expect(describeCertainty(null)).toBeNull();
    expect(describeCertainty(undefined)).toBeNull();
    expect(describeCertainty({ confidence: null, rawValue: '12' })).toBeNull();
    expect(describeCertainty({ rawValue: '12' })).toBeNull();
    expect(describeCertainty({ confidence: Number.NaN, rawValue: '12' })).toBeNull();
  });

  it('separates a value read exactly from one that could not be interpreted', () => {
    const exact = describeCertainty({ confidence: 1, valueKind: 'Number', rawValue: '12' });
    const salvaged = describeCertainty({ confidence: 0.2, valueKind: 'Number', rawValue: '9 weeks' });
    expect(exact?.label).toBe('Read exactly');
    expect(exact?.color).toBe('success');
    expect(salvaged?.label).toBe('Could not be interpreted');
    expect(salvaged?.color).toBe('warning');
    // This distinction is the whole point: the two must never render alike.
    expect(exact?.label).not.toBe(salvaged?.label);
  });

  it('calls an unstated field what it is, rather than a low-confidence read', () => {
    const view = describeCertainty({ confidence: 0, valueKind: 'Number', rawValue: null });
    expect(view?.label).toBe('Not stated in the document');
    expect(view?.color).toBe('default');
    expect(view?.detail).toContain('nothing to read');
  });

  it('marks a derived value as not read from the document', () => {
    expect(describeCertainty({ confidence: 1, valueKind: 'Derived', rawValue: '3' })?.label)
      .toBe('Derived, not read');
  });

  it('quotes the recorded number verbatim and never as a percentage', () => {
    const view = describeCertainty({ confidence: 0.2, rawValue: 'nine weeks' });
    expect(view?.recorded).toContain('0.20');
    expect(view?.recorded).not.toMatch(/%/);
    expect(view?.label).not.toMatch(/%/);
    expect(view?.detail).not.toMatch(/%/);
  });

  it('never claims a measured accuracy', () => {
    const inputs = [
      { confidence: 1, rawValue: 'EA' },
      { confidence: 0.2, rawValue: 'nine' },
      { confidence: 0, rawValue: null },
      { confidence: 0, rawValue: 'something' },
    ];
    for (const input of inputs) {
      const view = describeCertainty(input);
      expect(view).not.toBeNull();
      expect(`${view!.label} ${view!.detail} ${view!.recorded}`).not.toMatch(/accura(te|cy)(?!\.|,| )/i);
      expect(view!.recorded).toContain('not a measured accuracy');
    }
  });
});
