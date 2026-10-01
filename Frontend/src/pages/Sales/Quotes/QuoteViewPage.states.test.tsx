import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MemoryRouter, Route, Routes, useLocation } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { describe, expect, it, vi, beforeEach } from 'vitest';

/**
 * The quote page says one true thing per state, with one contained button (design review
 * 2026-09-28, state-correctness rows 1, 6, 7, 10, 19, 24, 31, 32).
 *
 * - Edit only on a draft: every other quote refused the whole edit page on Save.
 * - A sent quote's next step is Update status (one window for replied, the client's step, and
 *   how it ended). "Customer responded" and "Record outcome" were two buttons for it.
 * - A closed quote no longer promises a "Revise" the server refuses.
 * - One status chip in the list's words; "Stale" never on a replaced quote.
 * - ?action=po (the list's "Enter PO") opens the PO window once.
 * - Every state test is on the status code, never on the renameable label.
 */

const { getById, getRevisionInfo, getSendReadiness } = vi.hoisted(() => ({
  getById: vi.fn(),
  getRevisionInfo: vi.fn(),
  getSendReadiness: vi.fn(),
}));

vi.mock('../../../api/services/quoteService', () => ({
  default: {
    getById,
    getRevisionInfo,
    getSendReadiness,
    getPriceAttestation: vi.fn().mockResolvedValue({ satisfied: false }),
    sendEmail: vi.fn(),
    transitionStatus: vi.fn(),
    downloadPdf: vi.fn(),
    downloadUploadedFile: vi.fn(),
    revise: vi.fn(),
  },
}));
vi.mock('../../../api/services/procurementService', () => ({
  default: { getWorkbench: vi.fn().mockResolvedValue({ lines: [], awards: [], offers: [] }) },
}));
const permissions = vi.hoisted(() => ({ denied: new Set<string>() }));
vi.mock('../../../context/AuthContext', () => ({
  useAuth: () => ({
    userData: { businessUnitId: 1 },
    hasPermission: (module: string, action = 'view') => !permissions.denied.has(`${module}:${action}`),
  }),
}));
vi.mock('react-hot-toast', () => ({
  toast: Object.assign(vi.fn(), { success: vi.fn(), error: vi.fn() }),
  default: Object.assign(vi.fn(), { success: vi.fn(), error: vi.fn() }),
}));
vi.mock('../../../components/common/CommercialLineIntelligence', () => ({ default: () => null }));
vi.mock('./ExtendValidityDialog', () => ({ default: () => null }));
vi.mock('./FollowUpDialog', () => ({ default: () => null }));
vi.mock('./PriceConfirmationDialog', () => ({ default: () => null }));
vi.mock('./UpdateQuoteStatusDialog', () => ({
  default: ({ open }: { open: boolean }) => (open ? <div>update status window</div> : null),
}));
vi.mock('./customer-awards', () => ({
  CustomerAwardDialog: ({ open }: { open: boolean }) => (open ? <div>po window</div> : null),
}));
vi.mock('../../../components/common/EmailPromptDialog', () => ({
  default: ({ open }: { open: boolean }) => (open ? <div>recipient dialog</div> : null),
}));
vi.mock('../../Procurement/RFQs/SendQuoteDialog', () => ({
  default: ({ open, rfqId }: { open: boolean; rfqId: number }) => (open ? <div>send window for RFQ {rfqId}</div> : null),
}));

import QuoteViewPage from './QuoteViewPage';

const base = {
  id: 21, quoteNo: 'QT-0926-0021', lifecycleVersion: 2, version: 1,
  currencyId: 1, currencyCode: 'SAR', totalAmount: 1150, quoteDate: '2026-09-10', validUntil: '2027-01-10',
  customerName: 'Saudi Electricity Company', customerId: 30, commercialCaseId: 5, nexoraSerial: 'NX-1',
  rfqId: 41, rfqNo: 'NXR-RFQ-41', leadId: 9, ownerName: 'Faisal A.', createdBy: 'faisal', createdDate: '2026-09-10',
  sourceLeadRevision: 1, sourceRfqRevision: 1,
  quoteItems: [
    { id: 1, productName: 'Valve', quantity: 1, unitPrice: 1000, discount: 0, totalAmount: 1150, taxAmount: 150, taxableBase: 1000, taxRatePercentApplied: 15 },
  ],
};
const sent = { ...base, statusValue: 'Sent', statusCode: 'SENT', sentOn: '2026-09-11T08:00:00Z', daysSinceSent: 3 };

