import { describe, expect, it } from 'vitest';
import { LEAD_STATUS_WORDS, leadStatusWords } from './leadStatusWords';

describe('leadStatusWords', () => {
  it.each([
    ['RECEIVED', 'New'],
    ['PENDING_IDENTIFICATION', 'Customer not matched yet'],
    ['UNASSIGNED', 'Waiting for an owner'],
    ['ASSIGNED', 'Owner assigned'],
    ['UNDER_REVIEW', 'Under review'],
    ['QUALIFIED', 'Qualified'],
    ['DISQUALIFIED', 'Declined'],
    ['CONVERTED_TO_RFQ', 'Became an RFQ'],
    ['QUOTED', 'Quote sent'],
    ['NEGOTIATION', 'In negotiation'],
    ['AWARDED', 'Won'],
    ['PARTIALLY_AWARDED', 'Partly won'],
    ['LOST', 'Lost'],
    ['CANCELLED', 'Cancelled'],
    ['COMPLETED', 'Completed'],
    ['DUPLICATED', 'Duplicate'],
  ])('names %s as "%s"', (code, words) => {
    expect(leadStatusWords(code)).toBe(words);
  });

  it('covers exactly the sixteen governed lead status codes', () => {
    expect(Object.keys(LEAD_STATUS_WORDS)).toHaveLength(16);
  });

  it('never calls a lead "Quoted": no quote exists when a request merely has lines marked to quote', () => {
    expect(Object.values(LEAD_STATUS_WORDS)).not.toContain('Quoted');
  });

  it('reads a code written in any case, with stray spaces', () => {
    expect(leadStatusWords('converted_to_rfq')).toBe('Became an RFQ');
    expect(leadStatusWords('  Disqualified ')).toBe('Declined');
  });

  it('returns null for an unknown, empty or missing code so the caller picks its own fallback', () => {
    expect(leadStatusWords('SOMETHING_NEW')).toBeNull();
    expect(leadStatusWords('')).toBeNull();
    expect(leadStatusWords('   ')).toBeNull();
    expect(leadStatusWords(null)).toBeNull();
    expect(leadStatusWords(undefined)).toBeNull();
    expect(leadStatusWords('toString')).toBeNull();
  });
});
