import { describe, it, expect } from 'vitest';
import {
  parseTransformations,
  fieldEvidenceKey,
  type LeadFieldEvidenceEntry,
} from './extractionReviewService';

const entry = (overrides: Partial<LeadFieldEvidenceEntry> = {}): LeadFieldEvidenceEntry => ({
  fieldName: 'quantity',
  ...overrides,
});

describe('parseTransformations', () => {
  it('passes an array through, dropping blanks', () => {
    const result = parseTransformations(entry({ transformations: ['trim', '', 'toNumber'] }));
    expect(result).toEqual(['trim', 'toNumber']);
  });

  it('parses a JSON array stored as a string', () => {
    const result = parseTransformations(entry({ transformationsJson: '["trim","parseDecimal"]' }));
    expect(result).toEqual(['trim', 'parseDecimal']);
  });

  it('renders a JSON object as key: value steps', () => {
    const result = parseTransformations(entry({ transformationsJson: '{"trim":true,"scale":1000}' }));
    expect(result).toEqual(['trim: true', 'scale: 1000']);
  });

  it('shows a non-JSON string verbatim rather than dropping the evidence', () => {
    expect(parseTransformations(entry({ transformationsJson: 'trimmed then rounded' }))).toEqual(['trimmed then rounded']);
  });

  it('returns nothing when the backend recorded nothing', () => {
    expect(parseTransformations(entry())).toEqual([]);
    expect(parseTransformations(entry({ transformationsJson: '' }))).toEqual([]);
    expect(parseTransformations(entry({ transformationsJson: null }))).toEqual([]);
  });

  it('prefers the array form when both shapes arrive', () => {
    const result = parseTransformations(entry({ transformations: ['fromArray'], transformationsJson: '["fromJson"]' }));
    expect(result).toEqual(['fromArray']);
  });

  it('stringifies non-string array members instead of losing them', () => {
    expect(parseTransformations(entry({ transformationsJson: '[{"op":"trim"}]' }))).toEqual(['{"op":"trim"}']);
  });

  it('survives a JSON scalar', () => {
    expect(parseTransformations(entry({ transformationsJson: '42' }))).toEqual([]);
  });
});

describe('fieldEvidenceKey', () => {
  it('is case-insensitive on the field name', () => {
    expect(fieldEvidenceKey(7, 'Quantity')).toBe(fieldEvidenceKey(7, 'quantity'));
  });

  it('keeps different lines apart', () => {
    expect(fieldEvidenceKey(7, 'quantity')).not.toBe(fieldEvidenceKey(8, 'quantity'));
  });

  it('maps header-level fields (null line) to a stable key', () => {
    expect(fieldEvidenceKey(null, 'rfqno')).toBe('0::rfqno');
    expect(fieldEvidenceKey(undefined, 'rfqno')).toBe('0::rfqno');
  });
});
