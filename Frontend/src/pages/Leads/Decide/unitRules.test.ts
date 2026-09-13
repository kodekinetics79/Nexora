import { describe, expect, it } from 'vitest';
import type { LeadDecisionLineDTO } from '../../../api/services/leadDecisionService';
import { readUnit, tenantUnitCode, unitAsWritten, unitCaption } from './unitRules';

const codes = new Set(['EA', 'SET', 'M']);

const line = (overrides: Partial<LeadDecisionLineDTO> = {}): LeadDecisionLineDTO => ({
  id: 1,
  revisionLineId: 10,
  lineItemNo: '00001',
  verificationStatus: 'VERIFIED',
  ...overrides,
});

describe('what the screen can say about a line\'s unit', () => {
  it('pre-selects a spelling the server mapped, and names the word the customer wrote', () => {
    const mapped = line({
      unitOfMeasure: 'EA',
      normalizedUom: 'EA',
      sourceFields: [{ field: 'UnitOfMeasure', rawValue: 'nos' }],
    });
    expect(readUnit(mapped, codes)).toEqual({ kind: 'mapped', code: 'EA', asWritten: 'nos' });
    expect(unitCaption(readUnit(mapped, codes), 'EA')).toBe('as written: nos (read as EA)');
    // Once the rep picks something else, the caption stops claiming the reading.
    expect(unitCaption(readUnit(mapped, codes), 'SET')).toBe('as written: nos');
  });

  it('says nothing extra when the customer wrote the code itself', () => {
    const plain = line({ unitOfMeasure: 'EA', normalizedUom: 'EA', sourceFields: [{ field: 'UnitOfMeasure', rawValue: 'EA' }] });
    expect(unitCaption(readUnit(plain, codes), 'EA')).toBeNull();
  });

  it('keeps a refused word visible instead of an empty picker with no reason', () => {
    const roll = line({ unitOfMeasure: 'Roll', normalizedUom: 'Roll' });
    expect(readUnit(roll, codes)).toEqual({ kind: 'unrecognised', asWritten: 'Roll' });
    expect(unitCaption(readUnit(roll, codes))).toBe('as written: Roll — choose the unit you quote in');
    // Where there is no picker to act on (a locked record, a line not quoted), only the word.
    expect(unitCaption(readUnit(roll, codes), undefined, false)).toBe('as written: Roll');
  });

  it('treats a code the tenant does not carry as not quotable, with the customer\'s word', () => {
    const tenantWithoutEach = new Set(['NOS']);
    const read = readUnit(line({ unitOfMeasure: 'EA', normalizedUom: 'EA', sourceFields: [{ field: 'UOM', rawValue: '10 nos' }] }), tenantWithoutEach);
    expect(read).toEqual({ kind: 'unrecognised', asWritten: 'nos' });
  });

  it('says a missing unit is missing, and never supplies one', () => {
    expect(readUnit(line({ unitOfMeasure: null, normalizedUom: null }), codes)).toEqual({ kind: 'absent' });
    expect(unitCaption({ kind: 'absent' })).toBe('not stated in the request');
    expect(unitAsWritten(line({ unitOfMeasure: '  ' }))).toBeNull();
  });

  it('matches the tenant\'s own spelling of a code regardless of case', () => {
    const options = [{ code: 'Ea', label: 'Each' }, { code: 'SET', label: 'Set' }];
    expect(tenantUnitCode('EA', options)).toBe('Ea');
    expect(tenantUnitCode('set', options)).toBe('SET');
    expect(tenantUnitCode('Roll', options)).toBeUndefined();
    expect(tenantUnitCode(undefined, options)).toBeUndefined();
  });
});
