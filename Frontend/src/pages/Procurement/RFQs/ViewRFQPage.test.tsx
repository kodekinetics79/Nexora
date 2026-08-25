import { beforeEach, describe, expect, it, vi } from 'vitest';
import { render, screen, within } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { ReactNode } from 'react';
import ViewRFQPage from './ViewRFQPage';
import type { RfqResponseDTO, RfqitemResponseDTO } from '../../../api/services/rfqService';
import type { RfqCommercialIntelligence } from '../../../api/services/commercialLearningService';
import type { SourcingWorkbench } from '../../../api/services/procurementService';

const getRfq = vi.fn();
const getWorkbench = vi.fn();
const getRfqIntelligence = vi.fn();
const getRfqLineResolutions = vi.fn();

vi.mock('react-router-dom', async (importOriginal) => {
  const actual = await importOriginal<typeof import('react-router-dom')>();
  return { ...actual, useNavigate: () => vi.fn(), useParams: () => ({ id: '9001' }) };
});
vi.mock('../../../api/services/rfqService', () => ({
  default: {
    getById: (...args: unknown[]) => getRfq(...args),
    approve: vi.fn(),
    prepareQuoteDraft: vi.fn(),
  },
}));
vi.mock('../../../api/services/procurementService', () => ({
  default: { getWorkbench: (...a: unknown[]) => getWorkbench(...a), createOrOpenSourcingCase: vi.fn() },
}));
vi.mock('../../../api/services/commercialLearningService', () => ({
  default: { getRfqIntelligence: (...a: unknown[]) => getRfqIntelligence(...a) },
}));
vi.mock('../../../api/services/commercialIntelligenceService', () => ({
  default: { getRfqLineResolutions: (...a: unknown[]) => getRfqLineResolutions(...a) },
}));
vi.mock('../../../api/services/commercialLifecycleService', () => ({
  default: { getState: vi.fn().mockResolvedValue({ aggregateId: 9001, currentStatusCode: 'APPROVED', version: 1, isTerminal: false, allowedTransitions: [] }) },
}));
vi.mock('../../../context/AuthContext', () => ({
  useAuth: () => ({ hasPermission: () => true, userData: { businessUnitId: 7, userName: 'qa', id: 1 } }),
}));
vi.mock('notistack', () => ({ useSnackbar: () => ({ enqueueSnackbar: vi.fn() }) }));
// Panels with their own data of their own; not what this spec is about.
vi.mock('../../../components/common/CommercialLineIntelligence', () => ({ default: () => null }));
vi.mock('../../../components/common/CommercialProcessingEvidence', () => ({ default: () => null }));
vi.mock('../../../components/common/LifecycleActions', () => ({ default: () => null }));
vi.mock('../../../components/common/EmailPromptDialog', () => ({ default: () => null }));

const line = (id: number, over: Partial<RfqitemResponseDTO> = {}): RfqitemResponseDTO => ({
  id,
  rfqid: 9001,
  lineItemNo: `00${id}0`,
  quantity: 10,
  unitOfMeasure: 'EA',
  manufacturerPartNumber: `MPN-${id}`,
  productShortDescription: `Line ${id}`,
  bidClosingDateLine: '2026-09-01T00:00:00Z',
  createdBy: 'seed',
  createdDate: '2026-08-01T00:00:00Z',
  participationDecision: 'Pending',
  ...over,
});

/**
 * `recDate` and `createdDate` are non-nullable DateTime server-side, so an RFQ that never
 * captured one serialises as 0001-01-01 rather than null — the sentinel utils/dates.ts exists
 * to catch.
 */
const rfq = (over: Partial<RfqResponseDTO> = {}): RfqResponseDTO => ({
  id: 9001,
  rfqno: 'RFQ-9001',
  nexoraSerial: 'NX-9001',
  recDate: '2026-08-01T00:00:00Z',
  activeLeadRevision: 1,
  createdBy: 'qa',
  createdDate: '2026-08-01T00:00:00Z',
  businessUnitId: 7,
  rfqstatusValue: 'Approved',
  customerName: 'Fulton County',
  leadId: 55,
  readiness: 'Review Required',
  rfqitems: [line(1), line(2), line(3)],
  ...over,
});

const workbench = (lines: RfqitemResponseDTO[]): SourcingWorkbench => ({
  rfqId: 9001,
  lines: lines.map((item) => ({
    id: item.id,
    rfqId: 9001,
    description: item.productShortDescription ?? '',
    requestedQuantity: 10,
    availableQuantity: 10,
    reservedQuantity: 0,
    shortfallQuantity: 0,
    resolution: 'IN_STOCK' as const,
  })),
  solicitations: [],
  offers: [],
  awards: [],
  purchaseOrders: [],
});

