import type { ReactNode } from 'react';
import { act, renderHook, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import type { ProductDTO } from '../../api/services/productService';

const mocks = vi.hoisted(() => ({
  save: vi.fn(), currencies: vi.fn(), markSaved: vi.fn(), acceptRecovered: vi.fn(), notify: vi.fn(),
}));
vi.mock('../../api/services/productService', () => ({ default: { getCurrencies: mocks.currencies } }));
vi.mock('../../api/services/pricingSheetService', () => ({ default: { save: mocks.save } }));
vi.mock('notistack', () => ({ useSnackbar: () => ({ enqueueSnackbar: mocks.notify }) }));
vi.mock('../../hooks/useUnsavedWorkGuard', () => ({
  default: () => ({ recoveredDraft: null, markSaved: mocks.markSaved, acceptRecovered: mocks.acceptRecovered }),
}));

import useProductPricing from './useProductPricing';

const product = (overrides: Partial<ProductDTO> = {}): ProductDTO => ({
  id: 1, partNo: 'PART-1', productName: 'Product one', qtyOnHand: 40, reorderPoint: 8,
  unitCost: 100, sellingPrice: 135, priceCurrencyId: 1, priceCurrencyCode: 'SAR',
  lastPurchaseCost: 25.5, lastPurchaseCurrencyCode: 'EUR', lastPurchaseOn: '2026-09-25T00:00:00Z',
  isActive: true, createdBy: 'manager', createdOn: '2026-09-01', images: [], attachments: [],
  ...overrides,
});

function setup(canEdit = true) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } });
  const invalidate = vi.spyOn(client, 'invalidateQueries');
  const wrapper = ({ children }: { children: ReactNode }) => <QueryClientProvider client={client}>{children}</QueryClientProvider>;
  return { ...renderHook(({ permitted }) => useProductPricing(permitted), { wrapper, initialProps: { permitted: canEdit } }), client, invalidate };
}

