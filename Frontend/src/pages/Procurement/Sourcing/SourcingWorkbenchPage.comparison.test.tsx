/**
 * The supplier comparison table, seen the way a sales engineer sees it.
 *
 * Two things are proved here because both were invisible on screen while being correct underneath:
 * the ranking is actually applied to the rows, so changing the weights moves the winner to the
 * top; and an offer that cannot be awarded is never handed the sentence that says it can be.
 */
import { describe, expect, it, vi, beforeEach } from 'vitest';
import { fireEvent, render, screen, within } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router-dom';
import type {
  QuoteComparisonResult,
  SourcingWorkbench,
  SupplierOffer,
} from '../../../api/services/procurementService';

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
vi.mock('../../../api/services/currencyService', () => ({
  default: { getAll: vi.fn().mockResolvedValue({ items: [] }) },
}));
vi.mock('../../../api/services/warehouseService', () => ({
  default: { getAll: vi.fn().mockResolvedValue({ items: [] }) },
}));
vi.mock('../../../context/AuthContext', () => ({
  useAuth: () => ({ userData: { businessUnitId: 7 }, hasPermission: () => true }),
}));

const procurementService = (await import('../../../api/services/procurementService'))
  .default as unknown as {
    getWorkbench: ReturnType<typeof vi.fn>;
    getQuoteComparison: ReturnType<typeof vi.fn>;
  };
const SourcingWorkbenchPage = (await import('./SourcingWorkbenchPage')).default;

const offer = (id: number, supplierName: string): SupplierOffer => ({
  id,
  solicitationId: 1,
  rfqItemId: 10,
  supplierId: id,
  supplierName,
  quoteReference: `REF-${id}`,
  quoteRevision: 1,
  currencyId: 1,
  currencyCode: 'SAR',
  quantity: 5,
  availableQuantity: 5,
  unitPrice: 1000,
  freightCost: 0,
  dutyCost: 0,
  otherCost: 0,
  landedUnitCost: 1000,
  leadTimeDays: 14,
  reliabilitySnapshot: null,
  validUntil: null,
  eligible: true,
  blockingReasons: [],
  awarded: false,
  version: 1,
});

const workbench: SourcingWorkbench = {
  rfqId: 5,
  rfqNumber: 'RFQ-5',
  lines: [{
    id: 10, rfqId: 5, description: 'Pressure transmitter', requestedQuantity: 5,
    availableQuantity: 0, reservedQuantity: 0, shortfallQuantity: 5, resolution: 'SHORTAGE',
  }],
  solicitations: [],
  // Deliberately the order the API returns: the winner is last and the blocked offer is first.
  offers: [offer(1, 'Blocked Supplier'), offer(2, 'Middle Supplier'), offer(3, 'Winning Supplier')],
  awards: [],
  purchaseOrders: [],
  customerQuoteDraft: null,
};

const line = (
  supplierQuotedItemId: number,
  weightedScore: number | null,
  eligible = true,
  blockers: string[] = [],
) => ({
  supplierQuotedItemId,
  supplierId: supplierQuotedItemId,
  quantity: 5,
  availableQuantity: 5,
  unitPrice: 1000,
  landedUnitCost: 1000,
  currencyId: 1,
  leadTimeDays: 14,
  reliability: null,
  blockers,
  eligible,
  weightedScore,
  scoreBreakdown: weightedScore == null ? null : [
    { criterion: 'PRICE', label: 'Landed cost', rawValue: 1000, weight: 80, pointsEarned: weightedScore },
  ],
  scoreUnavailableReason: weightedScore == null
    ? (eligible ? 'Cannot score — lead time missing' : 'Not scored — this offer cannot be awarded as it stands')
    : null,
});

const comparison = (weights: 'price' | 'speed'): QuoteComparisonResult => ({
  rfqItemId: 10,
  lines: [
    line(1, null, false, ['supplier registration expired']),
    line(2, weights === 'price' ? 60 : 90),
    line(3, weights === 'price' ? 90 : 60),
  ],
  recommendedSupplierQuotedItemId: weights === 'price' ? 3 : 2,
});

const renderPage = () =>
  render(
    <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
      <MemoryRouter>
        <SourcingWorkbenchPage />
      </MemoryRouter>
    </QueryClientProvider>,
  );

/** Opens the comparison and returns the supplier names in the order they are rendered. */
const supplierOrder = async () => {
  renderPage();
  fireEvent.click(await screen.findByRole('tab', { name: /Supplier offers/ }));
  const rows = await screen.findAllByText(/Supplier$/);
  return rows.map((cell) => cell.textContent);
};

beforeEach(() => {
  vi.clearAllMocks();
  procurementService.getWorkbench.mockResolvedValue(workbench);
});

describe('the supplier comparison table', () => {
  it('renders the best-scoring offer first, not the order the API happened to return', async () => {
    procurementService.getQuoteComparison.mockResolvedValue(comparison('price'));
    expect(await supplierOrder()).toEqual([
      'Winning Supplier', 'Middle Supplier', 'Blocked Supplier',
    ]);
  });

  it('moves the winner to the top when the weights change the score', async () => {
    // Same three offers, same API order, a different weight set. If the table ignored the ranking
    // the buyer would see identical rows in identical places and conclude the setting does nothing.
    procurementService.getQuoteComparison.mockResolvedValue(comparison('speed'));
    expect(await supplierOrder()).toEqual([
      'Middle Supplier', 'Winning Supplier', 'Blocked Supplier',
    ]);
  });

  it('never tells a blocked offer it can still be awarded', async () => {
    procurementService.getQuoteComparison.mockResolvedValue(comparison('price'));
    renderPage();
    fireEvent.click(await screen.findByRole('tab', { name: /Supplier offers/ }));
    const blockedRow = (await screen.findByText('Blocked Supplier')).closest('tr')!;
    expect(within(blockedRow).queryByText(/can still be awarded/i)).not.toBeInTheDocument();
    expect(blockedRow).toHaveTextContent('supplier registration expired');
    expect(blockedRow).toHaveTextContent('cannot be awarded as it stands');
  });

  it('keeps an awardable offer with a missing value visibly awardable', async () => {
    procurementService.getQuoteComparison.mockResolvedValue({
      rfqItemId: 10,
      lines: [line(1, null, false, ['supplier registration expired']), line(2, null), line(3, 90)],
      recommendedSupplierQuotedItemId: 3,
    });
    renderPage();
    fireEvent.click(await screen.findByRole('tab', { name: /Supplier offers/ }));
    const unscoredRow = (await screen.findByText('Middle Supplier')).closest('tr')!;
    // R-F: the value that is missing is named, no zero is invented, and the offer keeps standing.
    expect(unscoredRow).toHaveTextContent('Cannot score — lead time missing');
    expect(within(unscoredRow).getByText(/can still be awarded/i)).toBeInTheDocument();
    expect(within(unscoredRow).getByRole('button', { name: 'Approve' })).toBeEnabled();
  });
});
