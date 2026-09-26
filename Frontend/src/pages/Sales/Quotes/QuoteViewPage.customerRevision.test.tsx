import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { describe, expect, it, vi, beforeEach } from 'vitest';

/**
 * A customer revision must say what changed, and offer to take the change.
 *
 * Driving QT-0926-0001 on 2026-09-15 the panel read "This Quote Draft is stale and must be
 * reviewed against Lead Revision 3" — 3 being the revision the quote was BUILT from, not the one
 * that arrived — never said what changed, and its one button "Mark review complete" let the draft
 * proceed with the OLD quantities. And after that click the readiness list still said "This quote
 * is stale…" until a manual reload, because only the quote query was invalidated.
 *
 * These tests pin the sentence, the two buttons, and that both queries have caught up before the
 * rep is told the review is done.
 */

const { getById, getPriceAttestation, getSendReadiness, resolveRevisionImpact, applyRevisionQuantities } = vi.hoisted(() => ({
  getById: vi.fn(),
  getPriceAttestation: vi.fn(),
  getSendReadiness: vi.fn(),
  resolveRevisionImpact: vi.fn(),
  applyRevisionQuantities: vi.fn(),
}));

const toastMock = vi.hoisted(() => Object.assign(vi.fn(), { success: vi.fn(), error: vi.fn() }));

vi.mock('../../../api/services/quoteService', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../../../api/services/quoteService')>();
  return {
    ...actual,
    default: {
      getById,
      getPriceAttestation,
      getSendReadiness,
      resolveRevisionImpact,
      applyRevisionQuantities,
      sendEmail: vi.fn(),
      transitionStatus: vi.fn(),
      exportPdf: vi.fn(),
      getRevisions: vi.fn().mockResolvedValue([]),
      getRevisionInfo: vi.fn().mockResolvedValue(null),
    },
  };
});

vi.mock('../../../api/services/procurementService', () => ({
  default: { getWorkbench: vi.fn().mockResolvedValue(null), getRfqIntelligence: vi.fn().mockResolvedValue(null) },
}));

vi.mock('../../../context/AuthContext', () => ({
  useAuth: () => ({ userData: { businessUnitId: 7 }, hasPermission: () => true }),
}));

vi.mock('react-hot-toast', () => ({ toast: toastMock, default: toastMock }));

vi.mock('../../../components/common/CommercialLineIntelligence', () => ({ default: () => null }));
vi.mock('./QuoteOutcomeDialog', () => ({ default: () => null }));
vi.mock('./ExtendValidityDialog', () => ({ default: () => null }));
vi.mock('./PriceConfirmationDialog', () => ({ default: () => null }));
vi.mock('./customer-awards', () => ({ CustomerAwardDialog: () => null }));
vi.mock('../../../components/common/EmailPromptDialog', () => ({ default: () => null }));

import QuoteViewPage from './QuoteViewPage';

/** QT-0926-0001 as driven: a draft built on lead revision 3, revision 4 just arrived. */
const staleDraft = () => ({
  id: 91,
  quoteNo: 'QT-0926-0001',
  statusValue: 'Draft',
  statusCode: 'DRAFT',
  lifecycleVersion: 1,
  currencyId: 3,
  currencyCode: 'SAR',
  totalAmount: 4205,
  quoteDate: '2026-09-15',
  validUntil: '2026-10-15',
  customerName: 'Marafiq',
  sourceLeadRevision: 3,
  sourceRfqRevision: 1,
  revisionImpact: 'DRAFT_STALE_REVIEW_REQUIRED',
  revisionImpactDetail: {
    impactId: 501,
    impactType: 'DRAFT_STALE_REVIEW_REQUIRED',
    fromRevision: 3,
    toRevision: 4,
    changes: [
      { line: '2', field: 'quantity', from: '20', to: '35' },
      { line: '4', field: 'quantity', from: '1500', to: '2000' },
    ],
  },
  quoteItems: [
    { id: 1, productName: 'Gasket', quantity: 20, unitPrice: 10, discount: 0, totalAmount: 230, taxAmount: 30, taxableBase: 200, taxRatePercentApplied: 15 },
    { id: 2, productName: 'Bolt', quantity: 1500, unitPrice: 2, discount: 0, totalAmount: 3450, taxAmount: 450, taxableBase: 3000, taxRatePercentApplied: 15 },
  ],
});

const reviewedDraft = () => ({ ...staleDraft(), revisionImpact: null, revisionImpactDetail: null });

const staleReadiness = {
  quoteId: 91,
  canSend: false,
  blockers: [{
    code: 'CUSTOMER_REVISION_UNRESOLVED',
    message: 'This quote is stale because a customer revision was received. Review and resolve the revision impact before sending it.',
  }],
};
const clearReadiness = { quoteId: 91, canSend: true, blockers: [] };