function LocationProbe() {
  const location = useLocation();
  return <div data-testid="location">{location.pathname}{location.search}</div>;
}

function renderQuote(path = '/sales/quotes/view/21') {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } });
  return render(
    <QueryClientProvider client={client}>
      <MemoryRouter initialEntries={[path]}>
        <Routes><Route path="/sales/quotes/view/:id" element={<><QuoteViewPage /><LocationProbe /></>} /></Routes>
      </MemoryRouter>
    </QueryClientProvider>,
  );
}

const containedButtons = () => screen.getAllByRole('button').filter((b) => b.classList.contains('MuiButton-contained'));

beforeEach(() => {
  vi.clearAllMocks();
  permissions.denied.clear();
  getRevisionInfo.mockResolvedValue({ quoteId: 21, quoteNo: 'QT-0926-0021', revisionNo: 1, chainLocked: false, canRevise: true });
  getSendReadiness.mockResolvedValue({ quoteId: 21, canSend: true, blockers: [] });
});

describe('a quote with the customer (SENT)', () => {
  it('has no Edit; Make a revision sits where Edit was', async () => {
    getById.mockResolvedValue(sent);
    renderQuote();

    const revise = await screen.findByRole('button', { name: 'Make a revision' });
    expect(revise).toHaveClass('MuiButton-outlined');
    expect(screen.queryByRole('button', { name: /^edit$/i })).not.toBeInTheDocument();
  });

  it('the control: a draft does show Edit', async () => {
    // The server never offers a revision of a plain draft: it is edited instead.
    getRevisionInfo.mockResolvedValue({ quoteId: 21, quoteNo: 'QT-0926-0021', revisionNo: 1, chainLocked: false, canRevise: false });
    getById.mockResolvedValue({ ...base, statusValue: 'Draft', statusCode: 'DRAFT' });
    renderQuote();

    expect(await screen.findByRole('button', { name: /^edit$/i })).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Make a revision' })).not.toBeInTheDocument();
  });

  it('Update status is the one contained button, and it opens the one status window', async () => {
    getById.mockResolvedValue(sent);
    renderQuote();

    const update = await screen.findByRole('button', { name: 'Update status' });
    expect(update).toHaveClass('MuiButton-contained');
    expect(containedButtons()).toEqual([update]);
    expect(screen.getAllByRole('button', { name: 'Update status' })).toHaveLength(1);
    expect(screen.queryByRole('button', { name: /customer responded/i })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /record outcome/i })).not.toBeInTheDocument();
    expect(screen.getByTestId('quote-next-step')).toHaveTextContent('Waiting for the customer. Update the status when they reply.');

    fireEvent.click(update);
    expect(await screen.findByText('update status window')).toBeInTheDocument();
  });

  it("shows the client's customer step in the facts row and the sentence", async () => {
    getById.mockResolvedValue({ ...sent, subStatusId: 4, subStatusName: 'Technical evaluation', subStatusKind: 'STEP', subStatusOn: '2026-09-20T10:00:00Z' });
    renderQuote();

    const stage = await screen.findByTestId('quote-customer-stage');
    expect(stage).toHaveTextContent(/Customer stage/i);
    expect(stage).toHaveTextContent('Technical evaluation');
    expect(screen.getByTestId('quote-next-step')).toHaveTextContent(/The customer is at Technical evaluation since 20 Sep 2026\. Update the status when they decide\./);
  });

  it('reads the code, not the label: a renamed "Sent" still offers Update status', async () => {
    getById.mockResolvedValue({ ...sent, statusValue: 'With customer' });
    renderQuote();

    expect(await screen.findByRole('button', { name: 'Update status' })).toBeInTheDocument();
    expect(screen.getByTestId('quote-next-step')).not.toHaveTextContent(/closed/i);
  });

  it('names who can act when this user cannot update the status', async () => {
    permissions.denied.add('Quotations:edit');
    getById.mockResolvedValue(sent);
    renderQuote();

    const panel = await screen.findByTestId('quote-next-step');
    expect(panel).toHaveTextContent('With the customer. Ask your sales manager to update the status when they reply.');
    expect(screen.queryByRole('button', { name: 'Update status' })).not.toBeInTheDocument();
  });

  it('a replaced quote shows Replaced, never Stale, and opens the newer revision', async () => {
    getRevisionInfo.mockResolvedValue({ quoteId: 21, quoteNo: 'QT-0926-0021', revisionNo: 1, supersededByQuoteId: 22, supersededByQuoteNo: 'QT-0926-0021-R2', chainLocked: false, canRevise: false });
    getById.mockResolvedValue({ ...sent, isStale: true, daysSinceSent: 12 });
    renderQuote();

    const open = await screen.findByRole('button', { name: 'Open QT-0926-0021-R2' });
    const status = screen.getByTestId('quote-status');
    expect(status).toHaveTextContent('Replaced');
    expect(status).not.toHaveTextContent(/no reply|stale/i);
    expect(screen.queryByRole('button', { name: 'Update status' })).not.toBeInTheDocument();
    fireEvent.click(open);
    await waitFor(() => expect(screen.getByTestId('location')).toHaveTextContent('/sales/quotes/view/22'));
  });
});

