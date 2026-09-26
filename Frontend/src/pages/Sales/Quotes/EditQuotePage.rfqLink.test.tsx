import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { describe, expect, it, vi, beforeEach } from 'vitest';

/**
 * D20 — "Update Quote" used to cut every line loose from the supplier award that priced it.
 *
 * Prepare Quote Draft writes `rfqItemId` on each quote line; that is the only join between a
 * quoted line and the approved supplier award behind its cost. This screen loaded the lines
 * WITHOUT that field and posted them back without it, and the server assigned whatever arrived.
 * One routine price edit and every line on the View page flipped to "Cost Source Pending" — the
 * award→price trace gone, nothing for the later PO to follow.
 *
 * The server now preserves an absent link; this pins the screen's half — it states the link.
 */

const { getById, update, getAll, productGetAll, customerGetAll, policyGet } = vi.hoisted(() => ({
  getById: vi.fn(),
  update: vi.fn(),
  getAll: vi.fn(),
  productGetAll: vi.fn(),
  customerGetAll: vi.fn(),
  policyGet: vi.fn(),
}));

vi.mock('../../../api/services/quoteService', () => ({
  default: { getById, update },
}));
vi.mock('../../../api/services/setupService', () => ({ default: { getAll } }));
vi.mock('../../../api/services/productService', () => ({ default: { getAll: productGetAll } }));
vi.mock('../../../api/services/customerService', () => ({ default: { getAll: customerGetAll } }));
vi.mock('../../../api/services/commercialPolicyService', () => ({
  default: { get: policyGet, getPolicy: policyGet },
}));
vi.mock('./CustomerContextPanel', () => ({ default: () => null }));
vi.mock('../../../context/AuthContext', () => ({
  useAuth: () => ({ userData: { businessUnitId: 1 }, hasPermission: () => true }),
}));
vi.mock('react-hot-toast', () => ({
  toast: Object.assign(vi.fn(), { success: vi.fn(), error: vi.fn() }),
  default: Object.assign(vi.fn(), { success: vi.fn(), error: vi.fn() }),
}));

import EditQuotePage from './EditQuotePage';

const draftFromRfq = {
  id: 9,
  quoteNo: 'QT-0926-0001',
  statusValue: 'Draft',
  statusCode: 'DRAFT',
  statusId: 1,
  currencyId: 4,
  currencyCode: 'SAR',
  customerId: 3,
  rfqId: 12,
  quoteDate: '2026-09-01',
  validUntil: '2026-10-01',
  headerRemarks: '',
  totalAmount: 3741.66,
  quoteItems: [
    {
      id: 1, rfqItemId: 301, productId: 11, productName: 'Gate valve 2" CL150',
      description: '', quantity: 12, unitPrice: 155.9, discount: 0,
      totalAmount: 1870.83, taxAmount: 280.62, taxRatePercentApplied: 15, taxCategory: 'STANDARD',
    },
    {
      id: 2, rfqItemId: 302, productId: 12, productName: 'Globe valve 2" CL150',
      description: '', quantity: 12, unitPrice: 155.9, discount: 0,
      totalAmount: 1870.83, taxAmount: 280.62, taxRatePercentApplied: 15, taxCategory: 'STANDARD',
    },
  ],
};

function renderEdit() {
  const client = new QueryClient({
    defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
  });
  return render(
    <QueryClientProvider client={client}>
      <MemoryRouter initialEntries={['/sales/quotes/edit/9']}>
        <Routes>
          <Route path="/sales/quotes/edit/:id" element={<EditQuotePage />} />
        </Routes>
      </MemoryRouter>
    </QueryClientProvider>,
  );
}

beforeEach(() => {
  vi.clearAllMocks();
  getById.mockResolvedValue(draftFromRfq);
  getAll.mockResolvedValue({ items: [] });
  productGetAll.mockResolvedValue({ items: [] });
  customerGetAll.mockResolvedValue({ items: [] });
  policyGet.mockResolvedValue({ outputTaxRatePercent: 15 });
  update.mockResolvedValue({});
});

describe('a draft prepared from an RFQ, saved again from the Edit screen', () => {
  it('states each line\'s link to its RFQ item, so the award behind the price is kept', async () => {
    renderEdit();
    await screen.findByText(/Revised Summary/i);

    fireEvent.click(screen.getByRole('button', { name: /update quote/i }));

    await waitFor(() => expect(update).toHaveBeenCalled());
    const payload = update.mock.calls[0][1];
    expect(payload.quoteItems.map((i: { id: number; rfqItemId: number | null }) => [i.id, i.rfqItemId]))
      .toEqual([[1, 301], [2, 302]]);
  });
});
