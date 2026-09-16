import { beforeEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';

/**
 * After adding a supplier, the case row read "Needs approval — Supplier approval or explicit
 * provisional approval is required · 3 more" and the panel sent the manager off to "open each
 * one and have a manager approve it, then press Refresh candidates" (found driving the journey on
 * 2026-09-15). A manager standing on the case now approves the supplier for RFQs from the row —
 * the same governance write the supplier page makes, with an audit reason naming this screen — and
 * the list refreshes by itself. Every blocker is listed in words; nothing hides behind "3 more".
 */

const auth = { isManager: true, grants: null as Set<string> | null };
const getSourcingCase = vi.fn();
const refreshSourcingCaseCandidates = vi.fn();
const getById = vi.fn();
const govern = vi.fn();

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
vi.mock('../../../api/services/supplierService', () => ({
  default: { getById: (...a: unknown[]) => getById(...a), govern: (...a: unknown[]) => govern(...a) },
}));
vi.mock('../../../context/AuthContext', () => ({
  useAuth: () => ({
    userData: { businessUnitId: 7, userName: 'Rana', isManager: auth.isManager },
    hasPermission: (module: string, action?: string) =>
      auth.grants === null || auth.grants.has(`${module}:${action ?? 'view'}`),
  }),
}));

const SourcingCasePage = (await import('./SourcingCasePage')).default;

const GOVERNANCE_BLOCKERS = [
  'Supplier approval or explicit provisional approval is required',
  'Supplier verification status must be VERIFIED',
  'Supplier outreach readiness must be READY',
  'Supplier compliance status must be CLEARED',
];
const blocked = {
  id: 1, supplierId: 9, supplierName: 'Gulf Switchgear Trading Co.', contactEmail: 'sales@gulfswitchgear.example',
  rank: 1, evidenceType: 'SUPPLIER_METADATA', recommendationReason: 'Tags name LV431831', evidenceScore: 0.6,
  evidenceFreshOn: null, selected: false, governanceStatus: 'UNVERIFIED', readinessStatus: 'REVIEW_REQUIRED',
  eligibleForSupplierRfq: false, blockingReasons: GOVERNANCE_BLOCKERS,
};
const approved = {
  ...blocked, governanceStatus: 'APPROVED', readinessStatus: 'READY', eligibleForSupplierRfq: true, blockingReasons: [],
};
const sourcingCase = (candidates: unknown[]) => ({
  id: 3, commercialDemandLineId: 1, rfqId: 5, rfqItemId: 10, nexoraSerial: 'NX-1', requestedPartNumber: 'LV431831',
  description: 'Circuit breaker', requestedQuantity: 5, stockQuantity: 0, unfulfilledQuantity: 5,
  searchLimit: 10, status: 'CANDIDATES_READY', nextAction: 'Select suppliers for outreach', version: 1, candidates,
});
const supplierRecord = {
  id: 9, name: 'Gulf Switchgear Trading Co.', contactEmail: 'sales@gulfswitchgear.example', isActive: true,
  governanceStatus: 'UNVERIFIED', verificationStatus: 'UNKNOWN', complianceStatus: 'UNKNOWN',
  riskStatus: 'UNKNOWN', readinessStatus: 'REVIEW_REQUIRED', concurrencyToken: 'tok-1',
};

const renderPage = () => render(
  <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
    <MemoryRouter initialEntries={['/procurement/sourcing-cases/3']}><SourcingCasePage /></MemoryRouter>
  </QueryClientProvider>,
);

const rowOf = async (name: string) => (await screen.findByText(name)).closest('tr') as HTMLElement;

beforeEach(() => {
  vi.clearAllMocks();
  auth.isManager = true;
  auth.grants = null;
  getSourcingCase.mockResolvedValue(sourcingCase([blocked]));
  getById.mockResolvedValue(supplierRecord);
  govern.mockResolvedValue({ ...supplierRecord, governanceStatus: 'APPROVED', readinessStatus: 'READY' });
  refreshSourcingCaseCandidates.mockResolvedValue({
    sourcingCaseId: 3, requestedLimit: 10, resultCount: 1, version: 2, replayed: false, candidates: [approved],
  });
});

describe('SourcingCasePage — a manager approves a supplier for RFQs from the row', () => {
  it('lists every blocker in words instead of "3 more"', async () => {
    renderPage();
    const row = await rowOf('Gulf Switchgear Trading Co.');

    expect(within(row).getByText('Not approved')).toBeInTheDocument();
    expect(within(row).getByText('Not verified')).toBeInTheDocument();
    expect(within(row).getByText('Not marked ready for RFQs')).toBeInTheDocument();
    expect(within(row).getByText('Compliance not cleared')).toBeInTheDocument();
    expect(within(row).queryByText(/more$/)).not.toBeInTheDocument();
  });

  it('records the working combination with a reason naming this screen, then refreshes the list', async () => {
    renderPage();
    const row = await rowOf('Gulf Switchgear Trading Co.');
    expect(screen.getByTestId('sourcing-case-next-step')).toHaveTextContent(/press approve for rfqs beside a supplier you trust/i);

    fireEvent.click(within(row).getByRole('button', { name: /approve for rfqs/i }));

    await waitFor(() => expect(govern).toHaveBeenCalledTimes(1));
    expect(getById).toHaveBeenCalledWith(9);
    expect(govern).toHaveBeenCalledWith(9, {
      governanceStatus: 'APPROVED', verificationStatus: 'VERIFIED', complianceStatus: 'CLEARED',
      riskStatus: 'LOW', readinessStatus: 'READY',
      expectedConcurrencyToken: 'tok-1', reason: 'Approved for RFQs by Rana from the sourcing case',
    });
    await waitFor(() => expect(refreshSourcingCaseCandidates).toHaveBeenCalledWith(3, 10, 1));
    await waitFor(() => expect(screen.getByRole('checkbox', { name: /select gulf switchgear/i })).toBeEnabled());
    expect(screen.queryByRole('button', { name: /approve for rfqs/i })).not.toBeInTheDocument();
  });

  it('offers nothing to a rep who is not a manager, and says who approves', async () => {
    auth.isManager = false;
    renderPage();
    const row = await rowOf('Gulf Switchgear Trading Co.');

    expect(within(row).queryByRole('button', { name: /approve for rfqs/i })).not.toBeInTheDocument();
    expect(screen.getByTestId('sourcing-case-next-step')).toHaveTextContent(/a manager approves them for rfqs on the supplier page/i);
  });

  it('does not offer approval when something the preset cannot fix is also in the way', async () => {
    getSourcingCase.mockResolvedValue(sourcingCase([{
      ...blocked, contactEmail: null, blockingReasons: ['A verified dispatch contact is required', ...GOVERNANCE_BLOCKERS],
    }]));
    renderPage();
    const row = await rowOf('Gulf Switchgear Trading Co.');

    expect(within(row).queryByRole('button', { name: /approve for rfqs/i })).not.toBeInTheDocument();
    expect(within(row).getAllByRole('listitem').map((item) => item.textContent)).toContain('No contact email');
    expect(within(row).getByText('Needs a contact email')).toBeInTheDocument();
  });
});
