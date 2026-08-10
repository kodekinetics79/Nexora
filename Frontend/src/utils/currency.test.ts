import { describe, expect, it } from 'vitest';
import { formatMoney, parseMoneyInput } from './currency';

describe('formatMoney', () => {
  it('formats in the currency the record carries', () => {
    expect(formatMoney(1234.5, 'SAR')).toContain('1,234.5');
    expect(formatMoney(1234.5, 'USD')).toContain('1,234.5');
  });

  it('renders a bare number when the record has no currency', () => {
    // The load-bearing fallback: no currency on the record means no symbol invented.
    expect(formatMoney(1234.5, null)).toBe('1,234.5');
    expect(formatMoney(1234.5, undefined)).toBe('1,234.5');
    expect(formatMoney(1234.5, '   ')).toBe('1,234.5');
    expect(formatMoney(1234.5, null)).not.toContain('$');
  });

  it('never substitutes a default currency', () => {
    for (const rendered of [formatMoney(10, null), formatMoney(10, undefined)]) {
      expect(rendered).not.toMatch(/[$£€]|SAR|USD/);
    }
  });

  it('states the code beside the number when the code is not valid ISO 4217', () => {
    expect(formatMoney(10, 'NOTACODE')).toBe('NOTACODE 10');
  });

  it('renders an explicit gap rather than a zero for missing values', () => {
    expect(formatMoney(null, 'SAR')).toBe('—');
    expect(formatMoney(undefined, 'SAR')).toBe('—');
    expect(formatMoney(Number.NaN, 'SAR')).toBe('—');
    expect(formatMoney(0, 'SAR')).not.toBe('—');
  });
});

describe('parseMoneyInput', () => {
  it('parses a plain number', () => {
    expect(parseMoneyInput('25')).toBe(25);
    expect(parseMoneyInput('25.50')).toBe(25.5);
    expect(parseMoneyInput('-25.50')).toBe(-25.5);
  });

  it('parses values carrying any currency symbol or ISO code', () => {
    // The regression this module exists to prevent: the old `replace('$ ', '')` parser
    // returned NaN for every one of these.
    expect(parseMoneyInput('$ 25.50')).toBe(25.5);
    expect(parseMoneyInput('$25.50')).toBe(25.5);
    expect(parseMoneyInput('SAR 25.50')).toBe(25.5);
    expect(parseMoneyInput('25.50 SAR')).toBe(25.5);
    expect(parseMoneyInput('€25.50')).toBe(25.5);
    expect(parseMoneyInput('  25.50  ')).toBe(25.5);
  });

  it('parses pasted grouped values that the old parser corrupted', () => {
    expect(parseMoneyInput('$1,000.00')).toBe(1000);
    expect(parseMoneyInput('1,234.56')).toBe(1234.56);
    expect(parseMoneyInput('1,234')).toBe(1234);
    expect(parseMoneyInput('SAR 1,000,000.25')).toBe(1000000.25);
  });

  it('reads a comma decimal separator', () => {
    expect(parseMoneyInput('10,50')).toBe(10.5);
    expect(parseMoneyInput('1.234,56')).toBe(1234.56);
  });

  it('returns null rather than NaN so callers cannot store a corrupt figure', () => {
    expect(parseMoneyInput('')).toBeNull();
    expect(parseMoneyInput('   ')).toBeNull();
    expect(parseMoneyInput('abc')).toBeNull();
    expect(parseMoneyInput('$')).toBeNull();
    expect(parseMoneyInput('SAR')).toBeNull();
    expect(parseMoneyInput(null)).toBeNull();
    expect(parseMoneyInput(undefined)).toBeNull();
  });

  it('round-trips what formatMoney produced for a bare number', () => {
    expect(parseMoneyInput(formatMoney(1234.56, null))).toBe(1234.56);
  });
});
