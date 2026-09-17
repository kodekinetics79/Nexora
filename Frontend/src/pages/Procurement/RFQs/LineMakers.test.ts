import { describe, expect, it } from 'vitest';
import { acceptedMakersOf, makerLabel, splitMakers } from './LineMakers';

describe('accepted makers', () => {
  it('reads long document entries as short labels', () => {
    expect(makerLabel('ABB ELECTRICAL INDUSTRIES CO. LTD (SA): P/N AF96-30-00-13')).toBe('ABB AF96-30-00-13');
    expect(makerLabel('SIEMENS AG AUTOMATION AND DRIVE (DE): P/N 3RT2046-1AN20')).toBe('SIEMENS 3RT2046-1AN20');
    expect(makerLabel('SCHNEIDER ELECTRIC USA / SQUARE-D (US): P/N LC1D95M7')).toBe('SCHNEIDER LC1D95M7');
    expect(makerLabel('GENERAL ELECTRIC')).toBe('GENERAL ELECTRIC');
    expect(makerLabel('Eaton')).toBe('Eaton');
  });

  it('splits on ; like email addresses and drops repeats', () => {
    expect(splitMakers(' ABB  S203; GE THQL32010 ;;\nEaton; eaton')).toEqual(['ABB S203', 'GE THQL32010', 'Eaton']);
  });

  it('puts the named maker first and does not repeat it', () => {
    expect(acceptedMakersOf({
      id: 1, manufacturerName: 'ABB', manufacturerPartNumber: 'S203',
      extraFields: JSON.stringify({ 'Approved manufacturers': 'ABB S203; GE THQL32010; Eaton' }),
    })).toEqual(['ABB S203', 'GE THQL32010', 'Eaton']);
  });
});