describe('a closed quote', () => {
  it('says how it closed and promises nothing the server refuses', async () => {
    getRevisionInfo.mockResolvedValue({ quoteId: 21, quoteNo: 'QT-0926-0021', revisionNo: 1, chainLocked: true, canRevise: false });
    getById.mockResolvedValue({ ...sent, statusValue: 'Rejected', statusCode: 'REJECTED', outcomeOn: '2026-09-12T09:00:00', outcomeReasonName: 'Price too high' });
    renderQuote();

    const panel = await screen.findByTestId('quote-next-step');
    expect(panel).toHaveTextContent('Closed as Lost on 12 Sep. Nothing is left to do here.');
    expect(panel).not.toHaveTextContent(/revise/i);
    // One status, in the list's words: "Lost", not "Rejected" beside "Lost".
    const status = screen.getByTestId('quote-status');
    expect(status).toHaveTextContent('Lost');
    expect(screen.queryByText('Rejected')).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /^edit$/i })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Make a revision' })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Update status' })).not.toBeInTheDocument();
    // Validity on a closed quote is a date, not a countdown.
    expect(screen.getByText('10 Jan 2027')).toBeInTheDocument();
  });

  it('an ordered quote has its PDF beside the sentence, once', async () => {
    getRevisionInfo.mockResolvedValue({ quoteId: 21, quoteNo: 'QT-0926-0021', revisionNo: 1, chainLocked: true, canRevise: false });
    getById.mockResolvedValue({ ...sent, statusValue: 'Ordered', statusCode: 'ORDERED', outcomeOn: '2026-09-12T09:00:00' });
    renderQuote();

    const panel = await screen.findByTestId('quote-next-step');
    const pdf = screen.getByRole('button', { name: 'Export PDF' });
    expect(panel).toContainElement(pdf);
    expect(pdf).toHaveClass('MuiButton-contained');
    expect(screen.getAllByRole('button', { name: 'Export PDF' })).toHaveLength(1);
  });
});

describe('a won quote', () => {
  it('opens the PO window once when the list sent the rep here to enter the PO', async () => {
    getRevisionInfo.mockResolvedValue({ quoteId: 21, quoteNo: 'QT-0926-0021', revisionNo: 1, chainLocked: true, canRevise: false });
    getById.mockResolvedValue({ ...sent, statusValue: 'Accepted', statusCode: 'ACCEPTED', outcomeOn: '2026-09-12T09:00:00' });
    renderQuote('/sales/quotes/view/21?action=po');

    expect(await screen.findByText('po window')).toBeInTheDocument();
    // The flag is dropped, so a reload or Back does not open it again.
    await waitFor(() => expect(screen.getByTestId('location')).toHaveTextContent(/^\/sales\/quotes\/view\/21$/));
    expect(screen.getByRole('button', { name: 'Enter PO' })).toHaveClass('MuiButton-contained');
  });

  it('without ?action=po the PO window stays closed until asked', async () => {
    getRevisionInfo.mockResolvedValue({ quoteId: 21, quoteNo: 'QT-0926-0021', revisionNo: 1, chainLocked: true, canRevise: false });
    getById.mockResolvedValue({ ...sent, statusValue: 'Accepted', statusCode: 'ACCEPTED', outcomeOn: '2026-09-12T09:00:00' });
    renderQuote();

    const enter = await screen.findByRole('button', { name: 'Enter PO' });
    expect(screen.queryByText('po window')).not.toBeInTheDocument();
    fireEvent.click(enter);
    expect(await screen.findByText('po window')).toBeInTheDocument();
  });
});

