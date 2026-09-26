import { beforeEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, within } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';

/**
 * A sourcing case whose candidate list is empty used to say "Review discovery options" with no control
 * beside it, and "No tenant Supplier history matched this demand line": a dead end in system words. The
 * rep's next move is to add the supplier, so the panel says why nothing matched and offers that step,
 * carrying the part into the supplier form and bringing the rep back.
 */

const auth = { grants: null as Set<string> | null };
const getSourcingCase = vi.fn();
const navigate = vi.fn();

vi.mock('react-router-dom', async (importOriginal) => {
  const actual = await importOriginal<typeof import('react-router-dom')>();
  return { ...actual, useParams: () => ({ caseId: '3' }), useNavigate: () => navigate };
});
vi.mock('../../../api/services/procurementService', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../../../api/services/procurementService')>();
  return { ...actual, default: { ...actual.default, getSourcingCase: () => getSourcingCase() } };
});
vi.mock('../../../context/AuthContext', () => ({
  useAuth: () => ({
    userData: { businessUnitId: 7 },
    hasPermission: (module: string, action?: string) =>
      auth.grants === null || auth.grants.has(`${module}:${action ?? 'view'}`),
  }),
}));

const SourcingCasePage = (await import('./SourcingCasePage')).default;

const candidate = (over: Record<string, unknown> = {}) => ({
  id: 1, supplierId: 12, supplierName: 'Arabian Turbine Parts', contactEmail: 'q@example.com', rank: 1,
  evidenceType: 'SUPPLIER_METADATA', recommendationReason: 'Maker named in Tags', evidenceScore: 50,
  selected: false, governanceStatus: 'UNVERIFIED', readinessStatus: 'REVIEW_REQUIRED',
  eligibleForSupplierRfq: false, blockingReasons: ['Supplier approval is required'], ...over,
});
const sourcingCase = (candidates: unknown[], status = 'DISCOVERY_REQUIRED') => ({
  id: 3, commercialDemandLineId: 1, rfqId: 5, rfqItemId: 10, nexoraSerial: 'NX-1', requestedPartNumber: '104T2299G0002',
  description: 'SEGMENT,COMPRESSOR ROTOR', requestedQuantity: 1, stockQuantity: 0, unfulfilledQuantity: 1,
  searchLimit: 10, status, nextAction: 'Review discovery options', version: 1, candidates,
});

const renderPage = () => render(
  <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
    <MemoryRouter><SourcingCasePage /></MemoryRouter>
  </QueryClientProvider>,
);

beforeEach(() => {
  vi.clearAllMocks();
  auth.grants = null;
});

describe('SourcingCasePage — no known supplier is a next step, not a dead end', () => {
  it('says why nothing matched and opens the supplier form with the part and the way back', async () => {
    getSourcingCase.mockResolvedValue(sourcingCase([]));
    renderPage();

    const panel = await screen.findByTestId('sourcing-case-next-step');
    expect(within(panel).getByText(/No supplier on your list is linked to 104T2299G0002 yet/)).toBeInTheDocument();
    expect(within(panel).queryByText('Review discovery options')).not.toBeInTheDocument();

    fireEvent.click(within(panel).getByRole('button', { name: 'Add a supplier' }));
    expect(navigate).toHaveBeenCalledWith('/suppliers?new=1&tags=104T2299G0002&returnTo=%2Fprocurement%2Fsourcing-cases%2F3');
    expect(screen.getAllByRole('button', { name: 'Add a supplier' })).toHaveLength(1);
  });

  it('names who to ask when the rep cannot add suppliers', async () => {
    auth.grants = new Set(['RFQ Management:edit', 'Supplier History:create', 'Supplier History:view', 'Supplier History:edit']);
    getSourcingCase.mockResolvedValue(sourcingCase([]));
    renderPage();

    const panel = await screen.findByTestId('sourcing-case-next-step');
    expect(within(panel).getByText(/Ask someone who can add suppliers/)).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Add a supplier' })).not.toBeInTheDocument();
  });

  it('says a manager must approve when every candidate is blocked, and links each supplier to its page', async () => {
    getSourcingCase.mockResolvedValue(sourcingCase([candidate()]));
    renderPage();

    const panel = await screen.findByTestId('sourcing-case-next-step');
    expect(within(panel).getByText(/None of these suppliers can be asked yet/)).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: 'Arabian Turbine Parts' }));
    expect(navigate).toHaveBeenCalledWith('/suppliers/12');
  });

  it('speaks the buyer\'s words on an empty case: no system status, no zero counts, no filled button that cannot be pressed', async () => {
    getSourcingCase.mockResolvedValue(sourcingCase([]));
    renderPage();

    await screen.findByTestId('sourcing-case-next-step');
    expect(screen.getByRole('heading', { name: 'Ask suppliers' })).toBeInTheDocument();
    expect(screen.getByText('No supplier yet')).toBeInTheDocument();
    expect(screen.queryByText('Discovery Required')).not.toBeInTheDocument();
    expect(screen.queryByText(/can be asked ·/)).not.toBeInTheDocument();
    const ask = screen.getByRole('button', { name: 'Ask the ticked suppliers' });
    expect(ask).toBeDisabled();
    expect(ask.className).toContain('MuiButton-outlined');
  });

  it('shows every reason a blocked supplier cannot be asked, in words, with nothing hidden behind "N more"', async () => {
    // 2026-09-15: "Supplier approval is required · 3 more" sent the manager to a second screen to
    // find out what the three were. Every blocker is now on the row, in the rep's words.
    getSourcingCase.mockResolvedValue(sourcingCase([candidate({ blockingReasons: ['Supplier approval or explicit provisional approval is required', 'Supplier verification status must be VERIFIED'] })]));
    renderPage();

    expect(await screen.findByText(/Not approved/)).toBeInTheDocument();
    expect(screen.getByText(/Not verified/)).toBeInTheDocument();
    expect(screen.queryByText(/· 1 more/)).not.toBeInTheDocument();
    expect(screen.getByText('Tags name this part or its maker')).toBeInTheDocument();
    expect(screen.queryByText('Persisted tenant relationship')).not.toBeInTheDocument();
  });

  it('counts the ticked suppliers on the button and fills it only once one is ticked', async () => {
    getSourcingCase.mockResolvedValue({ ...sourcingCase([candidate({ eligibleForSupplierRfq: true, blockingReasons: [] })], 'CANDIDATES_READY'), nextAction: 'Select suppliers for outreach' });
    renderPage();

    fireEvent.click(await screen.findByRole('checkbox', { name: 'Select Arabian Turbine Parts' }));
    const ask = screen.getByRole('button', { name: 'Ask 1 supplier' });
    expect(ask).toBeEnabled();
    expect(ask.className).toContain('MuiButton-contained');
    expect(screen.getByText('1 of 1 can be asked · 1 ticked')).toBeInTheDocument();
  });

  it('tells the rep to tick once a supplier can be asked, instead of the server\'s "Select suppliers for outreach"', async () => {
    // The sentence follows the ticks (D13, 2026-09-15): none ticked → tell them to tick.
    getSourcingCase.mockResolvedValue({ ...sourcingCase([candidate({ eligibleForSupplierRfq: true, blockingReasons: [] })], 'CANDIDATES_READY'), nextAction: 'Select suppliers for outreach' });
    renderPage();

    const panel = await screen.findByTestId('sourcing-case-next-step');
    expect(within(panel).getByText(/Tick the suppliers you want to ask/)).toBeInTheDocument();
    expect(within(panel).queryByText('Select suppliers for outreach')).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Add a supplier' })).not.toBeInTheDocument();
  });
});
