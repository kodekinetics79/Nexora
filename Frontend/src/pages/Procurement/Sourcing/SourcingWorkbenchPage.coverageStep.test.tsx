import { beforeEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { SourcingLine, SourcingWorkbench } from '../../../api/services/procurementService';

/**
 * D12b — the Coverage next step described a screen that did not exist.
 *
 * On QT-0926-0001 the panel read "4 lines are still short and no supplier has been asked. Send a
 * supplier RFQ, one line at a time." while one line was awarded and another had a SENT supplier
 * RFQ with a reply due. The sentence is now derived from the lines, solicitations, offers and
 * awards the page already holds, and the button still points at the first line nobody has asked.
 */

vi.mock('react-router-dom', async (importOriginal) => {
  const actual = await importOriginal<typeof import('react-router-dom')>();
  return { ...actual, useParams: () => ({ rfqId: '5' }), useNavigate: () => vi.fn() };
});
vi.mock('../../../api/services/procurementService', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../../../api/services/procurementService')>();
  return {
    ...actual,
    default: { ...actual.default, getWorkbench: vi.fn(), getQuoteComparison: vi.fn(), createOrOpenSourcingCase: vi.fn() },
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
  createOrOpenSourcingCase: ReturnType<typeof vi.fn>;
};
const SourcingWorkbenchPage = (await import('./SourcingWorkbenchPage')).default;

const line = (id: number, description: string, shortfall: number): SourcingLine => ({
  id, rfqId: 5, description, requestedQuantity: 12, availableQuantity: 0, reservedQuantity: 0,
  shortfallQuantity: shortfall, resolution: shortfall === 0 ? 'COVERED' : 'SHORTAGE',
});

/** One line awarded, one out with a supplier, three nobody has asked about. */
const fiveLines: SourcingWorkbench = {
  rfqId: 5,
  rfqNumber: 'RFQ-5',
  lines: [
    line(10, 'Gate valve 2" CL150', 0),
    line(11, 'Globe valve 2" CL150', 12),
    line(12, 'Check valve 2" CL150', 12),
    line(13, 'Ball valve 2" CL150', 12),
    line(14, 'Strainer 2" CL150', 12),
  ],
  solicitations: [{
    id: 1, rfqId: 5, supplierId: 8, supplierName: 'Asked Supplier', status: 'SENT', channel: 'EMAIL', attemptCount: 1,
    sentOn: '2026-09-15T08:00:00Z', dueOn: '2026-09-22T12:00:00Z', updatedOn: '2026-09-15T08:00:00Z',
    requestedRfqItemIds: [11],
  }],
  offers: [{
    id: 3, solicitationId: 2, rfqItemId: 10, supplierId: 3, supplierName: 'Winning Supplier', quoteReference: 'REF-3',
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
  procurementService.getWorkbench.mockResolvedValue(fiveLines);
  procurementService.getQuoteComparison.mockResolvedValue({ rfqItemId: 10, lines: [], recommendedSupplierQuotedItemId: null });
  procurementService.createOrOpenSourcingCase.mockResolvedValue({ id: 900 });
});

describe('the Coverage next step with mixed progress across the lines', () => {
  it('says what is true of every line, including the reply that is due', async () => {
    renderPage();
    const panel = await screen.findByTestId('sourcing-next-step');
    expect(panel).toHaveTextContent(
      '1 line awarded. 1 line is with a supplier (reply due 22 Sep). 3 lines still need a supplier.',
    );
    expect(panel).not.toHaveTextContent(/no supplier has been asked/);
    expect(panel).not.toHaveTextContent(/4 lines/);
  });

  it('points the button at the first line that truly has no supplier RFQ', async () => {
    renderPage();
    const panel = await screen.findByTestId('sourcing-next-step');
    fireEvent.click(within(panel).getByRole('button', { name: 'Ask suppliers for the first line' }));
    // Line 12 — not 10 (awarded), not 11 (already with a supplier).
    await waitFor(() => expect(procurementService.createOrOpenSourcingCase).toHaveBeenCalledWith(5, 12, 10));
  });

  it('stays plain when nothing has been awarded or asked', async () => {
    procurementService.getWorkbench.mockResolvedValue({ ...fiveLines, solicitations: [], offers: [], awards: [], lines: fiveLines.lines.slice(1) });
    renderPage();
    const panel = await screen.findByTestId('sourcing-next-step');
    expect(panel).toHaveTextContent('4 lines still need a supplier.');
    expect(panel).not.toHaveTextContent(/awarded|with a supplier/);
  });
});
