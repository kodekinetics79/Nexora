import { beforeEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { SnackbarProvider } from 'notistack';
import AvailabilityPage from './AvailabilityPage';
import StockLevelsPage from './StockLevelsPage';
import type {
  AvailabilityDTO, StockLevelsDTO, WarehouseIntelligenceDTO,
} from '../../../api/services/commercialIntelligenceService';

/**
 * The order the empty-state ladder asks its questions in, which decides what a day-one user is
 * told.
 *
 * <p>An empty grid has to answer "why is this empty", and the branches are not interchangeable.
 * Every filter branch — "breaching only", a typed search — is conditioned on something the user
 * did, and each one has a true sentence to say about its own filter. Only one branch reports the
 * state of the module itself: nothing is stocked at all. Asking the filter first means the curious
 * demo user who flicked "Breaching only" was told "No stock row is currently breaching a configured
 * level." — true, and it withholds the fact that there are no stock rows, which is the one thing
 * that would have told them what to do next.</p>
 *
 * <p>Both pages now ask nothing-is-stocked first, and name the remedy. The guard tests below are
 * the other half: the reorder must not swallow the filter answers when stock does exist.</p>
 */

const getAvailability = vi.fn();
const getStockLevels = vi.fn();
const getWarehouses = vi.fn();

vi.mock('../../../api/services/commercialIntelligenceService', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../../../api/services/commercialIntelligenceService')>();
  return {
    ...actual,
    default: {
      getAvailability: (params: unknown) => getAvailability(params),
      getStockLevels: (breachedOnly: boolean) => getStockLevels(breachedOnly),
      getWarehouses: () => getWarehouses(),
    },
  };
});

vi.mock('../../../api/services/productService', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../../../api/services/productService')>();
  return { ...actual, default: { ...actual.default, getAll: () => Promise.resolve({ items: [], totalItems: 0, pageNumber: 1, pageSize: 20, totalPages: 0 }) } };
});

const hasPermission = vi.fn();
vi.mock('../../../context/AuthContext', () => ({
  useAuth: () => ({ hasPermission: (module: string, action?: string) => hasPermission(module, action) }),
}));

const WAREHOUSES: WarehouseIntelligenceDTO[] = [];

/**
 * Day one. `rowCount` is the UNFILTERED total — InventoryIntelligenceController counts it off the
 * evaluated rows before `breachedOnly` narrows the payload — so a zero here means the module has
 * never been initialised, whichever switch the user is sitting on.
 */
const NOTHING_STOCKED: StockLevelsDTO = {
  generatedAt: '2026-08-21T09:00:00Z',
  rowCount: 0, configuredCount: 0, breachedCount: 0, unmonitoredCount: 0, rows: [],
};

/** Stock exists, levels are set, nothing is short. The filter answer is the honest one here. */
const STOCKED_AND_HEALTHY: StockLevelsDTO = {
  generatedAt: '2026-08-21T09:00:00Z',
  rowCount: 2, configuredCount: 2, breachedCount: 0, unmonitoredCount: 0, rows: [],
};

const STOCKED_ROW: AvailabilityDTO = {
  inventoryId: 1, productId: 11, partNumber: 'VLV-100', productName: 'Gate valve',
  warehouseId: 21, warehouseName: 'Dammam',
  onHand: 12, reserved: 0, available: 12, incoming: 0,
  reorderPoint: 0, minimumLevel: null, maximumLevel: null, safetyStock: 0, leadTimeDays: null,
};

function renderPage(node: React.ReactNode) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={client}>
      <SnackbarProvider>{node}</SnackbarProvider>
    </QueryClientProvider>,
  );
}

beforeEach(() => {
  vi.clearAllMocks();
  hasPermission.mockReturnValue(true);
  getWarehouses.mockResolvedValue(WAREHOUSES);
  getAvailability.mockResolvedValue([]);
  getStockLevels.mockResolvedValue(NOTHING_STOCKED);
});

describe('Stock levels — nothing-stocked is asked before the breach filter', () => {
  it('tells a user sitting on "Breaching only" that nothing is stocked, not that nothing is breaching', async () => {
    renderPage(<StockLevelsPage />);
    await screen.findByText(/no stock has been recorded yet/i);

    fireEvent.click(screen.getByLabelText('Breaching only'));

    await waitFor(() => expect(getStockLevels).toHaveBeenCalledWith(true));
    // The whole finding: at HEAD this branch printed the breach sentence and stopped there.
    await waitFor(() => expect(screen.getByText(/no stock has been recorded yet/i)).toBeInTheDocument());
    expect(screen.queryByText(/breaching a configured level/i)).not.toBeInTheDocument();
  });

  it('names the remedy — the opening-stock door — rather than only stating the emptiness', async () => {
    renderPage(<StockLevelsPage />);

    const empty = await screen.findByText(/no stock has been recorded yet/i);
    expect(empty).toHaveTextContent(/Record opening stock/i);
    // The door it points at has to be on the screen it points from.
    expect(screen.getByRole('button', { name: 'Record opening stock' })).toBeInTheDocument();
  });

  it('does not point a view-only user at a button they cannot see', async () => {
    hasPermission.mockReturnValue(false);
    renderPage(<StockLevelsPage />);

    const empty = await screen.findByText(/no stock has been recorded yet/i);
    expect(empty).not.toHaveTextContent(/Use "Record opening stock"/i);
    expect(screen.queryByRole('button', { name: 'Record opening stock' })).not.toBeInTheDocument();
  });

  it('still gives the breach answer when stock exists and nothing is short', async () => {
    getStockLevels.mockResolvedValue(STOCKED_AND_HEALTHY);
    renderPage(<StockLevelsPage />);
    await screen.findByText(/no stock rows exist for this business unit/i);

    fireEvent.click(screen.getByLabelText('Breaching only'));

    await waitFor(() => expect(screen.getByText(/no stock row is currently breaching a configured level/i)).toBeInTheDocument());
    expect(screen.queryByText(/no stock has been recorded yet/i)).not.toBeInTheDocument();
  });
});

describe('Availability — nothing-stocked is asked before the search filter', () => {
  it('tells a user who typed a search on an unstocked tenant that nothing is stocked at all', async () => {
    renderPage(<AvailabilityPage />);
    await screen.findByText(/no stock has been recorded yet/i);

    fireEvent.change(screen.getByLabelText(/search part or product/i), { target: { value: 'zzz' } });

    await waitFor(() => expect(getAvailability).toHaveBeenCalledWith({ search: 'zzz' }));
    await waitFor(() => expect(screen.getByText(/no stock has been recorded yet/i)).toBeInTheDocument());
    expect(screen.queryByText(/match this search/i)).not.toBeInTheDocument();
  });

  it('still says "no match" when the tenant does hold stock and only the search came back empty', async () => {
    getAvailability.mockImplementation((params: { search?: string } | undefined) =>
      Promise.resolve(params?.search ? [] : [STOCKED_ROW]));
    renderPage(<AvailabilityPage />);
    await screen.findByText('VLV-100');

    fireEvent.change(screen.getByLabelText(/search part or product/i), { target: { value: 'zzz' } });

    await waitFor(() => expect(screen.getByText(/match this search/i)).toBeInTheDocument());
    expect(screen.queryByText(/no stock has been recorded yet/i)).not.toBeInTheDocument();
  });
});
