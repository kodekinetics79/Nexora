import { beforeEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import QuotedItemsPage from './QuotedItemsPage';

/**
 * Every data surface has FOUR states, not one. This grid had one: a failed request, a tenant that
 * has recorded nothing, and a search that matched nothing all rendered as MUI's bare "No rows" —
 * which a salesperson reads as a statement about their vendors.
 */

const getAll = vi.fn();
const enqueueSnackbar = vi.fn();

vi.mock('../../api/services/supplierQuotedItemService', () => ({
  default: { getAll: (...a: unknown[]) => getAll(...a), create: vi.fn(), update: vi.fn(), delete: vi.fn() },
}));
vi.mock('../../api/services/supplierService', () => ({ default: { getAll: vi.fn().mockResolvedValue({ items: [] }) } }));
vi.mock('../../api/services/currencyService', () => ({ default: { getAll: vi.fn().mockResolvedValue({ items: [] }) } }));
vi.mock('../../api/services/uomService', () => ({ default: { getAll: vi.fn().mockResolvedValue([]) } }));
vi.mock('notistack', () => ({ useSnackbar: () => ({ enqueueSnackbar }) }));

let permitted = true;
vi.mock('../../context/AuthContext', () => ({
  useAuth: () => ({ userData: { id: 1, businessUnitId: 3 }, hasPermission: () => permitted }),
}));

const row = {
  id: 1, supplierId: 5, supplierName: 'Gulf Valves', itemName: 'Gate valve 6"',
  description: '', uomId: 1, uomName: 'EA', quantity: 10, unitPrice: 250,
  currencyId: 1, currencyCode: 'SAR', quoteReference: 'GV-1', quoteDate: '2026-08-01',
  validUntil: '2026-09-01', taxAmount: 0, discountAmount: 0, isActive: true,
};

function renderPage() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(<QueryClientProvider client={client}><QuotedItemsPage /></QueryClientProvider>);
}

beforeEach(() => {
  vi.clearAllMocks();
  permitted = true;
  getAll.mockResolvedValue([]);
});

describe('Supplier quoted items — the four states of one grid', () => {
  it('true zero names what the list is for and offers the first action', async () => {
    renderPage();
    expect(await screen.findByText(/no supplier prices recorded yet/i)).toBeInTheDocument();
    expect(screen.getByRole('button', { name: /record the first quote/i })).toBeInTheDocument();
  });

  it('filtered-to-zero is visibly different, and offers to clear the filter', async () => {
    getAll.mockResolvedValue([row]);
    renderPage();

    await screen.findByText('Gate valve 6"');
    fireEvent.change(screen.getByPlaceholderText(/search by item/i), { target: { value: 'zzzz' } });

    expect(await screen.findByText(/nothing matches this search/i)).toBeInTheDocument();
    expect(screen.getByRole('button', { name: /clear the search/i })).toBeInTheDocument();
    // It must NOT claim the business has no supplier prices.
    expect(screen.queryByText(/no supplier prices recorded yet/i)).not.toBeInTheDocument();
  });

  it('a failure says the request failed — it never renders as "you have none"', async () => {
    getAll.mockRejectedValue(new Error('boom'));
    renderPage();

    expect(await screen.findByText(/no empty result has been assumed/i)).toBeInTheDocument();
    expect(screen.queryByText(/no supplier prices recorded yet/i)).not.toBeInTheDocument();
    // The transport's own sentence never reaches the reader.
    expect(screen.queryByText(/boom/i)).not.toBeInTheDocument();
  });

  it('a reader who cannot record prices is told why, not just shown nothing', async () => {
    permitted = false;
    renderPage();

    expect(await screen.findAllByText(/ask your administrator for permission to record supplier prices/i))
      .not.toHaveLength(0);
    expect(screen.queryByRole('button', { name: /new quote item/i })).not.toBeInTheDocument();
  });
});
