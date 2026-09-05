import { describe, expect, it } from 'vitest';
import type { CurrencyDTO } from '../../../api/services/currencyService';
import { createOrderBlockers, defaultCurrencyId } from './createOrderForm';

const currency = (over: Partial<CurrencyDTO> & { id: number; code: string }): CurrencyDTO => ({
  currencyName: over.code,
  symbol: null,
  exchangeRate: 1,
  isBaseCurrency: false,
  businessUnitId: 1,
  isActive: true,
  ...over,
});

describe('defaultCurrencyId', () => {
  it('starts on the tenant base currency when there is one', () => {
    const list = [currency({ id: 1, code: 'USD' }), currency({ id: 2, code: 'SAR', isBaseCurrency: true })];
    expect(defaultCurrencyId(list)).toBe(2);
  });

  it('starts on the only active currency when no base currency is flagged', () => {
    const list = [currency({ id: 3, code: 'AED' }), currency({ id: 4, code: 'PKR', isActive: false })];
    expect(defaultCurrencyId(list)).toBe(3);
  });

  it('refuses to guess between several currencies with no base', () => {
    const list = [currency({ id: 1, code: 'USD' }), currency({ id: 2, code: 'SAR' })];
    expect(defaultCurrencyId(list)).toBeNull();
    expect(defaultCurrencyId([])).toBeNull();
    expect(defaultCurrencyId(undefined)).toBeNull();
  });

  it('never defaults to an inactive base currency', () => {
    expect(defaultCurrencyId([currency({ id: 9, code: 'OLD', isBaseCurrency: true, isActive: false })])).toBeNull();
  });
});

describe('createOrderBlockers', () => {
  it('names the missing currency in the words the rep should read', () => {
    const blockers = createOrderBlockers({ customerId: 5, currencyId: null, itemCount: 1 });
    expect(blockers).toEqual(['Please choose the currency the customer will be invoiced in']);
  });

  it('is empty when the order can be raised', () => {
    expect(createOrderBlockers({ customerId: 5, currencyId: 2, itemCount: 1 })).toEqual([]);
  });

  it('lists every blocker, customer first', () => {
    const blockers = createOrderBlockers({ customerId: null, currencyId: null, itemCount: 0 });
    expect(blockers).toHaveLength(3);
    expect(blockers[0]).toBe('Please select a customer');
  });
});
