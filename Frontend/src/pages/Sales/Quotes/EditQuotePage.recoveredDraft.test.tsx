import { fireEvent, render, screen } from '@testing-library/react';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

/**
 * `useUnsavedWorkGuard` has written a sessionStorage draft from this page since the day it was
 * added, and this page never read `guard.recoveredDraft` back. A rep whose browser died mid-price
 * had their work saved to a place nothing would ever show them. The lead decision workbench is
 * the one screen that offered the draft back; this is the same banner here.
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

const draft = {
  savedAt: '2026-09-01T10:00:00.000Z',
  value: {
    quoteNo: 'QT-2026-0009', customerId: 3, quoteDate: '2026-08-01', validUntil: '2026-09-01',
    headerRemarks: 'Terms typed before the browser died', discountTypeId: null, discountValue: 0,
    items: [{ productId: 11, productName: 'Cable tray', quantity: 4, unitPrice: 100, discountTypeId: null, discountValue: 0, isDeleted: false }],
  },
};

const renderEdit = () => render(
  <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } })}>
    <MemoryRouter initialEntries={['/sales/quotes/edit/9']}>
      <Routes><Route path="/sales/quotes/edit/:id" element={<EditQuotePage />} /></Routes>
    </MemoryRouter>
  </QueryClientProvider>,
);

beforeEach(() => {
  vi.clearAllMocks();
  sessionStorage.clear();
  getById.mockResolvedValue(quote);
  getAll.mockResolvedValue({ items: [] });
  productGetAll.mockResolvedValue({ items: [] });
  customerGetAll.mockResolvedValue({ items: [{ id: 3, name: 'Aramco' }] });
  policyGet.mockResolvedValue({ outputTaxRatePercent: 15 });
});
afterEach(() => sessionStorage.clear());

describe('EditQuotePage — a draft left in this browser', () => {
  it('is offered back, and Restore puts it on the form', async () => {
    sessionStorage.setItem('nexora.quote.edit.9', JSON.stringify(draft));
    renderEdit();

    expect(await screen.findByText(/unsaved pricing recovered/i)).toBeInTheDocument();
    expect(screen.getByLabelText(/remarks \/ terms/i)).toHaveValue('Saved terms');

    fireEvent.click(screen.getByRole('button', { name: /restore/i }));

    expect(screen.getByLabelText(/remarks \/ terms/i)).toHaveValue('Terms typed before the browser died');
    expect(screen.queryByText(/unsaved pricing recovered/i)).not.toBeInTheDocument();
    expect(sessionStorage.getItem('nexora.quote.edit.9')).toBeNull();
  });

  it('Discard keeps the saved version and forgets the draft', async () => {
    sessionStorage.setItem('nexora.quote.edit.9', JSON.stringify(draft));
    renderEdit();

    await screen.findByText(/unsaved pricing recovered/i);
    fireEvent.click(screen.getByRole('button', { name: /discard/i }));

    expect(screen.getByLabelText(/remarks \/ terms/i)).toHaveValue('Saved terms');
    expect(screen.queryByText(/unsaved pricing recovered/i)).not.toBeInTheDocument();
    expect(sessionStorage.getItem('nexora.quote.edit.9')).toBeNull();
  });

  it('shows no banner when there is nothing to recover (the control)', async () => {
    renderEdit();
    expect(await screen.findByLabelText(/remarks \/ terms/i)).toHaveValue('Saved terms');
    expect(screen.queryByText(/unsaved pricing recovered/i)).not.toBeInTheDocument();
  });
});
