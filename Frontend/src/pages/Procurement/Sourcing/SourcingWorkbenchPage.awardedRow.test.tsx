import { beforeEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, within } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { QuoteComparisonResult, SourcingWorkbench, SupplierOffer } from '../../../api/services/procurementService';

/**
 * D17 — the row the buyer had JUST approved contradicted them.
 *
 * Driving QT-0926-0001: press "Approve award" on the best offer, and the same row then read
 * "Not scored — this offer cannot be awarded as it stands: sourcing requirement already covered",
 * wore the "sourcing requirement already covered" chip, and a "Needs attention: 1 supplier offer
 * cannot be awarded until missing commercial evidence is resolved" banner appeared above the
 * table — all about the offer whose own award had covered the requirement.
 *
 * An offer with an APPROVED award is presented as awarded. Other offers on the covered line may
 * still carry the blocker, because for them it is true.
 */

vi.mock('react-router-dom', async (importOriginal) => {
  const actual = await importOriginal<typeof import('react-router-dom')>();
  return { ...actual, useParams: () => ({ rfqId: '5' }), useNavigate: () => vi.fn() };
});
vi.mock('../../../api/services/procurementService', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../../../api/services/procurementService')>();
  return { ...actual, default: { ...actual.default, getWorkbench: vi.fn(), getQuoteComparison: vi.fn() } };
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

const ALREADY_COVERED = 'sourcing requirement already covered';

const offer = (id: number, supplierName: string, awarded: boolean): SupplierOffer => ({
  id, solicitationId: 1, rfqItemId: 10, supplierId: id, supplierName, quoteReference: `REF-${id}`, quoteRevision: 1,
  currencyId: 1, currencyCode: 'SAR', quantity: 12, availableQuantity: 12, unitPrice: 155.9,
  freightCost: 0, dutyCost: 0, otherCost: 0, landedUnitCost: 155.9, leadTimeDays: 14, reliabilitySnapshot: null,
  validUntil: null,
  // What the server said about both offers once the line was covered: blocked, same reason.
  eligible: false, blockingReasons: [ALREADY_COVERED], awarded, version: 1,
});

const coveredByAward: SourcingWorkbench = {
  rfqId: 5,
  rfqNumber: 'RFQ-5',
  lines: [{
    id: 10, rfqId: 5, description: 'Gate valve 2" CL150', requestedQuantity: 12,
    availableQuantity: 0, reservedQuantity: 0, shortfallQuantity: 0, resolution: 'COVERED',
  }],
  solicitations: [],
  offers: [offer(3, 'Winning Supplier', true), offer(4, 'Runner-up Supplier', false)],
  awards: [{
    id: 50, rfqItemId: 10, supplierQuotedItemId: 3, supplierName: 'Winning Supplier', supplierId: 3, quantity: 12,
    landedUnitCost: 155.9, currencyCode: 'SAR', currencyId: 1, status: 'APPROVED', version: 1,
  }],
  purchaseOrders: [],
  customerQuoteDraft: {
    quoteId: 77, quoteNumber: 'QT-0926-0001',
    lines: [{ quoteItemId: 501, rfqItemId: 10, quantity: 12, unitPrice: 0, totalAmount: 0 }],
  },
};

const blockedLine = (supplierQuotedItemId: number) => ({
  supplierQuotedItemId, supplierId: supplierQuotedItemId, quantity: 12, availableQuantity: 12, unitPrice: 155.9,
  landedUnitCost: 155.9, currencyId: 1, leadTimeDays: 14, reliability: null,
  blockers: [ALREADY_COVERED], eligible: false, weightedScore: null, scoreBreakdown: null,
  scoreUnavailableReason: 'Not scored — this offer cannot be awarded as it stands',
});

const comparison: QuoteComparisonResult = {
  rfqItemId: 10,
  lines: [blockedLine(3), blockedLine(4)],
  recommendedSupplierQuotedItemId: null,
};

const renderPage = () => render(
  <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
    <MemoryRouter><SourcingWorkbenchPage /></MemoryRouter>
  </QueryClientProvider>,
);

const openOffers = async () => {
  renderPage();
  fireEvent.click(await screen.findByRole('tab', { name: /Supplier offers/ }));
};

beforeEach(() => {
  vi.clearAllMocks();
  procurementService.getWorkbench.mockResolvedValue(coveredByAward);
  procurementService.getQuoteComparison.mockResolvedValue(comparison);
});

describe('the offer that was just awarded', () => {
  it('reads as awarded — quantity, landed cost — and is never told it cannot be awarded', async () => {
    await openOffers();
    const row = (await screen.findByText('Winning Supplier')).closest('tr')!;

    expect(row).toHaveTextContent('Awarded · 12 of 12 · SAR 155.90 landed');
    expect(row).not.toHaveTextContent(ALREADY_COVERED);
    expect(row).not.toHaveTextContent(/cannot be awarded/i);
    expect(within(row).getByRole('button', { name: /price customer quote/i })).toBeEnabled();
    // No Approve control competes with the decision already made.
    expect(within(row).queryByRole('button', { name: /^(approve|covered|award more)$/i })).not.toBeInTheDocument();
  });

  it('is not counted under "Needs attention", while the runner-up still is', async () => {
    await openOffers();
    const runnerUp = (await screen.findByText('Runner-up Supplier')).closest('tr')!;
    // For the other offer the blocker is TRUE: the line is covered, by someone else.
    expect(runnerUp).toHaveTextContent(ALREADY_COVERED);

    // One offer needs attention, not two.
    expect(await screen.findByText(/1 supplier offer cannot be awarded/)).toBeInTheDocument();
    expect(screen.queryByText(/2 supplier offers cannot be awarded/)).not.toBeInTheDocument();
  });

  it('raises no "Needs attention" at all when it is the only offer on the line', async () => {
    procurementService.getWorkbench.mockResolvedValue({ ...coveredByAward, offers: [coveredByAward.offers[0]] });
    procurementService.getQuoteComparison.mockResolvedValue({ ...comparison, lines: [blockedLine(3)] });
    await openOffers();
    await screen.findByText('Winning Supplier');

    expect(screen.queryByText(/Needs attention/)).not.toBeInTheDocument();
  });
});
