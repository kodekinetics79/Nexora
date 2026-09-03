import { beforeEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { SourcingWorkbench, SupplierSolicitation } from '../../../api/services/procurementService';

/**
 * A deploy restarts the single instance. A merge landing while a supplier RFQ is mid-send leaves
 * the claim stale; the worker fences it, and the provider MAY ALREADY HOLD THE MESSAGE.
 *
 * Both terminal delivery states show as DELIVERYFAILED on the solicitation. Until this, the
 * screen told them apart with nothing at all — it printed the raw code DELIVERY_UNCERTAIN in 12px
 * red beside a one-click Retry, which is the one action that can put a SECOND RFQ for the same
 * line in the supplier's inbox. Production solicitation 1 (RFQ 58, supplier 77) has been sitting
 * in exactly that state since 2026-08-20.
 *
 * The server refuses an unconfirmed retry of an UNCERTAIN delivery
 * (ProcurementApplicationService.RetrySolicitationAsync). These tests pin the screen half: the
 * tenant is told which state it is in, and the confirmation the server demands is actually
 * reachable — and is NOT sent for a delivery that definitely never happened.
 */

vi.mock('react-router-dom', async (importOriginal) => {
  const actual = await importOriginal<typeof import('react-router-dom')>();
  return { ...actual, useParams: () => ({ rfqId: '5' }), useNavigate: () => vi.fn() };
});
vi.mock('../../../api/services/procurementService', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../../../api/services/procurementService')>();
  return {
    ...actual,
    default: { ...actual.default, getWorkbench: vi.fn(), getQuoteComparison: vi.fn(), retrySolicitation: vi.fn() },
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
  retrySolicitation: ReturnType<typeof vi.fn>;
};
const SourcingWorkbenchPage = (await import('./SourcingWorkbenchPage')).default;

const solicitation = (deliveryOutcome: SupplierSolicitation['deliveryOutcome'], lastErrorCode: string): SupplierSolicitation => ({
  id: 1, rfqId: 5, supplierId: 77, supplierName: 'Interrupted Supplier', supplierEmail: 'sales@supplier.test',
  status: 'DELIVERY_FAILED' as never, channel: 'EMAIL', attemptCount: 1, lastErrorCode, deliveryOutcome,
  updatedOn: '2026-08-20T00:27:47Z', requestedRfqItemIds: [10],
});

const workbench = (item: SupplierSolicitation): SourcingWorkbench => ({
  rfqId: 5,
  rfqNumber: 'RFQ-5',
  lines: [{ id: 10, rfqId: 5, description: 'Pressure transmitter', requestedQuantity: 5, availableQuantity: 0, reservedQuantity: 0, shortfallQuantity: 5, resolution: 'SHORTAGE' }],
  solicitations: [item],
  offers: [],
  awards: [],
  purchaseOrders: [],
  customerQuoteDraft: null,
});

const renderPage = () => render(
  <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
    <MemoryRouter><SourcingWorkbenchPage /></MemoryRouter>
  </QueryClientProvider>,
);

beforeEach(() => {
  vi.clearAllMocks();
  procurementService.retrySolicitation.mockResolvedValue({});
  procurementService.getQuoteComparison.mockResolvedValue({ rfqItemId: 10, lines: [], recommendedSupplierQuotedItemId: null });
});

describe('SourcingWorkbenchPage — the two terminal delivery states are not the same fact', () => {
  it('an uncertain delivery is explained and Retry asks the operator to check first', async () => {
    procurementService.getWorkbench.mockResolvedValue(workbench(solicitation('UNCERTAIN', 'DELIVERY_UNCERTAIN')));
    renderPage();
    fireEvent.click(await screen.findByRole('tab', { name: /Solicitations/ }));

    // Said in words the rep can act on, not as an error code.
    expect(await screen.findByText(/may already have this RFQ/i)).toBeInTheDocument();
    expect(screen.queryByText('DELIVERY_UNCERTAIN')).not.toBeInTheDocument();

    // Retry does not send. It asks.
    fireEvent.click(screen.getByRole('button', { name: /^retry$/i }));
    expect(await screen.findByRole('heading', { name: /did this rfq reach interrupted supplier/i })).toBeInTheDocument();
    expect(screen.getByRole('link', { name: /supplier quote inbox/i })).toHaveAttribute('href', '/procurement/supplier-quotes');
    expect(procurementService.retrySolicitation).not.toHaveBeenCalled();

    // Only the explicit confirmation authorises a second send.
    fireEvent.click(screen.getByRole('button', { name: /it never arrived/i }));
    await waitFor(() => expect(procurementService.retrySolicitation).toHaveBeenCalledTimes(1));
    expect(procurementService.retrySolicitation.mock.calls[0][2]).toBe(true);
  });

  it('a delivery that definitely never happened stays one click, and confirms nothing', async () => {
    // The control. If this ever opens the dialog, the warning has been attached to the wrong
    // state and every mailbox misconfiguration now costs a phone call to the supplier.
    procurementService.getWorkbench.mockResolvedValue(
      workbench(solicitation('NOT_DELIVERED', 'DELIVERY_PROVIDER_NOT_CONFIGURED')));
    renderPage();
    fireEvent.click(await screen.findByRole('tab', { name: /Solicitations/ }));

    expect(await screen.findByText(/retrying is safe/i)).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: /^retry$/i }));

    await waitFor(() => expect(procurementService.retrySolicitation).toHaveBeenCalledTimes(1));
    expect(procurementService.retrySolicitation.mock.calls[0][2]).toBe(false);
    expect(screen.queryByRole('heading', { name: /did this rfq reach/i })).not.toBeInTheDocument();
  });
});
