import { describe, expect, it } from 'vitest';
import { statusLabel } from './statusLabels';

/**
 * Ten one-line versions of this existed, with three incompatible casing rules, so the same
 * PARTIAL_MATCH read as "PARTIAL MATCH" on the client-PO inbox, "Partial Match" on duplicate
 * uploads and "Partial match" on extraction review — three spellings of one state along one
 * journey. These pin the single rule.
 */
describe('statusLabel', () => {
  it('renders a SCREAMING_SNAKE code as a phrase', () => {
    expect(statusLabel('PARTIAL_MATCH')).toBe('Partial Match');
    expect(statusLabel('REVIEW_REQUIRED')).toBe('Review Required');
    expect(statusLabel('PARTIAL_REQUIRES_SOURCE')).toBe('Partial Requires Source');
  });

  it('splits PascalCase and camelCase the same way', () => {
    expect(statusLabel('KnownInStock')).toBe('Known In Stock');
    expect(statusLabel('documentQuarantined')).toBe('Document Quarantined');
  });

  it('leaves business acronyms alone', () => {
    // "External Po Created" would have been worse than the raw code this replaced.
    expect(statusLabel('EXTERNAL_PO_CREATED')).toBe('External PO Created');
    expect(statusLabel('RFQ_SENT')).toBe('RFQ Sent');
  });

  it('never invents a state for a missing code', () => {
    expect(statusLabel(null)).toBe('Not recorded');
    expect(statusLabel(undefined)).toBe('Not recorded');
    expect(statusLabel('')).toBe('Not recorded');
    expect(statusLabel('   ')).toBe('Not recorded');
    expect(statusLabel(null, 'Unverified')).toBe('Unverified');
  });
});
