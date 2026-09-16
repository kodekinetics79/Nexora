import { beforeEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, within } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router-dom';
import SalesTodayPage from './SalesTodayPage';

/**
 * Sales today showed "Unassigned leads 2" in a tile and "Nothing requires sales attention right
 * now." in the queue under it; the Inbox had two documents to check. The opportunity block leaked
 * "Server-ranked guidance in shadow mode… Cohort: 0 eligible | 0 insufficient evidence… No
 * persisted shadow priorities…", and a coaching card read "Observed 0; policy threshold 1. Sample
 * 1; confidence 100%. Policy growth-intelligence-v2.5". The queue now lists what the tiles count,
 * and the machinery is gone or one plain sentence. The backend ranking is untouched.
 */

const navigate = vi.fn();
const authUser: { isManager?: boolean } = {};

vi.mock('../../api/services/commercialIntelligenceService', () => ({
  default: {
    getSalesToday: vi.fn().mockResolvedValue({
      generatedAt: '2026-09-15T12:00:00Z', scope: 'tenant',
      metrics: [
        { key: 'open-follow-ups', label: 'Open follow-ups', value: 0, unit: 'count' },
        { key: 'unassigned-leads', label: 'Unassigned leads', value: 2, unit: 'count' },
      ],
      attentionItems: [], attentionItemLimit: 100, attentionItemsTruncated: false,
    }),
    getCoachingRecovery: vi.fn().mockResolvedValue({
      scope: 'tenant', policyVersion: 'growth-intelligence-v2.5', generatedAt: '2026-09-15T12:00:00Z',
      dataCompleteness: { status: 'complete', incompleteSources: [] },
      coachingFindings: [{
        findingKey: 'f1', salesRepName: 'Sara Bin Ali', recommendation: 'Follow up sooner', customerName: 'Saudi Aramco', reference: 'RFQ-1',
        severity: 'medium', observedValue: 0, observedUnit: null, thresholdValue: 1, sampleSize: 1, confidence: 1,
        policyVersion: 'growth-intelligence-v2.5', asOf: '2026-09-15T00:00:00Z', evidence: [], actionRoute: '/sales/quotes', latestAcknowledgement: null,
      }],
      recoveryOpportunities: [],
    }),
    acknowledgeCoachingFinding: vi.fn(),
  },
}));
vi.mock('../../api/services/opportunityPriorityService', () => ({
  default: {
    getPriorities: vi.fn().mockResolvedValue({
      items: [], total: 0, pageNumber: 1, pageSize: 10, generatedAtUtc: '2026-09-15T12:00:00Z', accessScope: 'tenant',
      policyVersion: 'opportunity-priority-v1', mode: 'Shadow',
      cohort: { currentRecommendations: 0, eligibleRecommendations: 0, insufficientEvidenceRecommendations: 0, recommendationsWithObservedOutcome: 0, recommendationsWithFeedback: 0, accuracyPercent: null, accuracyStatus: 'insufficient-evidence' },
    }),
    reconcileAll: vi.fn(),
  },
  createOpportunityCommandIdentity: () => ({ idempotencyKey: 'k', correlationId: 'c' }),
}));
vi.mock('../../api/services/leadService', () => ({
  default: {
    getOutstandingLeads: vi.fn().mockResolvedValue({
      items: [
        { id: 683, rfqno: 'RFQ-683', buyersName: 'Marafiq buyer', customerName: 'Marafiq', acceptedDate: '2026-09-15T08:00:00Z', unassignedHours: 5.2, isUnassignedOverdue: false },
        { id: 686, rfqno: 'RFQ-686', buyersName: 'Aramco buyer', customerName: null, acceptedDate: '2026-09-15T11:00:00Z', unassignedHours: 0.3, isUnassignedOverdue: false },
      ],
      totalCount: 2, pageNumber: 1, pageSize: 25,
    }),
  },
}));
vi.mock('../../api/services/extractionReviewService', () => ({
  default: {
    getNeedsReview: vi.fn().mockResolvedValue({
      items: [{ id: 91, rfqno: 'RFQ-91', buyersName: 'Saudi Electricity Company', recDate: '2026-09-14', bidClosingDate: '2026-09-20T00:00:00Z', leadSource: 'Email', aiconfidence: null, itemCount: 3, reviewReason: null, receivedOn: null, reviewVersion: 1 }],
      totalCount: 1, pageNumber: 1, pageSize: 25,
    }),
  },
}));
vi.mock('../../context/AuthContext', () => ({
  useAuth: () => ({ hasPermission: () => true, userData: authUser }),
}));
vi.mock('react-router-dom', async (importOriginal) => {
  const actual = await importOriginal<typeof import('react-router-dom')>();
  return { ...actual, useNavigate: () => navigate };
});

const renderPage = () => render(
  <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } })}>
    <MemoryRouter><SalesTodayPage /></MemoryRouter>
  </QueryClientProvider>,
);

beforeEach(() => {
  vi.clearAllMocks();
  authUser.isManager = false;
});

describe('Sales today — the attention queue lists what the tiles count', () => {
  it('shows the unowned inquiries and the documents to check instead of "nothing requires attention"', async () => {
    renderPage();
    const queue = await screen.findByRole('region', { name: 'Sales attention queue' });
    expect(within(queue).getByText('RFQ-683')).toBeInTheDocument();
    expect(within(queue).getByText('RFQ-686')).toBeInTheDocument();
    expect(within(queue).getByText('RFQ-91')).toBeInTheDocument();
    expect(within(queue).getAllByText(/Nobody owns this inquiry/).length).toBe(2);
    expect(within(queue).getByText('Nobody owns this inquiry; waiting 5 h')).toBeInTheDocument();
    expect(within(queue).getByText('A person has to check what was read from this document')).toBeInTheDocument();
    expect(screen.queryByText(/Nothing requires sales attention/)).not.toBeInTheDocument();
    expect(screen.queryByText(/Nothing is waiting on you/)).not.toBeInTheDocument();

    const documentRow = within(queue).getByText('RFQ-91').closest('tr')!;
    fireEvent.click(within(documentRow).getByRole('button', { name: 'Open' }));
    expect(navigate).toHaveBeenCalledWith('/procurement/extraction/review/91');
  });

  it('says nothing about shadow mode, cohorts, policies or confidence percentages', async () => {
    renderPage();
    await screen.findByRole('region', { name: 'Sales attention queue' });
    expect(await screen.findByText('Seen 0, expected at least 1, from 1 case.')).toBeInTheDocument();
    for (const machinery of [/shadow mode/i, /Cohort:/, /No persisted shadow priorities/, /Policy growth-intelligence/, /policy threshold/i, /confidence 100%/i, /Accuracy:/]) {
      expect(screen.queryByText(machinery)).not.toBeInTheDocument();
    }
  });

  it('hides the suggested-order block from a rep when there is nothing to suggest, and keeps the rebuild button for a manager', async () => {
    renderPage();
    await screen.findByRole('region', { name: 'Sales attention queue' });
    expect(screen.queryByText('Suggested order of work')).not.toBeInTheDocument();

    authUser.isManager = true;
    renderPage();
    expect(await screen.findByRole('button', { name: 'Rebuild suggestions' })).toBeInTheDocument();
  });
});
