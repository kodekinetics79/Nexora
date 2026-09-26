import { beforeEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { QuoteComparisonResult, SourcingWorkbench, SupplierOffer } from '../../../api/services/procurementService';

/**
 * D16 — the only decision on the Supplier offers table was the 14th column, off-screen at 1600px
 * with no scroll cue, and the panel's contained "Compare supplier offers" merely switched tabs.
 *
 * The Decision column is pinned to the right edge of the scrolling table, and the panel button
 * lands the buyer on the recommended row's Approve control with focus on it.
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

const offer = (id: number, supplierName: string): SupplierOffer => ({
  id, solicitationId: 1, rfqItemId: 10, supplierId: id, supplierName, quoteReference: `REF-${id}`, quoteRevision: 1,
  currencyId: 1, currencyCode: 'SAR', quantity: 5, availableQuantity: 5, unitPrice: 1000, freightCost: 0, dutyCost: 0,
  otherCost: 0, landedUnitCost: 1000, leadTimeDays: 14, reliabilitySnapshot: null, validUntil: null,
  eligible: true, blockingReasons: [], awarded: false, version: 1,
});

const offersIn: SourcingWorkbench = {
  rfqId: 5,
  rfqNumber: 'RFQ-5',
  lines: [{ id: 10, rfqId: 5, description: 'Pressure transmitter', requestedQuantity: 5, availableQuantity: 0, reservedQuantity: 0, shortfallQuantity: 5, resolution: 'SHORTAGE' }],
  solicitations: [],
  offers: [offer(2, 'Middle Supplier'), offer(3, 'Winning Supplier')],
  awards: [],
  purchaseOrders: [],
  customerQuoteDraft: null,
};

const scored = (supplierQuotedItemId: number, weightedScore: number) => ({
  supplierQuotedItemId, supplierId: supplierQuotedItemId, quantity: 5, availableQuantity: 5, unitPrice: 1000,
  landedUnitCost: 1000, currencyId: 1, leadTimeDays: 14, reliability: null, blockers: [], eligible: true, weightedScore,
  scoreBreakdown: [{ criterion: 'PRICE', label: 'Landed cost', rawValue: 1000, weight: 80, pointsEarned: weightedScore }],
  scoreUnavailableReason: null,
});

const comparison: QuoteComparisonResult = {
  rfqItemId: 10,
  lines: [scored(2, 60), scored(3, 90)],
  recommendedSupplierQuotedItemId: 3,
};

const renderPage = () => render(
  <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
    <MemoryRouter><SourcingWorkbenchPage /></MemoryRouter>
  </QueryClientProvider>,
);

/** Every CSS rule the page has injected, so a style can be asserted rather than a class name. */
const injectedRules = () =>
  Array.from(document.styleSheets).flatMap((sheet) => {
    try { return Array.from(sheet.cssRules).map((rule) => rule.cssText); } catch { return []; }
  });

beforeEach(() => {
  vi.clearAllMocks();
  procurementService.getWorkbench.mockResolvedValue(offersIn);
  procurementService.getQuoteComparison.mockResolvedValue(comparison);
});

describe('the Decision column', () => {
  it('is pinned to the right edge of the scrolling table, on the header and on every row', async () => {
    renderPage();
    fireEvent.click(await screen.findByRole('tab', { name: /Supplier offers/ }));
    const header = await screen.findByRole('columnheader', { name: 'Decision' });
    expect(header).toHaveAttribute('data-pinned', 'right');

    const winner = (await screen.findByText('Winning Supplier')).closest('tr')!;
    const decisionCell = within(winner).getByRole('button', { name: 'Approve' }).closest('td')!;
    expect(decisionCell).toHaveAttribute('data-pinned', 'right');

    const pinnedRule = injectedRules().find((rule) => rule.includes("[data-pinned='right']") || rule.includes('[data-pinned="right"]'));
    expect(pinnedRule, 'no stylesheet rule pins the decision cell').toBeDefined();
    expect(pinnedRule).toMatch(/position:\s*sticky/);
    expect(pinnedRule).toMatch(/right:\s*0(px)?/);
  });
});

describe('"Compare supplier offers" on the next-step panel', () => {
  it('lands on the recommended row with its Approve control focused', async () => {
    renderPage();
    const panel = await screen.findByTestId('sourcing-next-step');
    fireEvent.click(within(panel).getByRole('button', { name: 'Compare supplier offers' }));

    const winner = (await screen.findByText('Winning Supplier')).closest('tr')!;
    const approve = within(winner).getByRole('button', { name: 'Approve' });
    await waitFor(() => expect(document.activeElement).toBe(approve));
  });
});
