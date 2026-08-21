import { beforeEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { SnackbarProvider } from 'notistack';
import AvailabilityPage from './AvailabilityPage';
import StockLevelsPage from './StockLevelsPage';
import type { StockLevelsDTO, WarehouseIntelligenceDTO } from '../../../api/services/commercialIntelligenceService';
import type { PaginatedProductResponse } from '../../../api/services/productService';

/**
 * The bootstrap gap. Every stock-write door in the application hangs off a grid row, and every
 * grid row is an INNER JOIN from Models.Inventory — a row that only exists once the product
 * already has stock. A product created through the UI is committed with no Inventory row at all,
 * so it appears on no inventory grid, so there is nothing to click, so its opening stock can
 * never be recorded. A customer who already holds stock cannot initialise the module.
 *
 * What is locked down here:
 *  - a stock screen offers a productless "Record opening stock" door, not only a per-row one;
 *  - that door records the count against the product and warehouse the user picked, through the
 *    existing idempotency-keyed stock/count route, which creates the Inventory row on first use;
 *  - an empty grid says which emptiness it is. "No records match this search" is a lie when no
 *    search was typed: the truth is that nothing is stocked yet, and the screen must say so and
 *    name the next step.
 */

const getAvailability = vi.fn();
const getStockLevels = vi.fn();
const getWarehouses = vi.fn();
const recordStockCount = vi.fn();

vi.mock('../../../api/services/commercialIntelligenceService', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../../../api/services/commercialIntelligenceService')>();
  return {
    ...actual,
    default: {
      getAvailability: (params: unknown) => getAvailability(params),
      getStockLevels: (breachedOnly: boolean) => getStockLevels(breachedOnly),
      getWarehouses: () => getWarehouses(),
      recordStockCount: (
        productId: number, warehouseId: number, countedQuantity: number,
        reason: string | undefined, idempotencyKey: string,
      ) => recordStockCount(productId, warehouseId, countedQuantity, reason, idempotencyKey),
    },
  };
});

const getAllProducts = vi.fn();
vi.mock('../../../api/services/productService', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../../../api/services/productService')>();
  return { ...actual, default: { ...actual.default, getAll: (params: unknown) => getAllProducts(params) } };
});

const hasPermission = vi.fn();
vi.mock('../../../context/AuthContext', () => ({
  useAuth: () => ({ hasPermission: (module: string, action?: string) => hasPermission(module, action) }),
}));

/** Day one: the product exists in the catalogue and has never been stocked. */
const PRODUCTS: PaginatedProductResponse = {
  items: [{
    id: 11, partNo: 'VLV-100', productName: 'Gate valve', qtyOnHand: 0, reorderPoint: 0,
    isActive: true, createdBy: 'seed', createdOn: '2026-08-01T00:00:00Z', images: [], attachments: [],
  }],
  totalItems: 1, pageNumber: 1, pageSize: 20, totalPages: 1,
};

const WAREHOUSES: WarehouseIntelligenceDTO[] = [
  {
    warehouseId: 21, code: 'DMM', name: 'Dammam', location: null, active: true,
    skuCount: 0, onHandUnits: 0, reservedUnits: 0, availableUnits: 0, exceptionCount: 0,
  },
];

/** Nothing has ever been stocked. Not "nothing matched" — nothing exists. */
const NO_LEVELS: StockLevelsDTO = {
  generatedAt: '2026-08-21T09:00:00Z',
  rowCount: 0, configuredCount: 0, breachedCount: 0, unmonitoredCount: 0, rows: [],
};

function renderPage(node: React.ReactNode) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={client}>
      <SnackbarProvider>{node}</SnackbarProvider>
    </QueryClientProvider>,
  );
}

/** Drives a MUI select by its accessible name, then picks the option by its visible text. */
async function pick(label: string, option: RegExp | string) {
  fireEvent.mouseDown(screen.getByRole('combobox', { name: label }));
  fireEvent.click(await screen.findByRole('option', { name: option }));
}

const openDoor = async () =>
  fireEvent.click(await screen.findByRole('button', { name: 'Record opening stock' }));

beforeEach(() => {
  vi.clearAllMocks();
  hasPermission.mockReturnValue(true);
  getAvailability.mockResolvedValue([]);
  getStockLevels.mockResolvedValue(NO_LEVELS);
  getWarehouses.mockResolvedValue(WAREHOUSES);
  getAllProducts.mockResolvedValue(PRODUCTS);
  recordStockCount.mockResolvedValue({
    productId: 11, warehouseId: 21, onHand: 40, available: 40,
    bookQuantity: 0, countedQuantity: 40, variance: 40,
  });
});

describe('Opening stock bootstrap', () => {
  it('offers a productless opening-stock door on availability, so a never-stocked product can be entered', async () => {
    renderPage(<AvailabilityPage />);

    expect(await screen.findByRole('button', { name: 'Record opening stock' })).toBeInTheDocument();
  });

  it('offers the same door on stock levels', async () => {
    renderPage(<StockLevelsPage />);

    expect(await screen.findByRole('button', { name: 'Record opening stock' })).toBeInTheDocument();
  });

  it('records the count against the picked product and warehouse, through the existing keyed route', async () => {
    renderPage(<AvailabilityPage />);

    await openDoor();

    await pick('Product', /VLV-100/);
    await pick('Warehouse', /Dammam/);
    fireEvent.change(screen.getByLabelText(/quantity on hand/i), { target: { value: '40' } });
    fireEvent.click(screen.getByRole('button', { name: 'Save' }));

    await waitFor(() => expect(recordStockCount).toHaveBeenCalledTimes(1));
    const [productId, warehouseId, counted, , key] = recordStockCount.mock.calls[0];
    expect(productId).toBe(11);
    expect(warehouseId).toBe(21);
    expect(counted).toBe(40);
    expect(String(key).length).toBeGreaterThan(0);
  });

  it('will not submit until a product, a warehouse and a quantity have all been given', async () => {
    renderPage(<AvailabilityPage />);

    await openDoor();
    const submit = await screen.findByRole('button', { name: 'Save' });
    expect(submit).toBeDisabled();

    await pick('Product', /VLV-100/);
    expect(submit).toBeDisabled();
    await pick('Warehouse', /Dammam/);
    expect(submit).toBeDisabled();

    fireEvent.change(screen.getByLabelText(/quantity on hand/i), { target: { value: '40' } });
    await waitFor(() => expect(submit).toBeEnabled());
  });
});

describe('Honest empty states', () => {
  it('an unsearched empty availability grid says nothing is stocked yet, not that nothing matched a search', async () => {
    renderPage(<AvailabilityPage />);

    const empty = await screen.findByText(/no stock has been recorded yet/i);
    expect(empty).toBeInTheDocument();
    expect(screen.queryByText(/match this search/i)).not.toBeInTheDocument();
  });

  it('still says "no match" once a search has actually been typed', async () => {
    renderPage(<AvailabilityPage />);

    await screen.findByText(/no stock has been recorded yet/i);
    fireEvent.change(screen.getByLabelText(/search part or product/i), { target: { value: 'zzz' } });

    await waitFor(() => expect(screen.getByText(/match this search/i)).toBeInTheDocument());
  });

  it('an empty stock-levels grid distinguishes "nothing is stocked" from "nothing is breaching"', async () => {
    renderPage(<StockLevelsPage />);

    await waitFor(() => expect(screen.getByText(/no stock has been recorded yet/i)).toBeInTheDocument());
  });
});