function renderQuote() {
  const client = new QueryClient({
    defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
  });
  return render(
    <QueryClientProvider client={client}>
      <MemoryRouter initialEntries={['/sales/quotes/view/91']}>
        <Routes>
          <Route path="/sales/quotes/view/:id" element={<QuoteViewPage />} />
        </Routes>
      </MemoryRouter>
    </QueryClientProvider>,
  );
}

beforeEach(() => {
  vi.clearAllMocks();
  getPriceAttestation.mockResolvedValue({ satisfied: true });
  getById.mockResolvedValue(staleDraft());
  getSendReadiness.mockResolvedValue(staleReadiness);
  resolveRevisionImpact.mockResolvedValue(undefined);
  applyRevisionQuantities.mockResolvedValue({
    quoteId: 91, fromRevision: 3, toRevision: 4, linesUpdated: 2,
    applied: [{ line: '2', field: 'quantity', from: '20', to: '35' }, { line: '4', field: 'quantity', from: '1500', to: '2000' }],
    linesNotOnQuote: [],
  });
});

describe('QuoteViewPage — a customer revision arrived after the draft', () => {
  it('names the revision that ARRIVED, the one the draft was built on, and each changed quantity', async () => {
    renderQuote();

    expect(await screen.findByText(/Revision 4 arrived after this draft \(built on revision 3\)/i)).toBeVisible();
    expect(screen.getByText(/line 2 quantity 20 → 35 · line 4 quantity 1,500 → 2,000/i)).toBeVisible();
    // The old sentence — the built-from revision presented as the thing to review against.
    expect(screen.queryByText(/must be reviewed against Lead Revision 3/i)).not.toBeInTheDocument();
  });

  it('offers to apply the new quantities as THE next step, and to keep as quoted as the alternative', async () => {
    renderQuote();

    const apply = await screen.findByRole('button', { name: /apply the new quantities/i });
    const keep = screen.getByRole('button', { name: /keep as quoted/i });
    expect(apply).toHaveClass('MuiButton-contained');
    expect(keep).toHaveClass('MuiButton-outlined');
    // Exactly one contained button on the screen: the rep is not offered two "primary" moves.
    const contained = screen.getAllByRole('button').filter((b) => b.classList.contains('MuiButton-contained'));
    expect(contained).toHaveLength(1);
    expect(screen.queryByRole('button', { name: /mark review complete/i })).not.toBeInTheDocument();
    // And the one-sentence next step says what the choice is.
    expect(screen.getByTestId('quote-next-step')).toHaveTextContent(/Apply the new quantities, or keep it as quoted/i);
  });

  it('applies the quantities on the server and the stale notices are gone before the rep is told', async () => {
    getById.mockResolvedValueOnce(staleDraft()).mockResolvedValue(reviewedDraft());
    getSendReadiness.mockResolvedValueOnce(staleReadiness).mockResolvedValue(clearReadiness);
    renderQuote();

    fireEvent.click(await screen.findByRole('button', { name: /apply the new quantities/i }));

    await waitFor(() => expect(applyRevisionQuantities).toHaveBeenCalledWith(91));
    await waitFor(() => expect(toastMock.success).toHaveBeenCalled());
    // By the time the rep reads the confirmation, both the quote and the readiness list agree.
    expect(screen.queryByText(/Revision 4 arrived after this draft/i)).not.toBeInTheDocument();
    expect(screen.queryByText(/This quote is stale because a customer revision was received/i)).not.toBeInTheDocument();
    expect(toastMock.success.mock.calls[0][0]).toMatch(/2 lines/i);
  });

  it('keep as quoted resolves the impact and refreshes the readiness list without a reload', async () => {
    getById.mockResolvedValueOnce(staleDraft()).mockResolvedValue(reviewedDraft());
    getSendReadiness.mockResolvedValueOnce(staleReadiness).mockResolvedValue(clearReadiness);
    renderQuote();

    expect(await screen.findByText(/This quote is stale because a customer revision was received/i)).toBeVisible();
    fireEvent.click(screen.getByRole('button', { name: /keep as quoted/i }));

    await waitFor(() => expect(resolveRevisionImpact).toHaveBeenCalledWith(91));
    await waitFor(() => expect(toastMock.success).toHaveBeenCalled());
    // The readiness query was asked again — the defect was that it never was.
    expect(getSendReadiness.mock.calls.length).toBeGreaterThanOrEqual(2);
    expect(screen.queryByText(/This quote is stale because a customer revision was received/i)).not.toBeInTheDocument();
    expect(screen.queryByText(/Revision 4 arrived after this draft/i)).not.toBeInTheDocument();
  });

  it('still reads correctly on an older server that sends only the impact type', async () => {
    getById.mockResolvedValue({ ...staleDraft(), revisionImpactDetail: null });
    renderQuote();

    expect(await screen.findByText(/A newer customer revision arrived after this draft \(built on revision 3\)/i)).toBeVisible();
    expect(screen.getByRole('button', { name: /keep as quoted/i })).toBeInTheDocument();
  });
});
