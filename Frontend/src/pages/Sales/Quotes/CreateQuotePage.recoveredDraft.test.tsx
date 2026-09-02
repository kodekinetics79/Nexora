import { fireEvent, render, screen } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

/** See EditQuotePage.recoveredDraft.test.tsx — the same missing read-back, on the create screen. */

const { create, rfqGetAll, productGetAll, setupGetAll, currencyGetAll, policyGet } = vi.hoisted(() => ({
  create: vi.fn(), rfqGetAll: vi.fn(), productGetAll: vi.fn(), setupGetAll: vi.fn(), currencyGetAll: vi.fn(), policyGet: vi.fn(),
}));
vi.mock('../../../api/services/quoteService', () => ({ default: { create } }));
vi.mock('../../../api/services/rfqService', () => ({ default: { getAll: rfqGetAll } }));
vi.mock('../../../api/services/productService', () => ({ default: { getAll: productGetAll } }));
vi.mock('../../../api/services/setupService', () => ({ default: { getAll: setupGetAll } }));
vi.mock('../../../api/services/currencyService', () => ({ default: { getAll: currencyGetAll } }));
vi.mock('../../../api/services/commercialPolicyService', () => ({ default: { get: policyGet, getPolicy: policyGet } }));
vi.mock('./CustomerContextPanel', () => ({ default: () => null }));
vi.mock('../../../context/AuthContext', () => ({
  useAuth: () => ({ userData: { businessUnitId: 1 }, hasPermission: () => true }),
}));
vi.mock('react-hot-toast', () => ({
  toast: Object.assign(vi.fn(), { success: vi.fn(), error: vi.fn() }),
  default: Object.assign(vi.fn(), { success: vi.fn(), error: vi.fn() }),
}));

import CreateQuotePage from './CreateQuotePage';

const draft = {
  savedAt: '2026-09-01T10:00:00.000Z',
  value: {
    rfqId: null, customerId: null, quoteDate: '2026-09-01', validUntil: '2026-10-01',
    headerRemarks: 'Delivery within 4 weeks, DAP Dammam', discountTypeId: null, discountValue: 0,
    items: [{ productId: 11, productName: 'Cable tray', quantity: 4, unitPrice: 100, discountTypeId: null, discountValue: 0 }],
  },
};

const renderCreate = () => render(
  <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } })}>
    <MemoryRouter initialEntries={['/sales/quotes/create']}><CreateQuotePage /></MemoryRouter>
  </QueryClientProvider>,
);

beforeEach(() => {
  vi.clearAllMocks();
  sessionStorage.clear();
  rfqGetAll.mockResolvedValue({ items: [] });
  productGetAll.mockResolvedValue({ items: [] });
  setupGetAll.mockResolvedValue({ items: [] });
  currencyGetAll.mockResolvedValue({ items: [] });
  policyGet.mockResolvedValue({ outputTaxRatePercent: 15 });
});
afterEach(() => sessionStorage.clear());

describe('CreateQuotePage — a draft left in this browser', () => {
  it('is offered back, and Restore puts it on the form', async () => {
    sessionStorage.setItem('nexora.quote.create', JSON.stringify(draft));
    renderCreate();

    expect(await screen.findByText(/unsaved quote recovered/i)).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: /restore/i }));

    expect(screen.getByLabelText(/header remarks \/ terms/i)).toHaveValue('Delivery within 4 weeks, DAP Dammam');
    expect(screen.queryByText(/unsaved quote recovered/i)).not.toBeInTheDocument();
    expect(sessionStorage.getItem('nexora.quote.create')).toBeNull();
  });

  it('shows no banner when there is nothing to recover (the control)', async () => {
    renderCreate();
    expect(await screen.findByLabelText(/header remarks \/ terms/i)).toHaveValue('');
    expect(screen.queryByText(/unsaved quote recovered/i)).not.toBeInTheDocument();
  });
});
