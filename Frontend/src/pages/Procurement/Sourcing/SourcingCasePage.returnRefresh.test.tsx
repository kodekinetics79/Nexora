import { beforeEach, describe, expect, it, vi } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';

/**
 * Coming back from "Add a supplier" (the suppliers page appends ?refresh=1 to the return address),
 * the case used to still read "No supplier on your list is linked to LV431831 yet … press Refresh
 * candidates" until the rep pressed Refresh — the very click the handoff was meant to save (found
 * driving the journey on 2026-09-15). The case now runs the search itself on that return.
 */

const getSourcingCase = vi.fn();
const refreshSourcingCaseCandidates = vi.fn();

vi.mock('react-router-dom', async (importOriginal) => {
  const actual = await importOriginal<typeof import('react-router-dom')>();
  return { ...actual, useParams: () => ({ caseId: '3' }), useNavigate: () => vi.fn() };
});
vi.mock('../../../api/services/procurementService', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../../../api/services/procurementService')>();
  return {
    ...actual,
    default: {
      ...actual.default,
      getSourcingCase: () => getSourcingCase(),
      refreshSourcingCaseCandidates: (...args: unknown[]) => refreshSourcingCaseCandidates(...args),
    },
  };
});
vi.mock('../../../context/AuthContext', () => ({
  useAuth: () => ({ userData: { businessUnitId: 7, userName: 'Rana' }, hasPermission: () => true }),
}));

const SourcingCasePage = (await import('./SourcingCasePage')).default;

const emptyCase = {
  id: 3, commercialDemandLineId: 1, rfqId: 5, rfqItemId: 10, nexoraSerial: 'NX-1', requestedPartNumber: 'LV431831',
  description: 'Circuit breaker', requestedQuantity: 5, stockQuantity: 0, unfulfilledQuantity: 5,
  searchLimit: 10, status: 'DISCOVERY_REQUIRED', nextAction: 'Review discovery options', version: 1, candidates: [],
};
const newCandidate = {
  id: 1, supplierId: 9, supplierName: 'Gulf Switchgear Trading Co.', contactEmail: 'sales@gulfswitchgear.example',
  rank: 1, evidenceType: 'SUPPLIER_METADATA', recommendationReason: 'Tags name LV431831', evidenceScore: 0.6,
  evidenceFreshOn: null, selected: false, governanceStatus: 'UNVERIFIED', readinessStatus: 'REVIEW_REQUIRED',
  eligibleForSupplierRfq: false, blockingReasons: ['Supplier approval or explicit provisional approval is required'],
};

const renderAt = (url: string) => render(
  <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
    <MemoryRouter initialEntries={[url]}><SourcingCasePage /></MemoryRouter>
  </QueryClientProvider>,
);

beforeEach(() => {
  vi.clearAllMocks();
  getSourcingCase.mockResolvedValue(emptyCase);
  refreshSourcingCaseCandidates.mockResolvedValue({
    sourcingCaseId: 3, requestedLimit: 10, resultCount: 1, version: 2, replayed: false, candidates: [newCandidate],
  });
});

describe('SourcingCasePage — returning from Add a supplier', () => {
  it('refreshes the candidates by itself and drops the empty-list sentence once the supplier is listed', async () => {
    renderAt('/procurement/sourcing-cases/3?refresh=1');

    expect(await screen.findByText('Gulf Switchgear Trading Co.')).toBeInTheDocument();
    expect(refreshSourcingCaseCandidates).toHaveBeenCalledTimes(1);
    expect(refreshSourcingCaseCandidates).toHaveBeenCalledWith(3, 10, 1);
    expect(screen.queryByText(/no supplier on your list is linked to/i)).not.toBeInTheDocument();
    expect(screen.queryByText(/no supplier is linked to this part yet/i)).not.toBeInTheDocument();
  });

  it('does not search on an ordinary open (the control)', async () => {
    renderAt('/procurement/sourcing-cases/3');

    expect(await screen.findByText(/no supplier is linked to this part yet/i)).toBeInTheDocument();
    await waitFor(() => expect(screen.getByRole('button', { name: /refresh candidates/i })).toBeEnabled());
    expect(refreshSourcingCaseCandidates).not.toHaveBeenCalled();
  });
});
