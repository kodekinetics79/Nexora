import { beforeEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { SourcingWorkbench } from '../../../api/services/procurementService';

/**
 * Two controls on the sourcing workbench that stopped a rep without saying why.
 *
 *  - "Capture response" was disabled until the solicitation reached SENT, with no reason and no
 *    mention that the Supplier Quote Inbox captures a reply regardless of delivery state.
 *  - "Price customer quote" was simply absent until a customer quote draft existed, so a rep who
 *    had just approved a supplier offer looked for the pricing step and found nothing.
 */

vi.mock('react-router-dom', async (importOriginal) => {
  const actual = await importOriginal<typeof import('react-router-dom')>();
  return { ...actual, useParams: () => ({ rfqId: '5' }), useNavigate: () => vi.fn() };
});
vi.mock('../../../api/services/procurementService', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../../../api/services/procurementService')>();
  return {
    ...actual,
    default: { ...actual.default, getWorkbench: vi.fn(), getQuoteComparison: vi.fn() },
  };
});
vi.mock('../../../api/services/currencyService', () => ({ default: { getAll: vi.fn().mockResolvedValue({ items: [] }) } }));
vi.mock('../../../api/services/warehouseService', () => ({ default: { getAll: vi.fn().mockResolvedValue({ items: [] }) } }));
vi.mock('../../../context/AuthContext', () => ({
  useAuth: () => ({ userData: { businessUnitId: 7 }, hasPermission: () => true }),
}));

const procurementService = (await import('../../../api/services/procurementService')).default as unknown as {
  getWorkbench: ReturnType<typeof vi.fn>;
  getQuoteComparison: ReturnType<typeof vi.fn>;
};
const SourcingWorkbenchPage = (await import('./SourcingWorkbenchPage')).default;

const workbench: SourcingWorkbench = {
  rfqId: 5,
  rfqNumber: 'RFQ-5',
  lines: [{ id: 10, rfqId: 5, description: 'Pressure transmitter', requestedQuantity: 5, availableQuantity: 0, reservedQuantity: 0, shortfallQuantity: 5, resolution: 'SHORTAGE' }],
  solicitations: [{
    id: 1, rfqId: 5, supplierId: 3, supplierName: 'Queued Supplier', status: 'QUEUED' as never, channel: 'EMAIL',
    attemptCount: 0, updatedOn: '2026-09-01T00:00:00Z', requestedRfqItemIds: [10],
  }],
  offers: [{
    id: 3, solicitationId: 1, rfqItemId: 10, supplierId: 3, supplierName: 'Winning Supplier', quoteReference: 'REF-3',
    quoteRevision: 1, currencyId: 1, currencyCode: 'SAR', quantity: 5, availableQuantity: 5, unitPrice: 1000,
    freightCost: 0, dutyCost: 0, otherCost: 0, landedUnitCost: 1000, leadTimeDays: 14, reliabilitySnapshot: null,
    validUntil: null, eligible: true, blockingReasons: [], awarded: true, version: 1,
  }],
  awards: [{
    id: 50, rfqItemId: 10, supplierQuotedItemId: 3, supplierName: 'Winning Supplier', supplierId: 3, quantity: 5,
    landedUnitCost: 1000, currencyCode: 'SAR', currencyId: 1, status: 'APPROVED', version: 1,
  }],
  purchaseOrders: [],
  // No customer quote draft yet: the pricing step has nowhere to write.
  customerQuoteDraft: null,
};

const renderPage = () => render(
  <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
    <MemoryRouter><SourcingWorkbenchPage /></MemoryRouter>
  </QueryClientProvider>,
);

beforeEach(() => {
  vi.clearAllMocks();
  procurementService.getWorkbench.mockResolvedValue(workbench);
  procurementService.getQuoteComparison.mockResolvedValue({ rfqItemId: 10, lines: [], recommendedSupplierQuotedItemId: null });
});

describe('SourcingWorkbenchPage — disabled controls print their reason', () => {
  it('Capture response says it waits for delivery and links to the Supplier Quote Inbox', async () => {
    renderPage();
    fireEvent.click(await screen.findByRole('tab', { name: /Solicitations/ }));

    const capture = await screen.findByRole('button', { name: /capture response/i });
    expect(capture).toBeDisabled();
    fireEvent.mouseOver(capture.parentElement as HTMLElement);
    expect(await screen.findByText(/available once the supplier rfq is delivered/i)).toBeInTheDocument();
    expect(screen.getByRole('link', { name: /supplier quote inbox/i })).toHaveAttribute('href', '/procurement/supplier-quotes');
  });

  it('Price customer quote stays visible, disabled, and points at Prepare Quote Draft on the RFQ', async () => {
    renderPage();
    fireEvent.click(await screen.findByRole('tab', { name: /Supplier offers/ }));

    const price = await screen.findByRole('button', { name: /price customer quote/i });
    expect(price).toBeDisabled();
    fireEvent.mouseOver(price.parentElement as HTMLElement);
    expect(await screen.findByText(/draft the customer quote first/i)).toBeInTheDocument();
    expect(screen.getByRole('link', { name: /the rfq/i })).toHaveAttribute('href', '/procurement/rfqs/view/5');
  });
});
