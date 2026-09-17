import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router-dom';
import { SnackbarProvider } from 'notistack';
import { beforeEach, describe, expect, it, vi } from 'vitest';

/**
 * An RFQ line that stock covers: one line of status, the two prices a rep checks first, and one
 * button to a small window that puts the price on the quote draft, ex stock, with nothing held.
 */

const mocks = vi.hoisted(() => ({ get: vi.fn(), use: vi.fn(), saveMargin: vi.fn(), canEditMargin: true }));

vi.mock('../../../api/services/stockPriceService', () => ({
  default: { get: mocks.get, use: mocks.use, saveMargin: mocks.saveMargin },
}));
vi.mock('../../../api/services/currencyService', () => ({
  default: { getAll: vi.fn().mockResolvedValue({ items: [{ id: 1, code: 'SAR' }] }) },
}));
vi.mock('../../../context/AuthContext', () => ({
  useAuth: () => ({
    userData: { id: 3, businessUnitId: 7 },
    hasPermission: (module: string, action?: string) => module !== 'Quote Configuration' || action !== 'edit' || mocks.canEditMargin,
  }),
}));

import { StockLineAction } from './StockPriceDialog';

const sold = { kind: 'SOLD', unitPrice: 142.5, currencyCode: 'SAR', quantity: 8, customer: 'Jubail Petrochem', on: '2026-08-28T00:00:00', reference: 'SO-1' };
const won = { kind: 'WON', unitPrice: 150, currencyCode: 'SAR', quantity: 10, customer: 'Al Jazirah', on: '2026-09-16T00:00:00', reference: 'QT-1' };
const quoted = { kind: 'QUOTED', unitPrice: 130, currencyCode: 'SAR', quantity: 4, customer: 'SEC', on: '2026-09-17T00:00:00', reference: 'QT-3' };

const view = (overrides = {}) => ({
  rfqItemId: 10, productId: 3, partNumber: 'GOLD-4', description: 'Test item', maker: null,
  requestedQuantity: 5, unit: 'EA',
  stock: { onHand: 40, free: 40, heldForOrders: 0, places: [{ warehouse: 'Main Store', onHand: 40, free: 40 }] },
  price: { source: 'COST_ONLY', sellingPrice: null, unitCost: 100, marginPercent: null, unitPrice: 100 },
  trackRecord: { lastQuoted: quoted, lastWon: won, timesQuoted: 2, timesWon: 2 },
  history: [sold],
  onQuote: { quoteId: 2, quoteNo: 'QT-0926-0002', unitPrice: 0, exStock: false, currencyCode: null, state: 'DRAFT' },
  currency: { id: 1, code: 'SAR' },
  ...overrides,
});

function renderLine() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={client}>
      <SnackbarProvider>
        <MemoryRouter>
          <StockLineAction rfqId={2} itemId={10} canPrice />
        </MemoryRouter>
      </SnackbarProvider>
    </QueryClientProvider>,
  );
}

beforeEach(() => {
  vi.clearAllMocks();
  mocks.canEditMargin = true;
  mocks.use.mockResolvedValue({ quoteId: 2, quoteNo: 'QT-0926-0002' });
  mocks.saveMargin.mockResolvedValue({ marginPercent: 25 });
});