const intelligence = (over: Partial<RfqCommercialIntelligence> = {}): RfqCommercialIntelligence => ({
  rfqId: 9001,
  rfqNumber: 'RFQ-9001',
  nexoraSerial: 'NX-9001',
  readinessScore: 62.75,
  commercialDecision: 'ACTIONABLE_WITH_BLOCKERS',
  slaRisk: 'DEADLINE_NOT_RECORDED',
  clarificationRequired: false,
  nextBestAction: {
    code: 'RECOVER_COVERAGE',
    label: 'Recover line coverage',
    explanation: 'Resolve 1 blocked line of the 1 line being quoted before preparing the customer quote.',
    confidence: 0.9,
    userOverrideAllowed: true,
    overrideAction: '/procurement/rfqs/9001/sourcing',
    evidence: [],
  },
  lines: [],
  digitalTwin: {
    calculatedOn: '2026-08-16T00:00:00Z',
    validity: 'Current',
    mode: 'SHADOW',
    policyVersion: 'digital-twin-v2.3',
    scenarios: [],
    customerTargetBridges: [],
    predictivePricing: [],
    backtest: { status: 'INSUFFICIENT', holdoutCount: 0, cohort: 'No decided cohort', limitation: 'none' },
    overrideAction: '',
  },
  ...over,
});

const wrapper = ({ children }: { children: ReactNode }) => {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return <QueryClientProvider client={client}>{children}</QueryClientProvider>;
};

beforeEach(() => {
  vi.clearAllMocks();
  getRfq.mockResolvedValue(rfq());
  getWorkbench.mockResolvedValue(workbench(rfq().rfqitems));
  getRfqIntelligence.mockResolvedValue(intelligence());
  getRfqLineResolutions.mockResolvedValue([]);
});

/**
 * The line table renders `visibleItems`, but its "nothing here" row was keyed on the RFQ's own
 * line collection. So the message appeared only for an RFQ with no lines at all — the one case
 * where "match this filter" means nothing — and a tile filter that genuinely matched nothing
 * left the operator staring at an empty table with no explanation and no way back.
 */
