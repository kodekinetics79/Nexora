import { act, fireEvent, render, screen } from '@testing-library/react';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

/**
 * The form was filled from the server copy on EVERY change to the query's data. A reconnect after a
 * Wi-Fi blip, or the re-read after Restore, replaced every price and remark the rep had typed.
 */

const { getById, update, getAll, productGetAll, customerGetAll, policyGet } = vi.hoisted(() => ({
  getById: vi.fn(), update: vi.fn(), getAll: vi.fn(), productGetAll: vi.fn(), customerGetAll: vi.fn(), policyGet: vi.fn(),
}));
vi.mock('../../../api/services/quoteService', () => ({ default: { getById, update } }));
vi.mock('../../../api/services/setupService', () => ({ default: { getAll } }));
vi.mock('../../../api/services/productService', () => ({ default: { getAll: productGetAll } }));
vi.mock('../../../api/services/customerService', () => ({ default: { getAll: customerGetAll } }));
vi.mock('../../../api/services/commercialPolicyService', () => ({ default: { get: policyGet, getPolicy: policyGet } }));
vi.mock('./CustomerContextPanel', () => ({ default: () => null }));
vi.mock('../../../context/AuthContext', () => ({
  useAuth: () => ({ userData: { businessUnitId: 1 }, hasPermission: () => true }),
}));
vi.mock('react-hot-toast', () => ({
  toast: Object.assign(vi.fn(), { success: vi.fn(), error: vi.fn() }),
  default: Object.assign(vi.fn(), { success: vi.fn(), error: vi.fn() }),
}));

import EditQuotePage from './EditQuotePage';

const quote = {
  id: 9, quoteNo: 'QT-2026-0009', statusValue: 'Draft', statusCode: 'DRAFT', statusId: 1, currencyCode: 'SAR',
  customerId: 3, quoteDate: '2026-08-01', validUntil: '2026-09-01', headerRemarks: 'Saved terms', totalAmount: 100,
  quoteItems: [{ id: 1, productId: 11, productName: 'Cable tray', description: 'Tray', quantity: 1, unitPrice: 100, discount: 0, totalAmount: 100, taxAmount: 15, taxRatePercentApplied: 15, taxCategory: 'STANDARD' }],
};

const changedOnServer = { ...quote, headerRemarks: 'Terms changed on the server', totalAmount: 120 };

const draft = {
  savedAt: '2026-09-01T10:00:00.000Z',
  value: {
    quoteNo: 'QT-2026-0009', customerId: 3, quoteDate: '2026-08-01', validUntil: '2026-09-01',
    headerRemarks: 'Terms typed before the browser died', discountTypeId: null, discountValue: 0,
    items: [{ productId: 11, productName: 'Cable tray', quantity: 4, unitPrice: 100, discountTypeId: null, discountValue: 0, isDeleted: false }],
  },
};

/**
 * TanStack Query tells components about new data on a timer after the fetch settles. Without this
 * flush a "the value did not change" assertion would run before the re-read reached the page, and
 * pass whether or not the page protects the rep's work.
 */
const flushQueryNotifications = () => act(async () => {
  await new Promise((resolve) => setTimeout(resolve, 20));
});

const renderEdit = () => {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } });
  render(
    <QueryClientProvider client={client}>
      <MemoryRouter initialEntries={['/sales/quotes/edit/9']}>
        <Routes><Route path="/sales/quotes/edit/:id" element={<EditQuotePage />} /></Routes>
      </MemoryRouter>
    </QueryClientProvider>,
  );
  return client;
};

beforeEach(() => {
  vi.clearAllMocks();
  sessionStorage.clear();
  getAll.mockResolvedValue({ items: [] });
  productGetAll.mockResolvedValue({ items: [] });
  customerGetAll.mockResolvedValue({ items: [{ id: 3, name: 'Aramco' }] });
  policyGet.mockResolvedValue({ outputTaxRatePercent: 15 });
});
afterEach(() => sessionStorage.clear());

describe('EditQuotePage — the quote is read again while the rep is editing', () => {
  it('keeps what the rep typed when the re-read returns a changed quote', async () => {
    getById.mockResolvedValueOnce(quote).mockResolvedValue(changedOnServer);
    const client = renderEdit();

    const remarks = await screen.findByLabelText(/remarks \/ terms/i);
    expect(remarks).toHaveValue('Saved terms');
    fireEvent.change(remarks, { target: { value: 'Delivery ex-works Dammam, 6 weeks' } });

    await act(async () => {
      await client.refetchQueries({ queryKey: ['quote-edit', '9'] });
    });
    await flushQueryNotifications();
    expect(client.getQueryData(['quote-edit', '9'])).toEqual(changedOnServer);

    expect(getById).toHaveBeenCalledTimes(2);
    expect(screen.getByLabelText(/remarks \/ terms/i)).toHaveValue('Delivery ex-works Dammam, 6 weeks');
  });

  it('keeps a restored draft when the quote is read again', async () => {
    sessionStorage.setItem('nexora.quote.edit.9', JSON.stringify(draft));
    getById.mockResolvedValueOnce(quote).mockResolvedValue(changedOnServer);
    const client = renderEdit();

    await screen.findByText(/unsaved pricing recovered/i);
    fireEvent.click(screen.getByRole('button', { name: /restore/i }));
    expect(screen.getByLabelText(/remarks \/ terms/i)).toHaveValue('Terms typed before the browser died');

    await act(async () => {
      await client.refetchQueries({ queryKey: ['quote-edit', '9'] });
    });
    await flushQueryNotifications();
    expect(client.getQueryData(['quote-edit', '9'])).toEqual(changedOnServer);

    expect(getById).toHaveBeenCalledTimes(2);
    expect(screen.getByLabelText(/remarks \/ terms/i)).toHaveValue('Terms typed before the browser died');
  });
});