describe('Products list pricing', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    mocks.currencies.mockResolvedValue([{ id: 1, code: 'SAR', isBase: true }, { id: 2, code: 'USD', isBase: false }]);
    mocks.save.mockResolvedValue({ saved: 1 });
  });

  it('saves only catalogue prices, refreshes dependent records, and leaves purchase evidence untouched', async () => {
    const { result, invalidate } = setup();
    const row = product();
    const original = structuredClone(row);
    act(() => result.current.start());
    act(() => result.current.update(row, 'salePrice', '150'));
    act(() => result.current.update(row, 'currencyId', '2'));
    await act(async () => { await result.current.save.mutateAsync(); });
    expect(mocks.save).toHaveBeenCalledWith([{ productId: 1, landedCost: 100, salePrice: 150, currencyId: 2 }]);
    expect(row).toEqual(original);
    for (const key of ['products', 'product-detail', 'pricing-sheet', 'stock-price']) {
      expect(invalidate).toHaveBeenCalledWith({ queryKey: [key] });
    }
    expect(result.current.count).toBe(0);
    expect(result.current.editing).toBe(false);
    expect(mocks.markSaved).toHaveBeenCalledWith({});
    expect(mocks.notify).toHaveBeenCalledWith('Saved 1 price.', { variant: 'success' });
  });

  it('blocks incomplete prices and non-finite or non-positive amounts', async () => {
    const { result } = setup();
    const row = product({ unitCost: undefined, sellingPrice: undefined, priceCurrencyId: null });
    act(() => result.current.update(row, 'landedCost', '100'));
    expect(result.current.invalid).toBe(true);
    await act(async () => { await expect(result.current.save.mutateAsync()).rejects.toThrow(); });
    expect(mocks.save).not.toHaveBeenCalled();
    act(() => result.current.update(row, 'salePrice', '125'));
    act(() => result.current.update(row, 'currencyId', '1'));
    expect(result.current.invalid).toBe(false);
    for (const value of ['0', '-1', 'NaN', 'Infinity']) {
      act(() => result.current.update(row, 'landedCost', value));
      expect(result.current.invalid).toBe(true);
    }
  });

  it('allows clearing all pricing fields together without making up a currency', async () => {
    const { result } = setup();
    const row = product();
    act(() => result.current.update(row, 'landedCost', ''));
    act(() => result.current.update(row, 'salePrice', ''));
    act(() => result.current.update(row, 'currencyId', ''));
    expect(result.current.invalid).toBe(false);
    await act(async () => { await result.current.save.mutateAsync(); });
    expect(mocks.save).toHaveBeenCalledWith([{ productId: 1, landedCost: null, salePrice: null, currencyId: null }]);
  });

  it('keeps drafts by product across paging/refetch and drops equivalent numeric input', () => {
    const { result } = setup();
    const first = product();
    const second = product({ id: 2, partNo: 'PART-2' });
    act(() => result.current.update(first, 'salePrice', '150'));
    act(() => result.current.update(second, 'landedCost', '110'));
    expect(result.current.count).toBe(2);
    expect(result.current.draftFor({ ...first, sellingPrice: 140 }).salePrice).toBe('150');
    expect(result.current.draftFor({ ...second }).landedCost).toBe('110');
    act(() => result.current.update({ ...first }, 'salePrice', '135.00'));
    expect(result.current.count).toBe(1);
    expect(result.current.draftFor(first).salePrice).toBe('135');
    act(() => result.current.update(second, 'landedCost', '100.000'));
    expect(result.current.count).toBe(0);
  });

  it('keeps failed-save drafts available for retry and surfaces the server detail', async () => {
    mocks.save.mockRejectedValueOnce({ response: { data: { detail: 'Currency is inactive.' } } });
    const { result } = setup();
    const row = product();
    act(() => result.current.start());
    act(() => result.current.update(row, 'salePrice', '145'));
    await act(async () => { await expect(result.current.save.mutateAsync()).rejects.toBeDefined(); });
    expect(result.current.count).toBe(1);
    expect(result.current.editing).toBe(true);
    expect(result.current.draftFor(row).salePrice).toBe('145');
    await waitFor(() => expect(result.current.error).toBe('Currency is inactive.'));
    expect(mocks.markSaved).not.toHaveBeenCalled();
    await act(async () => { await result.current.save.mutateAsync(); });
    expect(mocks.save).toHaveBeenCalledTimes(2);
    expect(result.current.count).toBe(0);
  });

  it('does not load edit lookups or save without edit permission, including after revocation', async () => {
    const { result, rerender } = setup(false);
    act(() => result.current.start());
    act(() => result.current.update(product(), 'salePrice', '145'));
    expect(result.current.editing).toBe(false);
    expect(mocks.currencies).not.toHaveBeenCalled();
    await act(async () => { await expect(result.current.save.mutateAsync()).rejects.toThrow(); });
    expect(mocks.save).not.toHaveBeenCalled();
    rerender({ permitted: true });
    await waitFor(() => expect(mocks.currencies).toHaveBeenCalledTimes(1));
    rerender({ permitted: false });
    await act(async () => { await expect(result.current.save.mutateAsync()).rejects.toThrow(); });
    expect(mocks.save).not.toHaveBeenCalled();
  });

  it('cancels drafts and errors without writing prices', async () => {
    mocks.save.mockRejectedValueOnce(new Error('Network failed'));
    const { result } = setup();
    const row = product();
    act(() => result.current.start());
    act(() => result.current.update(row, 'salePrice', '160'));
    await act(async () => { await expect(result.current.save.mutateAsync()).rejects.toThrow('Network failed'); });
    act(() => result.current.cancel());
    await waitFor(() => expect(result.current.save.isError).toBe(false));
    expect(result.current.count).toBe(0);
    expect(result.current.editing).toBe(false);
    expect(result.current.draftFor(row).salePrice).toBe('135');
    expect(mocks.markSaved).toHaveBeenCalledWith({});
    expect(mocks.save).toHaveBeenCalledTimes(1);
  });
});