describe('sending a draft', () => {
  const draft = { ...base, statusValue: 'Draft', statusCode: 'DRAFT' };

  it('from an RFQ: Send to customer opens the RFQ send window (email, or download for the portal)', async () => {
    getById.mockResolvedValue(draft);
    renderQuote();

    await waitFor(() => expect(screen.getByTestId('quote-next-step')).toHaveTextContent("Send it by email, or download it for the buyer's portal."));
    // Re-read after the check answers: the button moves from the rail into the panel.
    const send = screen.getByRole('button', { name: /^send to customer$/i });
    expect(send).toHaveClass('MuiButton-contained');
    fireEvent.click(send);

    expect(await screen.findByText('send window for RFQ 41')).toBeInTheDocument();
    expect(screen.queryByText('recipient dialog')).not.toBeInTheDocument();
  });

  it('with no RFQ: the email chain, as before', async () => {
    getById.mockResolvedValue({ ...draft, rfqId: undefined, rfqNo: undefined });
    renderQuote();

    fireEvent.click(await screen.findByRole('button', { name: /^send to customer$/i }));

    expect(await screen.findByText('recipient dialog')).toBeInTheDocument();
    expect(screen.queryByText(/send window for RFQ/)).not.toBeInTheDocument();
  });

  it('a failed send check says it could not check, never "nothing is blocking"', async () => {
    getSendReadiness.mockRejectedValue(new Error('network'));
    getById.mockResolvedValue(draft);
    renderQuote();

    const panel = await screen.findByTestId('quote-next-step');
    await waitFor(() => expect(panel).toHaveTextContent("Couldn't check whether anything blocks sending."));
    expect(panel).not.toHaveTextContent(/nothing is blocking/i);
    expect(screen.getByRole('button', { name: 'Try again' })).toBeInTheDocument();
  });
});

describe('a draft whose send check has not answered yet', () => {
  it('says it is checking, never "nothing is blocking", and promotes no button', async () => {
    getSendReadiness.mockReturnValue(new Promise(() => {}));
    getById.mockResolvedValue({ ...base, statusValue: 'Draft', statusCode: 'DRAFT' });
    renderQuote();

    const panel = await screen.findByTestId('quote-next-step');
    expect(panel).toHaveTextContent('Checking what could block sending…');
    expect(panel).not.toHaveTextContent(/nothing is blocking/i);
    // The Send control is on the page (outlined), so "no contained button" is not vacuous.
    expect(screen.getByRole('button', { name: /^send to customer$/i })).toHaveClass('MuiButton-outlined');
    expect(containedButtons()).toHaveLength(0);
  });

  it('the control: once the check answers, Send is the one contained button', async () => {
    getById.mockResolvedValue({ ...base, statusValue: 'Draft', statusCode: 'DRAFT' });
    renderQuote();

    await waitFor(() => expect(screen.getByRole('button', { name: /^send to customer$/i })).toHaveClass('MuiButton-contained'));
    const send = screen.getByRole('button', { name: /^send to customer$/i });
    expect(screen.getByTestId('quote-next-step')).not.toHaveTextContent(/Checking what could block sending/);
    expect(containedButtons()).toEqual([send]);
  });
});

describe('a draft whose delivery ended for good, with a customer revision open', () => {
  it('Make a revision is the one contained button; Apply the new quantities steps back to outlined', async () => {
    getSendReadiness.mockResolvedValue({
      quoteId: 21, canSend: false,
      blockers: [{ code: 'DELIVERY_FAILED', message: 'Delivery of this quote failed for good. Issue it as a new revision and send that.' }],
    });
    getById.mockResolvedValue({ ...base, statusValue: 'Draft', statusCode: 'DRAFT', revisionImpact: 'DRAFT_STALE_REVIEW_REQUIRED', revisionImpactDetail: null });
    renderQuote();

    await waitFor(() => expect(screen.getByRole('button', { name: 'Make a revision' })).toHaveClass('MuiButton-contained'));
    const revise = screen.getByRole('button', { name: 'Make a revision' });
    const apply = screen.getByRole('button', { name: /apply the new quantities/i });
    expect(apply).toHaveClass('MuiButton-outlined');
    expect(containedButtons()).toEqual([revise]);
  });
});

describe('a quote that cannot be shown', () => {
  it("says it doesn't exist or isn't yours, with the way back", async () => {
    getById.mockRejectedValue({ response: { status: 404 } });
    renderQuote();

    expect(await screen.findByText("This quote doesn't exist or isn't yours to see.")).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: 'Back to Quotes' }));
    await waitFor(() => expect(screen.queryByText("This quote doesn't exist or isn't yours to see.")).not.toBeInTheDocument());
  });
});