describe('Price from stock', () => {
  it('says the line is covered, shows the real shelf and the last quoted and won prices', async () => {
    mocks.get.mockResolvedValue(view());
    renderLine();
    expect(await screen.findByText(/All 5 in stock · 40 EA on the shelf/)).toBeInTheDocument();
    expect(screen.getByText(/Last quoted SAR\s?130\.00 · Last won SAR\s?150\.00/)).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Price from stock' })).toBeInTheDocument();
  });

  it('builds the price from cost and margin, saves the company margin when asked, and quotes ex stock', async () => {
    mocks.get.mockResolvedValue(view());
    renderLine();
    fireEvent.click(await screen.findByRole('button', { name: 'Price from stock' }));
    const dialog = await screen.findByRole('dialog');

    expect(within(dialog).getByText(/Main Store 40/)).toBeInTheDocument();
    expect(within(dialog).getByText('Quoted 2 times · won 2')).toBeInTheDocument();
    expect(within(dialog).getAllByText(/Al Jazirah/)).toHaveLength(1);
    expect(within(dialog).getByLabelText('Offer ex stock')).toBeChecked();
    expect(within(dialog).queryByText(/for all stock items/)).not.toBeInTheDocument();

    fireEvent.change(within(dialog).getByLabelText('Margin percent'), { target: { value: '25' } });
    expect(within(dialog).getByLabelText('Unit price')).toHaveValue(125);
    fireEvent.click(within(dialog).getByLabelText(/Use 25% for all stock items from now on/));
    fireEvent.click(within(dialog).getByRole('button', { name: 'Use this price' }));

    await waitFor(() => expect(mocks.use).toHaveBeenCalledWith(2, 10, { unitPrice: 125, exStock: true, currencyId: 1 }));
    expect(mocks.saveMargin).toHaveBeenCalledWith(25);
  });

  it('takes a past price with one click and hides the company margin choice from reps who cannot set it', async () => {
    mocks.canEditMargin = false;
    mocks.get.mockResolvedValue(view());
    renderLine();
    fireEvent.click(await screen.findByRole('button', { name: 'Price from stock' }));
    const dialog = await screen.findByRole('dialog');

    fireEvent.click(within(dialog).getByRole('button', { name: 'Use last won price' }));
    expect(within(dialog).getByLabelText('Unit price')).toHaveValue(150);
    expect(within(dialog).queryByText(/for all stock items/)).not.toBeInTheDocument();
    fireEvent.click(within(dialog).getByRole('button', { name: 'Use this price' }));
    await waitFor(() => expect(mocks.use).toHaveBeenCalledWith(2, 10, { unitPrice: 150, exStock: true, currencyId: 1 }));
    expect(mocks.saveMargin).not.toHaveBeenCalled();
  });

  it('a part quoted but never won says Never won on the line and in the window', async () => {
    mocks.get.mockResolvedValue(view({ trackRecord: { lastQuoted: quoted, lastWon: null, timesQuoted: 1, timesWon: 0 }, history: [] }));
    renderLine();
    expect(await screen.findByText(/Last quoted SAR\s?130\.00 · Never won/)).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: 'Price from stock' }));
    const dialog = await screen.findByRole('dialog');
    expect(within(dialog).getByText('Never won')).toBeInTheDocument();
    expect(within(dialog).getByText('Quoted 1 time · won 0')).toBeInTheDocument();
  });

  it('a line already sent to the customer says so and a new price makes a revision', async () => {
    mocks.get.mockResolvedValue(view({ onQuote: { quoteId: 3, quoteNo: 'QT-0926-0003', unitPrice: 130, exStock: true, currencyCode: 'SAR', state: 'SENT' } }));
    renderLine();
    expect(await screen.findByText(/Quoted SAR\s?130\.00 on QT-0926-0003/)).toBeInTheDocument();
    expect(screen.getByText(/· sent/)).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: 'Change price' }));
    const dialog = await screen.findByRole('dialog');
    expect(within(dialog).getByText(/already sent to the customer/)).toBeInTheDocument();
    fireEvent.change(within(dialog).getByLabelText('Unit price'), { target: { value: '128' } });
    fireEvent.click(within(dialog).getByRole('button', { name: 'Make a revision with this price' }));
    await waitFor(() => expect(mocks.use).toHaveBeenCalledWith(2, 10, { unitPrice: 128, exStock: true, currencyId: null, reviseIfSent: true }));
  });

  it('a line the customer already decided on cannot be repriced', async () => {
    mocks.get.mockResolvedValue(view({ onQuote: { quoteId: 1, quoteNo: 'QT-0926-0001', unitPrice: 150, exStock: false, currencyCode: 'SAR', state: 'DECIDED' } }));
    renderLine();
    expect(await screen.findByText(/customer decided/)).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Change price' })).not.toBeInTheDocument();
  });

  it('once priced, the line says where the price went and offers Change price', async () => {
    mocks.get.mockResolvedValue(view({ onQuote: { quoteId: 2, quoteNo: 'QT-0926-0002', unitPrice: 125, exStock: true, currencyCode: 'SAR', state: 'DRAFT' } }));
    renderLine();
    expect(await screen.findByText(/Priced SAR\s?125\.00 on QT-0926-0002 · ex stock/)).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Change price' })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Open quote' })).toBeInTheDocument();
  });
});
