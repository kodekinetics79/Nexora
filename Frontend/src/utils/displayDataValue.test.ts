import { describe, expect, it } from 'vitest';
import { displayDataValue } from './displayDataValue';

describe('displayDataValue', () => {
  it('preserves zero instead of presenting it as missing', () => {
    expect(displayDataValue(0)).toBe(0);
  });

  it('uses an em dash only for actually missing values', () => {
    expect(displayDataValue(null)).toBe('—');
    expect(displayDataValue(undefined)).toBe('—');
    expect(displayDataValue('')).toBe('—');
    expect(displayDataValue('0')).toBe('0');
  });
});