describe('ViewRFQPage — a filter that matches nothing says so', () => {
  it('explains an empty result and offers a way back to all lines', async () => {
    render(<ViewRFQPage />, { wrapper });

    // Nothing is ready for quote: intelligence reported no lines at all.
    const readyTile = await screen.findByText(/^Ready for quote/);
    readyTile.click();

    expect(await screen.findByText(/No line matches "Ready for quote/)).toBeInTheDocument();
    expect(screen.getByText(/3 lines on this RFQ/)).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Show all lines' })).toBeInTheDocument();
  });

  it('says the RFQ is empty rather than blaming a filter, when it truly has no lines', async () => {
    getRfq.mockResolvedValue(rfq({ rfqitems: [] }));
    getWorkbench.mockResolvedValue(workbench([]));
    render(<ViewRFQPage />, { wrapper });

    expect(await screen.findByText('This RFQ has no lines yet.')).toBeInTheDocument();
  });
});

/**
 * The buyer's own line number is the identifier they will quote back at you. Rendering the
 * position in a filtered array meant the number on screen changed when a tile was clicked.
 */
describe('ViewRFQPage — line identity', () => {
  it("shows the buyer's line number, not a position in the filtered array", async () => {
    render(<ViewRFQPage />, { wrapper });

    expect(await screen.findByText('0010')).toBeInTheDocument();
    expect(screen.getByText('0020')).toBeInTheDocument();
    expect(screen.getByText('0030')).toBeInTheDocument();
  });

  it('falls back to the position only when the document carried no line number', async () => {
    getRfq.mockResolvedValue(rfq({ rfqitems: [line(1, { lineItemNo: undefined })] }));
    render(<ViewRFQPage />, { wrapper });

    const rows = await screen.findAllByRole('row');
    expect(within(rows[rows.length - 1]).getByText('1')).toBeInTheDocument();
  });
});

describe('ViewRFQPage — immutable promotion lineage', () => {
  it('shows the receipt, Lead revision, participation version, and source-line trace supplied by the API', async () => {
    getRfq.mockResolvedValue(rfq({
      promotionId: 901,
      sourceLeadRevisionId: 5502,
      sourceLeadRevisionNumber: 2,
      participationDecisionId: 333,
      participationVersion: 4,
      promotedAtUtc: '2026-08-24T12:00:00Z',
      promotedBy: 'Bid Manager',
      rfqitems: [line(1, { sourceLeadItemRevisionId: 55101 })],
    }));
    render(<ViewRFQPage />, { wrapper });

    expect(await screen.findByText('Governed promotion receipt')).toBeInTheDocument();
    expect(screen.getByText('Revision 2')).toBeInTheDocument();
    expect(screen.getByText('Version 4')).toBeInTheDocument();
    expect(screen.getByText('#901')).toBeInTheDocument();
    expect(screen.getByText('Immutable source record #55101')).toBeInTheDocument();
    expect(screen.queryByText('Revision line #55101')).not.toBeInTheDocument();
    expect(screen.queryByText('Quote?')).not.toBeInTheDocument();
  });

  it('states that lineage is unavailable instead of inferring it for a legacy RFQ', async () => {
    render(<ViewRFQPage />, { wrapper });

    expect(await screen.findByText('Promotion receipt unavailable')).toBeInTheDocument();
    expect(screen.getByText(/immutable source lineage cannot be claimed/i)).toBeInTheDocument();
    expect(screen.getAllByText('Trace unavailable')).toHaveLength(3);
    expect(screen.getByText('Ready for quote of 3 lines')).toBeInTheDocument();
  });
});

/**
 * The screen carried three readiness statements. Two were constants: `rfq.readiness` derived
 * from an ItemCount the detail endpoint never populated, and a literal warning chip wired to
 * nothing at all. The evidence-backed score is the one that means something.
 */
describe('ViewRFQPage — one readiness statement, not three', () => {
  it('does not render a constant readiness field or a constant review chip', async () => {
    getRfq.mockResolvedValue(rfq({ readiness: 'Review Required' }));
    render(<ViewRFQPage />, { wrapper });

    await screen.findAllByText('RFQ-9001');
    expect(screen.queryByText('Review Required')).not.toBeInTheDocument();
    expect(screen.queryByText('Commercial Review Required')).not.toBeInTheDocument();
    // The evidence-backed score stays, rounded — the server sends 62.75 and this is a heuristic.
    expect(screen.getByText('63%')).toBeInTheDocument();
  });
});

/**
 * A sentinel date must never be coloured and presented as an overdue customer deadline.
 */
describe('ViewRFQPage — dates', () => {
  it('renders DateTime.MinValue as "not set", not as 01 Jan 1', async () => {
    getRfq.mockResolvedValue(rfq({
      bidClosingDate: '0001-01-01T00:00:00',
      recDate: '0001-01-01T00:00:00',
    }));
    render(<ViewRFQPage />, { wrapper });

    await screen.findAllByText('RFQ-9001');
    expect(screen.queryByText(/01 Jan 1$/)).not.toBeInTheDocument();
    expect(screen.queryByText(/Jan 0001/)).not.toBeInTheDocument();
  });
});

/**
 * A disabled primary action with no stated reason is what one transient intelligence failure
 * used to leave behind.
 */
describe('ViewRFQPage — the primary action states why it is unavailable', () => {
  it('gives the quote-draft button a reason drawn from the actual blocker', async () => {
    // Fixture corrected: this case must be one the SERVER actually refuses. The default fixture
    // is ACTIONABLE_WITH_BLOCKERS, and QuoteService.PrepareQuoteDraftAsync refuses only
    // NO_QUOTE_REVIEW — it says so in a comment, because demanding VIABLE_READY "made the draft
    // unreachable for any line needing sourcing — the normal case." The client had kept
    // enforcing the abandoned rule, so this assertion was pinning the defect rather than the
    // intent. The intent — a disabled action always states why — is preserved below and is right.
    getRfqIntelligence.mockResolvedValue(intelligence({ commercialDecision: 'NO_QUOTE_REVIEW' }));
    render(<ViewRFQPage />, { wrapper });

    const button = await screen.findByRole('button', { name: /Prepare Quote Draft/i });
    expect(button).toBeDisabled();
    expect(button.closest('span')).toHaveAttribute(
      'aria-label',
      'Resolve 1 blocked line of the 1 line being quoted before preparing the customer quote.',
    );
  });

  it('lets a rep start a quote whose lines still need sourcing, and says so honestly', async () => {
    // The everyday case for a distributor: lines not in stock. The server allows the draft; the
    // client used to grey the button out with an explanation the rep could not act on.
    getRfqIntelligence.mockResolvedValue(intelligence({ commercialDecision: 'ACTIONABLE_WITH_BLOCKERS' }));
    render(<ViewRFQPage />, { wrapper });

    const button = await screen.findByRole('button', { name: /Prepare Quote Draft/i });
    expect(button).toBeEnabled();
    // And the reason must not claim every line is covered when it plainly is not.
    const label = button.closest('span')?.getAttribute('aria-label') ?? '';
    expect(label).toMatch(/You can start the quote now/i);
    expect(label).not.toMatch(/evidence-backed fulfilment route/i);
  });
});
