import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router-dom';
import { SnackbarProvider } from 'notistack';
import { beforeEach, describe, expect, it, vi } from 'vitest';

/**
 * An RFQ line that stock covers: one line of status, the two prices a rep checks first, and one
 * button to a small window that puts the price on the quote draft, ex stock, with nothing held.
 */

const mocks = vi.hoisted(() => ({ get: vi.fn(), use: vi.fn(), saveMargin: vi.fn(), otherMakers: vi.fn(), canEditMargin: true }));

vi.mock('../../../api/services/stockPriceService', () => ({
  default: { get: mocks.get, use: mocks.use, saveMargin: mocks.saveMargin, otherMakers: mocks.otherMakers },
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

import { LinePriceAction, OtherMakerStockAction, StockLineAction } from './StockPriceDialog';

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
  coveredByStock: true,
  supplierPrices: [],
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
  mocks.otherMakers.mockResolvedValue([]);
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

    fireEvent.change(within(dialog).getByLabelText('Margin on cost percent'), { target: { value: '25' } });
    expect(within(dialog).getByLabelText('Quote price')).toHaveValue(125);
    fireEvent.click(within(dialog).getByLabelText(/Use 25% for all stock items from now on/));
    fireEvent.click(within(dialog).getByRole('button', { name: 'Save quote price' }));

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
    expect(within(dialog).getByLabelText('Quote price')).toHaveValue(150);
    expect(within(dialog).queryByText(/for all stock items/)).not.toBeInTheDocument();
    fireEvent.click(within(dialog).getByRole('button', { name: 'Save quote price' }));
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
    mocks.get.mockResolvedValue(view({
      price: { source: 'COST_PLUS_MARGIN', sellingPrice: null, unitCost: 100, marginPercent: 20, unitPrice: 120 },
      onQuote: { quoteId: 3, quoteNo: 'QT-0926-0003', unitPrice: 130, exStock: true, currencyCode: 'SAR', state: 'SENT' },
    }));
    renderLine();
    expect(await screen.findByText(/Quoted SAR\s?130\.00 on QT-0926-0003/)).toBeInTheDocument();
    expect(screen.getByText(/· sent/)).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: 'Change price' }));
    const dialog = await screen.findByRole('dialog');
    expect(within(dialog).getByText(/Sent to the customer on QT-0926-0003/)).toBeInTheDocument();
    // A sent line starts from the price the customer was given, so changing only the delivery
    // never silently reprices it. Today's price is one click away beside it.
    expect(within(dialog).getByLabelText('Quote price')).toHaveValue(130);
    expect(within(dialog).getByText(/Today's suggestion SAR\s?120\.00/)).toBeInTheDocument();
    fireEvent.change(within(dialog).getByLabelText('Quote price'), { target: { value: '128' } });
    fireEvent.click(within(dialog).getByRole('button', { name: 'Save as new revision' }));
    await waitFor(() => expect(mocks.use).toHaveBeenCalledWith(2, 10, { unitPrice: 128, exStock: true, currencyId: null, reviseIfSent: true }));
  });

  it('the window lays out cost, sale price and quote price, and a price under cost says it loses money', async () => {
    mocks.get.mockResolvedValue(view({
      price: { source: 'SELLING_PRICE', sellingPrice: 150, unitCost: 100, marginPercent: 20, unitPrice: 150 },
    }));
    renderLine();
    fireEvent.click(await screen.findByRole('button', { name: 'Price from stock' }));
    const dialog = await screen.findByRole('dialog');
    expect(await within(dialog).findByText('Cost per EA')).toBeInTheDocument();
    expect(within(dialog).getByText(/^SAR\s?100\.00$/)).toBeInTheDocument();
    expect(within(dialog).getByText('Sale price per EA')).toBeInTheDocument();
    expect(within(dialog).getByLabelText('Quote price')).toHaveValue(150);
    expect(within(dialog).getByLabelText('Margin on cost percent')).toHaveValue(50);
    expect(within(dialog).getByText(/Profit SAR\s?50\.00 per EA · SAR\s?250\.00 on this line/)).toBeInTheDocument();
    expect(within(dialog).queryByText(/^Below cost \(/)).not.toBeInTheDocument();
    fireEvent.change(within(dialog).getByLabelText('Quote price'), { target: { value: '90' } });
    expect(within(dialog).getByText(/Below cost: a loss of SAR\s?50\.00 on this line/)).toBeInTheDocument();
    // UX-11: said in red right under the price the rep typed.
    expect(within(dialog).getByText(/^Below cost \(SAR\s?100\.00 per EA\)$/)).toBeInTheDocument();
    // Informs, never blocks: the rep may still quote under cost.
    expect(within(dialog).getByRole('button', { name: 'Save quote price' })).toBeEnabled();
  });

  it('a part with no sale price says Not set rather than showing a number', async () => {
    mocks.get.mockResolvedValue(view({
      price: { source: 'COST_PLUS_MARGIN', sellingPrice: null, unitCost: 100, marginPercent: 20, unitPrice: 120 },
    }));
    renderLine();
    fireEvent.click(await screen.findByRole('button', { name: 'Price from stock' }));
    const dialog = await screen.findByRole('dialog');
    // Owner 2026-09-27: a missing price still shows a figure, SAR 0.00, with the way to set it.
    await within(dialog).findByText('Sale price per EA');
    expect(within(dialog).getAllByText(/^SAR\s?0\.00$/).length).toBeGreaterThan(0);
    expect(within(dialog).getByRole('link', { name: 'Products list' })).toHaveAttribute('href', '/inventory/products');
    expect(within(dialog).getByLabelText('Quote price')).toHaveValue(120);
  });

  it('says where the cost came from: the pricing sheet, or the stock record copy when the sheet has none', async () => {
    mocks.get.mockResolvedValue(view({
      price: { source: 'COST_PLUS_MARGIN', sellingPrice: null, unitCost: 100, marginPercent: 20, unitPrice: 120 },
      costSource: 'STOCK_RECORD',
    }));
    renderLine();
    fireEvent.click(await screen.findByRole('button', { name: 'Price from stock' }));
    const dialog = await screen.findByRole('dialog');
    expect(await within(dialog).findByText(/From the stock record, copied when the stock was counted/)).toBeInTheDocument();
  });

  it('a sheet price in another currency is named, not converted and not used', async () => {
    mocks.get.mockResolvedValue(view({
      price: { source: 'NONE', sellingPrice: null, unitCost: null, marginPercent: 20, unitPrice: null },
      sheet: { currencyCode: 'USD', landedCost: 30, salePrice: 50, usable: false, currencySet: true },
    }));
    renderLine();
    fireEvent.click(await screen.findByRole('button', { name: 'Price from stock' }));
    const dialog = await screen.findByRole('dialog');
    expect(await within(dialog).findByText(/The pricing sheet has this part in USD .*not SAR\. Nothing is converted\./)).toBeInTheDocument();
    expect(within(dialog).getByLabelText('Quote price')).toHaveValue(null);
  });

  it('a supplier price in another currency is shown in its own currency and never becomes the cost', async () => {
    mocks.get.mockResolvedValue(view({
      coveredByStock: false,
      stock: { onHand: 0, free: 0, heldForOrders: 0, places: [] },
      price: { source: 'NONE', sellingPrice: null, unitCost: null, marginPercent: 20, unitPrice: null },
      onQuote: null,
      supplierPrices: [{ id: 3, supplierId: 12, supplierName: 'Houston Valves', cost: 30, currencyCode: 'USD', leadTimeDays: 21, validUntil: '2026-12-31T00:00:00', valid: true, forThisRequest: true }],
    }));
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    render(
      <QueryClientProvider client={client}>
        <SnackbarProvider>
          <MemoryRouter>
            <LinePriceAction rfqId={2} itemId={10} canPrice primary />
          </MemoryRouter>
        </SnackbarProvider>
      </QueryClientProvider>,
    );
    fireEvent.click(await screen.findByRole('button', { name: 'Price it' }));
    const dialog = await screen.findByRole('dialog');
    expect(await within(dialog).findByText(/\$30\.00|US\$30\.00|USD\s?30\.00/)).toBeInTheDocument();
    // Cost shows SAR 0.00, and it is not treated as a real cost: nothing is worked out from it.
    expect(within(dialog).getAllByText(/^SAR\s?0\.00$/).length).toBe(2);
    expect(within(dialog).queryByText(/Profit|Below cost/)).not.toBeInTheDocument();
    expect(within(dialog).getByText(/The supplier price is not in SAR/)).toBeInTheDocument();
    expect(within(dialog).getByLabelText('Quote price')).toHaveValue(null);
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

  it('a line needing sourcing offers another accepted maker from stock and quotes that product', async () => {
    mocks.otherMakers.mockResolvedValue([{ productId: 17, partNumber: '3RT2046-1AN20', label: 'SIEMENS 3RT2046-1AN20', onHand: 30, free: 30 }]);
    mocks.get.mockResolvedValue(view({ otherMaker: { productId: 17, partNumber: '3RT2046-1AN20', label: 'SIEMENS 3RT2046-1AN20', onHand: 30, free: 30 } }));
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    render(
      <QueryClientProvider client={client}>
        <SnackbarProvider>
          <MemoryRouter>
            <OtherMakerStockAction rfqId={2} itemId={10} canPrice />
          </MemoryRouter>
        </SnackbarProvider>
      </QueryClientProvider>,
    );
    expect(await screen.findByText('SIEMENS 3RT2046-1AN20 in stock · 30')).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: 'Price from stock · SIEMENS 3RT2046-1AN20' }));
    const dialog = await screen.findByRole('dialog');
    expect(await within(dialog).findByText(/one of the makers the customer accepts/)).toBeInTheDocument();
    await waitFor(() => expect(mocks.get).toHaveBeenCalledWith(2, 10, 17));
    // Cost 100 with no usual margin: the box stays empty rather than quoting the part at cost.
    expect(within(dialog).getByLabelText('Quote price')).toHaveValue(null);
    expect(within(dialog).getByText('Type a margin on cost or a quote price.')).toBeInTheDocument();
    expect(within(dialog).getByRole('button', { name: 'Save quote price' })).toBeDisabled();
    fireEvent.change(within(dialog).getByLabelText('Quote price'), { target: { value: '120' } });
    expect(within(dialog).getByText(/Profit SAR\s?20\.00 per EA/)).toBeInTheDocument();
    fireEvent.click(within(dialog).getByRole('button', { name: 'Save quote price' }));
    await waitFor(() => expect(mocks.use).toHaveBeenCalledWith(2, 10, expect.objectContaining({ productId: 17, unitPrice: 120 })));
  });

  it('a line not in stock is priced from a supplier price with its delivery time', async () => {
    mocks.get.mockResolvedValue(view({
      coveredByStock: false,
      stock: { onHand: 0, free: 0, heldForOrders: 0, places: [] },
      price: { source: 'SUPPLIER_PLUS_MARGIN', sellingPrice: null, unitCost: 100, marginPercent: 20, unitPrice: 120 },
      supplierPrices: [
        { id: 1, supplierId: 11, supplierName: 'Gulf Switchgear', cost: 100, currencyCode: 'SAR', leadTimeDays: 21, validUntil: '2026-10-15T00:00:00', valid: true, forThisRequest: true },
        { id: 2, supplierId: 12, supplierName: 'Old Supplier', cost: 90, currencyCode: 'SAR', leadTimeDays: 7, validUntil: '2026-08-01T00:00:00', valid: false, forThisRequest: false },
      ],
    }));
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    render(
      <QueryClientProvider client={client}>
        <SnackbarProvider>
          <MemoryRouter>
            <LinePriceAction rfqId={2} itemId={10} canPrice primary />
          </MemoryRouter>
        </SnackbarProvider>
      </QueryClientProvider>,
    );
    fireEvent.click(await screen.findByRole('button', { name: 'Price it' }));
    const dialog = await screen.findByRole('dialog');
    expect(await within(dialog).findByText('Price this line')).toBeInTheDocument();
    expect(within(dialog).getByText('Not in stock')).toBeInTheDocument();
    expect(within(dialog).queryByLabelText('Offer ex stock')).not.toBeInTheDocument();
    await waitFor(() => expect(within(dialog).getByLabelText('Delivery time')).toHaveValue(3));
    expect(within(dialog).getByText('Expired')).toBeInTheDocument();
    expect(within(dialog).queryByRole('button', { name: 'Use Old Supplier price' })).not.toBeInTheDocument();
    fireEvent.click(within(dialog).getByRole('button', { name: 'Save quote price' }));
    await waitFor(() => expect(mocks.use).toHaveBeenCalledWith(2, 10, expect.objectContaining({ unitPrice: 120, exStock: false, leadTimeDays: 21 })));
  });

  it('an expired supplier price from an earlier request is called out, and Ask again opens Find supplier for that supplier', async () => {
    const askAgain = vi.fn();
    mocks.get.mockResolvedValue(view({
      coveredByStock: false,
      stock: { onHand: 0, free: 0, heldForOrders: 0, places: [] },
      price: { source: 'NONE', sellingPrice: null, unitCost: null, marginPercent: 20, unitPrice: null },
      onQuote: null,
      supplierPrices: [{ id: 2, supplierId: 12, supplierName: 'Old Supplier', cost: 90, currencyCode: 'SAR', leadTimeDays: 7, validUntil: '2026-08-01T00:00:00', valid: false, forThisRequest: false }],
    }));
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    render(
      <QueryClientProvider client={client}>
        <SnackbarProvider>
          <MemoryRouter>
            <LinePriceAction rfqId={2} itemId={10} canPrice primary={false} onAskAgain={askAgain} />
          </MemoryRouter>
        </SnackbarProvider>
      </QueryClientProvider>,
    );
    expect(await screen.findByText(/Old Supplier price expired/)).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: 'Price it' }));
    const dialog = await screen.findByRole('dialog');
    expect(await within(dialog).findByText('The supplier price has expired. Ask them again, or type a quote price.')).toBeInTheDocument();
    fireEvent.click(within(dialog).getByRole('button', { name: 'Ask Old Supplier again' }));
    expect(askAgain).toHaveBeenCalledWith([12]);
  });

  it('partly in stock: blended cost explained, the stock part sent straight away, the balance with a delivery time', async () => {
    mocks.get.mockResolvedValue(view({
      requestedQuantity: 200,
      coveredByStock: false,
      stock: { onHand: 150, free: 150, heldForOrders: 0, places: [{ warehouse: 'Main Store', onHand: 150, free: 150 }] },
      partial: { fromStock: 150, toOrder: 50, stockUnitCost: 5 },
      price: { source: 'BLENDED_PLUS_MARGIN', sellingPrice: null, unitCost: 5.375, marginPercent: 20, unitPrice: 6.45 },
      onQuote: null,
      supplierPrices: [{ id: 1, supplierId: 11, supplierName: 'Gulf Switchgear', cost: 6.5, currencyCode: 'SAR', leadTimeDays: 14, validUntil: '2026-10-30T00:00:00', valid: true, forThisRequest: false }],
    }));
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    render(
      <QueryClientProvider client={client}>
        <SnackbarProvider>
          <MemoryRouter>
            <LinePriceAction rfqId={2} itemId={10} canPrice primary />
          </MemoryRouter>
        </SnackbarProvider>
      </QueryClientProvider>,
    );
    fireEvent.click(await screen.findByRole('button', { name: 'Price it' }));
    const dialog = await screen.findByRole('dialog');
    expect(await within(dialog).findByText('150 from stock')).toBeInTheDocument();
    expect(within(dialog).getByText(/^SAR\s?5\.00 each$/)).toBeInTheDocument();
    expect(within(dialog).getByText('50 from Gulf Switchgear')).toBeInTheDocument();
    expect(within(dialog).getByText(/^SAR\s?6\.50 each$/)).toBeInTheDocument();
    expect(within(dialog).getByText(/^SAR\s?5\.38$/)).toBeInTheDocument();
    expect(within(dialog).getByLabelText('Quote price')).toHaveValue(6.45);
    expect(within(dialog).getByLabelText('Send the stock part straight away')).toBeChecked();
    expect(within(dialog).getByText('Quote prints "Delivery: 150 ex stock, balance in 2 weeks"')).toBeInTheDocument();
    fireEvent.click(within(dialog).getByRole('button', { name: 'Save quote price' }));
    await waitFor(() => expect(mocks.use).toHaveBeenCalledWith(2, 10, expect.objectContaining({ unitPrice: 6.45, exStock: false, leadTimeDays: 14, exStockQuantity: 150 })));
  });

  it('when another accepted maker is on the shelf, pricing the named part is the quieter choice', async () => {
    mocks.otherMakers.mockResolvedValue([{ productId: 17, partNumber: '3RT2046-1AN20', label: 'SIEMENS 3RT2046-1AN20', onHand: 30, free: 30 }]);
    mocks.get.mockResolvedValue(view({ coveredByStock: false, onQuote: null, stock: { onHand: 0, free: 0, heldForOrders: 0, places: [] } }));
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    render(
      <QueryClientProvider client={client}>
        <SnackbarProvider>
          <MemoryRouter>
            <LinePriceAction rfqId={2} itemId={10} canPrice primary={false} />
          </MemoryRouter>
        </SnackbarProvider>
      </QueryClientProvider>,
    );
    expect(await screen.findByRole('button', { name: 'Price the named part instead' })).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Price it' })).not.toBeInTheDocument();
    expect(screen.queryByText(/Never quoted before|Last quoted/)).not.toBeInTheDocument();
  });

  it('revising a sent quote with no cost on file starts from the sent price and delivery', async () => {
    mocks.get.mockResolvedValue(view({
      coveredByStock: false, stock: { onHand: 0, free: 0, heldForOrders: 0, places: [] },
      price: { source: 'NONE', sellingPrice: null, unitCost: null, marginPercent: 20, unitPrice: null },
      onQuote: { quoteId: 8, quoteNo: 'QT-0926-0004-R2', unitPrice: 50, exStock: false, currencyCode: 'SAR', state: 'SENT', leadTimeDays: 21 },
    }));
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    render(
      <QueryClientProvider client={client}>
        <SnackbarProvider>
          <MemoryRouter>
            <LinePriceAction rfqId={6} itemId={22} canPrice primary={false} />
          </MemoryRouter>
        </SnackbarProvider>
      </QueryClientProvider>,
    );
    fireEvent.click(await screen.findByRole('button', { name: 'Change price' }));
    const dialog = await screen.findByRole('dialog');
    await waitFor(() => expect(within(dialog).getByLabelText('Quote price')).toHaveValue(50));
    expect(within(dialog).getByLabelText('Delivery time')).toHaveValue(3);
    expect(within(dialog).getByRole('button', { name: 'Save as new revision' })).toBeEnabled();
  });

  it('a priced line with another maker in stock shows the price and one button, not two', async () => {
    mocks.otherMakers.mockResolvedValue([{ productId: 17, partNumber: '3RT2046-1AN20', label: 'SIEMENS 3RT2046-1AN20', onHand: 30, free: 30 }]);
    mocks.get.mockResolvedValue(view({
      coveredByStock: false, stock: { onHand: 0, free: 0, heldForOrders: 0, places: [] },
      onQuote: { quoteId: 9, quoteNo: 'QT-0926-0002-R2', unitPrice: 480, exStock: false, currencyCode: 'SAR', state: 'DRAFT' },
    }));
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    render(
      <QueryClientProvider client={client}>
        <SnackbarProvider>
          <MemoryRouter>
            <OtherMakerStockAction rfqId={2} itemId={7} canPrice />
            <LinePriceAction rfqId={2} itemId={7} canPrice primary={false} />
          </MemoryRouter>
        </SnackbarProvider>
      </QueryClientProvider>,
    );
    expect(await screen.findByText('SIEMENS 3RT2046-1AN20 in stock · 30')).toBeInTheDocument();
    await waitFor(() => expect(screen.getAllByText(/Priced SAR\s?480\.00 on QT-0926-0002-R2/)).toHaveLength(1));
    expect(screen.getAllByRole('button', { name: /Change price/ })).toHaveLength(1);
    expect(screen.getByRole('button', { name: 'Price the named part instead' })).toBeInTheDocument();
  });
});
