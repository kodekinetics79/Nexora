import { beforeEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';

/**
 * The next-step panel said "Select suppliers for outreach" (the server's phrase) and did not change
 * when a supplier was ticked; only the button did ("Ask 1 supplier"). The screen drives the user,
 * so the sentence is derived from the tick state the page already holds (found driving the
 * journey on 2026-09-15).
 */

const getSourcingCase = vi.fn();

vi.mock('react-router-dom', async (importOriginal) => {
  const actual = await importOriginal<typeof import('react-router-dom')>();
  return { ...actual, useParams: () => ({ caseId: '3' }), useNavigate: () => vi.fn() };
});
vi.mock('../../../api/services/procurementService', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../../../api/services/procurementService')>();
  return { ...actual, default: { ...actual.default, getSourcingCase: () => getSourcingCase() } };
});
vi.mock('../../../context/AuthContext', () => ({
  useAuth: () => ({ userData: { businessUnitId: 7, userName: 'Rana' }, hasPermission: () => true }),
}));

const SourcingCasePage = (await import('./SourcingCasePage')).default;

const ready = (id: number, supplierName: string) => ({
  id, supplierId: id, supplierName, contactEmail: `sales@${id}.example`, rank: id, evidenceType: 'SUPPLIER_METADATA',
  recommendationReason: 'Tags name LV431831', evidenceScore: 0.6, evidenceFreshOn: null, selected: false,
  governanceStatus: 'APPROVED', readinessStatus: 'READY', eligibleForSupplierRfq: true, blockingReasons: [],
});

const renderPage = () => render(
  <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
    <MemoryRouter initialEntries={['/procurement/sourcing-cases/3']}><SourcingCasePage /></MemoryRouter>
  </QueryClientProvider>,
);

beforeEach(() => {
  vi.clearAllMocks();
  getSourcingCase.mockResolvedValue({
    id: 3, commercialDemandLineId: 1, rfqId: 5, rfqItemId: 10, nexoraSerial: 'NX-1', requestedPartNumber: 'LV431831',
    description: 'Circuit breaker', requestedQuantity: 5, stockQuantity: 0, unfulfilledQuantity: 5,
    searchLimit: 10, status: 'CANDIDATES_READY', nextAction: 'Select suppliers for outreach', version: 1,
    candidates: [ready(1, 'Gulf Switchgear Trading Co.'), ready(2, 'Riyadh Breakers LLC')],
  });
});

describe('SourcingCasePage — the next-step sentence follows the ticks', () => {
  it('asks for ticks, then counts them and names the button', async () => {
    renderPage();
    const panel = await screen.findByTestId('sourcing-case-next-step');
    expect(panel).toHaveTextContent('Tick the suppliers you want to ask; each gets its own RFQ.');
    expect(panel).not.toHaveTextContent(/select suppliers for outreach/i);

    fireEvent.click(screen.getByRole('checkbox', { name: /select gulf switchgear/i }));
    expect(panel).toHaveTextContent('1 supplier ticked. Press Ask 1 supplier; each gets its own RFQ.');
    expect(screen.getByRole('button', { name: /^ask 1 supplier$/i })).toBeEnabled();

    fireEvent.click(screen.getByRole('checkbox', { name: /select riyadh breakers/i }));
    expect(panel).toHaveTextContent('2 suppliers ticked. Press Ask 2 suppliers; each gets its own RFQ.');

    fireEvent.click(screen.getByRole('checkbox', { name: /select gulf switchgear/i }));
    fireEvent.click(screen.getByRole('checkbox', { name: /select riyadh breakers/i }));
    expect(panel).toHaveTextContent('Tick the suppliers you want to ask; each gets its own RFQ.');
  });
});
