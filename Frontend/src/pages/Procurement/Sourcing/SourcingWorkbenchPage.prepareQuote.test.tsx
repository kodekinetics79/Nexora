import { beforeEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { SourcingWorkbench } from '../../../api/services/procurementService';

/**
 * D18 — an approved award with nowhere to put the price.
 *
 * With the best offer approved and no customer quote draft yet, the row's "Price customer quote"
 * was disabled and said nothing a screen reader could hear, and the panel's next step sent the rep
 * "back to the RFQ" to find a button. The draft is one call — the same one View RFQ's Prepare
 * Quote Draft makes — so the panel makes it and lands on the pricing dialog.
 */

vi.mock('react-router-dom', async (importOriginal) => {
  const actual = await importOriginal<typeof import('react-router-dom')>();
  return { ...actual, useParams: () => ({ rfqId: '5' }), useNavigate: () => vi.fn() };
});
vi.mock('../../../api/services/procurementService', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../../../api/services/procurementService')>();
  return { ...actual, default: { ...actual.default, getWorkbench: vi.fn(), getQuoteComparison: vi.fn() } };
});
vi.mock('../../../api/services/rfqService', () => ({ default: { prepareQuoteDraft: vi.fn() } }));
vi.mock('../../../api/services/currencyService', () => ({ default: { getAll: vi.fn().mockResolvedValue({ items: [] }) } }));
vi.mock('../../../api/services/warehouseService', () => ({ default: { getAll: vi.fn().mockResolvedValue({ items: [] }) } }));
vi.mock('../../../context/AuthContext', () => ({
  useAuth: () => ({ userData: { businessUnitId: 7 }, hasPermission: () => true }),
}));

const procurementService = (await import('../../../api/services/procurementService')).default as unknown as {
  getWorkbench: ReturnType<typeof vi.fn>;
  getQuoteComparison: ReturnType<typeof vi.fn>;
};
const rfqService = (await import('../../../api/services/rfqService')).default as unknown as {
  prepareQuoteDraft: ReturnType<typeof vi.fn>;
};
const SourcingWorkbenchPage = (await import('./SourcingWorkbenchPage')).default;

const awardedNoDraft: SourcingWorkbench = {
  rfqId: 5,
  rfqNumber: 'RFQ-5',
  lines: [{ id: 10, rfqId: 5, description: 'Gate valve 2" CL150', requestedQuantity: 12, availableQuantity: 0, reservedQuantity: 0, shortfallQuantity: 0, resolution: 'COVERED' }],
  solicitations: [],
  offers: [{
    id: 3, solicitationId: 1, rfqItemId: 10, supplierId: 3, supplierName: 'Winning Supplier', quoteReference: 'REF-3',
    quoteRevision: 1, currencyId: 1, currencyCode: 'SAR', quantity: 12, availableQuantity: 12, unitPrice: 155.9,
    freightCost: 0, dutyCost: 0, otherCost: 0, landedUnitCost: 155.9, leadTimeDays: 14, reliabilitySnapshot: null,
    validUntil: null, eligible: true, blockingReasons: [], awarded: true, version: 1,
  }],
  awards: [{
    id: 50, rfqItemId: 10, supplierQuotedItemId: 3, supplierName: 'Winning Supplier', supplierId: 3, quantity: 12,
    landedUnitCost: 155.9, currencyCode: 'SAR', currencyId: 1, status: 'APPROVED', version: 1,
  }],
  purchaseOrders: [],
  customerQuoteDraft: null,
};

const renderPage = () => render(
  <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } })}>
    <MemoryRouter><SourcingWorkbenchPage /></MemoryRouter>
  </QueryClientProvider>,
);

beforeEach(() => {
  vi.clearAllMocks();
  procurementService.getWorkbench.mockResolvedValue(awardedNoDraft);
  procurementService.getQuoteComparison.mockResolvedValue({ rfqItemId: 10, lines: [], recommendedSupplierQuotedItemId: null });
  rfqService.prepareQuoteDraft.mockResolvedValue({
    id: 77, quoteNo: 'QT-0926-0001',
    quoteItems: [{ id: 501, rfqItemId: 10, quantity: 12, unitPrice: 0 }],
  });
});

describe('an approved award and no customer quote draft', () => {
  it('makes "Prepare the customer quote" the next step, as the one contained button', async () => {
    renderPage();
    const panel = await screen.findByTestId('sourcing-next-step');
    expect(panel).toHaveTextContent(/Prepare the customer quote, then price its line from the offer/);
    const button = within(panel).getByRole('button', { name: 'Prepare the customer quote' });
    expect(button).toBeEnabled();
    expect(button.className).toMatch(/MuiButton-contained/);
    // The old step, which sent the rep away to find a button, is gone.
    expect(panel).not.toHaveTextContent(/Go back to the RFQ/);
  });

  it('prepares the draft with the same call View RFQ makes, then opens the pricing dialog on the awarded line', async () => {
    renderPage();
    const panel = await screen.findByTestId('sourcing-next-step');
    fireEvent.click(within(panel).getByRole('button', { name: 'Prepare the customer quote' }));

    await waitFor(() => expect(rfqService.prepareQuoteDraft).toHaveBeenCalledWith(5));
    expect(await screen.findByText('Price Customer Quote line')).toBeInTheDocument();
    // The dialog prices the AWARDED line at the AWARDED cost.
    expect(screen.getByText(/Approved landed cost/)).toHaveTextContent('SAR 155.90');
  });

  it('gives the disabled row control a reason a screen reader can hear', async () => {
    renderPage();
    fireEvent.click(await screen.findByRole('tab', { name: /Supplier offers/ }));
    const price = await screen.findByRole('button', { name: /price customer quote/i });
    expect(price).toBeDisabled();
    expect(screen.getByLabelText('Prepare the quote draft first — open the RFQ and press Prepare Quote Draft')).toContainElement(price);
  });
});
