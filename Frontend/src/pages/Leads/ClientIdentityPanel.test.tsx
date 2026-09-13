import { beforeEach, describe, expect, it, vi } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { ReactNode } from 'react';
import ClientIdentityPanel from './ClientIdentityPanel';
import type { LeadResponseDTO } from '../../api/services/leadService';

const getClientCandidates = vi.fn();
const linkClient = vi.fn();
const submitReview = vi.fn();
const getAll = vi.fn();
const getByCustomer = vi.fn();
const testAccess = vi.hoisted(() => ({ denied: new Set<string>() }));

vi.mock('../../context/AuthContext', () => ({
  useAuth: () => ({
    hasPermission: (moduleName: string, action = 'view') =>
      !testAccess.denied.has(`${moduleName}:${action}`),
  }),
}));

vi.mock('../../api/services/leadService', () => ({
  default: {
    getClientCandidates: (...args: unknown[]) => getClientCandidates(...args),
    linkClient: (...args: unknown[]) => linkClient(...args),
    getById: vi.fn(),
  },
}));
vi.mock('../../api/services/extractionReviewService', () => ({
  default: { submitReview: (...args: unknown[]) => submitReview(...args) },
}));
vi.mock('../../api/services/customerService', () => ({
  default: { getAll: (...args: unknown[]) => getAll(...args) },
}));
vi.mock('../../api/services/contactService', () => ({
  default: { getByCustomer: (...args: unknown[]) => getByCustomer(...args) },
}));

const lead = (over: Partial<LeadResponseDTO> = {}): LeadResponseDTO => ({
  id: 501,
  customerMatchStatus: 'UNRESOLVED',
  rfqno: 'C001046556',
  buyersName: 'AMER S. AL-DOSSARI',
  leadSource: 'Email',
  recDate: '2026-02-15T00:00:00Z',
  bidClosingDate: '2026-02-22T00:00:00Z',
  emailSource: 'inbox',
  clientemail: '57322@se.com.sa',
  status: 'New',
  isAccepted: false,
  isRejected: false,
  aiconfidence: 0.8,
  itemCount: 12,
  reviewVersion: 3,
  requiresCommercialReview: false,
  commercialFactsVerified: false,
  currentRevisionNumber: 1,
  businessUnitId: 80101,
  lifecycleVersion: 1,
  leadItems: [{ id: 900, quantity: 4, aiconfidence: 0.9, productShortName: 'Valve' }],
  ...over,
});

const wrapper = ({ children }: { children: ReactNode }) => {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return (
    <MemoryRouter>
      <QueryClientProvider client={client}>{children}</QueryClientProvider>
    </MemoryRouter>
  );
};

const renderPanel = (node: ReactNode) => render(<>{node}</>, { wrapper });

beforeEach(() => {
  vi.clearAllMocks();
  testAccess.denied.clear();
  getClientCandidates.mockResolvedValue([]);
  getAll.mockResolvedValue({ items: [], totalCount: 0, pageNumber: 1, pageSize: 10 });
  getByCustomer.mockResolvedValue([]);
  submitReview.mockResolvedValue(lead({ customerId: 42 }));
  linkClient.mockResolvedValue(lead({ customerId: 42 }));
});

describe('ClientIdentityPanel — unresolved', () => {
  it('shows the evidence Nexora does hold instead of a dead end, plus a live action', async () => {
    renderPanel(
      <ClientIdentityPanel
        lead={lead({
          customerCompanyNameExtracted: 'SAUDI ELECTRICITY COMPANY',
          customerCompanyEvidence: 'PURCHASE OPTIONAL AGREEMENT FOR SAUDI ELECTRICITY COMPANY',
          customerPortalNameExtracted: 'MATERIALS E-BIDDING SYSTEM',
          supplierAccountRefOnDocument: '2004414',
        })}
      />,
    );

    expect(await screen.findByText('No client linked yet')).toBeInTheDocument();

    // Every scrap of evidence a rep needs to decide in seconds.
    expect(screen.getByText('57322@se.com.sa')).toBeInTheDocument();
    expect(screen.getByText('SAUDI ELECTRICITY COMPANY')).toBeInTheDocument();
    expect(screen.getByText(/PURCHASE OPTIONAL AGREEMENT/)).toBeInTheDocument();
    expect(screen.getByText('MATERIALS E-BIDDING SYSTEM')).toBeInTheDocument();
    expect(screen.getByText('2004414')).toBeInTheDocument();
    expect(screen.getByText('AMER S. AL-DOSSARI')).toBeInTheDocument();

    // A dead end is the failure mode being fixed: there is always a way out, and when the
    // document names the company the way out carries its name instead of an empty search box.
    expect(screen.getByRole('button', { name: 'Set up SAUDI ELECTRICITY COMPANY' })).toBeEnabled();
    expect(screen.getByRole('button', { name: 'Search your customers' })).toBeEnabled();
    expect(screen.getByText(/looks like a buyer you have not set up yet/i)).toBeInTheDocument();
  });

  it('falls back to a plain choose when the document names no company', async () => {
    renderPanel(<ClientIdentityPanel lead={lead({ customerCompanyNameExtracted: null })} />);

    expect(await screen.findByText('No client linked yet')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Choose the customer' })).toBeEnabled();
    expect(screen.queryByRole('button', { name: /^Set up/ })).not.toBeInTheDocument();
  });

  it("does not present Nexora's own synthetic sender as evidence", async () => {
    renderPanel(<ClientIdentityPanel lead={lead({ clientemail: 'extraction@pipeline.local' })} />);

    expect(await screen.findByText('No client linked yet')).toBeInTheDocument();
    expect(screen.queryByText('extraction@pipeline.local')).not.toBeInTheDocument();
  });

  it('hides every write affordance when the user cannot edit, but still states the fact', async () => {
    renderPanel(<ClientIdentityPanel lead={lead()} canEdit={false} />);

    expect(await screen.findByText('No client linked yet')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /Find client/i })).not.toBeInTheDocument();
  });
});

describe('ClientIdentityPanel — suggested', () => {
  const suggested = lead({
    customerMatchStatus: 'SUGGESTED',
    customerMatchReasonCode: 'SENDER_DOMAIN',
  });

  beforeEach(() => {
    getClientCandidates.mockResolvedValue([
      { rank: 1, customerId: 42, customerName: 'Saudi Electricity Company', confidence: 0.95, reasonCode: 'SENDER_DOMAIN' },
    ]);
  });

  it('keeps an ambiguous lead in review language when candidate ranking is not yet available', async () => {
    getClientCandidates.mockResolvedValue([]);
    renderPanel(<ClientIdentityPanel lead={lead({ customerMatchStatus: 'AMBIGUOUS' })} />);

    expect(await screen.findByText('Several clients may match')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Review possible clients' })).toBeEnabled();
    expect(screen.queryByText('No client linked yet')).not.toBeInTheDocument();
  });

  it('says a weak hint might be the client and makes choosing the client the primary control', async () => {
    // Production, 2026-09-12: a numbering-pattern hint at 55% read "Nexora thinks this is
    // Saudi Aramco" beside a gold Confirm button, on a document that named Saudi Electricity.
    getClientCandidates.mockResolvedValue([
      { rank: 1, customerId: 30, customerName: 'Saudi Aramco', confidence: 0.55, reasonCode: 'RFQ_PATTERN' },
    ]);
    renderPanel(<ClientIdentityPanel lead={lead({ customerMatchStatus: 'SUGGESTED', customerMatchReasonCode: 'RFQ_PATTERN' })} />);

    expect(await screen.findByText(/This might be/)).toBeInTheDocument();
    expect(screen.getByText(/Only a weak hint/)).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Choose the client' })).toHaveClass('MuiButton-contained');
    expect(screen.getByRole('button', { name: /Confirm Saudi Aramco/ })).toHaveClass('MuiButton-outlined');
    expect(screen.queryByText(/Nexora thinks this is/)).not.toBeInTheDocument();
  });

  it('names the suggestion, its confidence and its evidence', async () => {
    renderPanel(<ClientIdentityPanel lead={suggested} />);

    expect(await screen.findByText('Saudi Electricity Company')).toBeInTheDocument();
    expect(screen.getByText(/\(95%\)/)).toBeInTheDocument();
    expect(screen.getByText(/Matched because the sender's email domain belongs to this client\./)).toBeInTheDocument();
  });

  /**
   * Lead 682 in production: a Marafiq RFQ whose own text reads "MARAFIQ invites bidders in
   * accordance with our Request for Quotation(RFQ)." The candidate carried that sentence
   * and the panel dropped it for the category phrase, so "Nexora thinks this is Marafiq"
   * never said what on the page made it think so.
   */
  it("prefers the candidate's own sentence, then the lead's, over the reason-code phrase", async () => {
    getClientCandidates.mockResolvedValue([
      {
        rank: 1,
        customerId: 55,
        customerName: 'Marafiq',
        confidence: 0.9,
        reasonCode: 'NAME_IN_DOCUMENT',
        explanation: '"MARAFIQ" appears in the sentence that names the buyer: "MARAFIQ invites bidders in accordance with our Request for Quotation(RFQ)."',
      },
    ]);
    renderPanel(
      <ClientIdentityPanel
        lead={lead({
          customerMatchStatus: 'SUGGESTED',
          customerMatchReasonCode: 'NAME_IN_DOCUMENT',
          customerMatchExplanation: 'Same portal vendor code 1495 on MARAFIQ.',
        })}
      />,
    );

    expect(await screen.findByText(/MARAFIQ invites bidders in accordance with our Request for Quotation/))
      .toBeInTheDocument();
    expect(screen.getByText('Why')).toBeInTheDocument();
    expect(screen.queryByText(/Matched because/)).not.toBeInTheDocument();
    expect(screen.queryByText(/Same portal vendor code/)).not.toBeInTheDocument();
  });

  it("falls back to the lead's sentence when the candidate carries none", async () => {
    getClientCandidates.mockResolvedValue([
      { rank: 1, customerId: 55, customerName: 'Marafiq', confidence: 0.9, reasonCode: 'NAME_IN_DOCUMENT' },
    ]);
    renderPanel(
      <ClientIdentityPanel
        lead={lead({
          customerMatchStatus: 'SUGGESTED',
          customerMatchReasonCode: 'NAME_IN_DOCUMENT',
          customerMatchExplanation: '"MARAFIQ" appears in the company named on the document: "MARAFIQ".',
        })}
      />,
    );

    expect(await screen.findByText(/appears in the company named on the document/)).toBeInTheDocument();
    expect(screen.queryByText(/Matched because/)).not.toBeInTheDocument();
  });

  it('keeps the weak-hint warning as its own sentence so the quoted words survive intact', async () => {
    getClientCandidates.mockResolvedValue([
      {
        rank: 1,
        customerId: 30,
        customerName: 'Saudi Aramco',
        confidence: 0.55,
        reasonCode: 'RFQ_PATTERN',
        explanation: '"Saudi Aramco" numbering: RFQ number C001046556 follows this client\'s numbering.',
      },
    ]);
    renderPanel(<ClientIdentityPanel lead={lead({ customerMatchStatus: 'SUGGESTED', customerMatchReasonCode: 'RFQ_PATTERN' })} />);

    expect(await screen.findByText('Only a weak hint — check the document before confirming.')).toBeInTheDocument();
    // Not lowercased into `"saudi Aramco" numbering…` by being folded into the warning.
    expect(screen.getByText(/"Saudi Aramco" numbering/)).toBeInTheDocument();
  });

  /**
   * One click, and it goes to the dedicated client endpoint.
   *
   * This used to submit an extraction review, echoing every stored line item back so the
   * server would not delete them. That path is refused outright for any lead whose
   * extraction already succeeded — the ordinary case — so a one-click confirm on such a
   * lead did nothing but raise a toast. The endpoint it calls now has no extraction-review
   * preconditions and needs no line items echoed.
   */
  it('confirms in ONE click, through the client endpoint rather than extraction review', async () => {
    const onChanged = vi.fn();
    renderPanel(<ClientIdentityPanel lead={suggested} onChanged={onChanged} />);

    const confirm = await screen.findByRole('button', { name: /Confirm Saudi Electricity Company/i });
    confirm.click();

    await waitFor(() => expect(linkClient).toHaveBeenCalledTimes(1));
    const [leadId, body] = linkClient.mock.calls[0];
    expect(leadId).toBe(501);
    expect(body).toEqual({ customerId: 42, contactId: null });
    expect(submitReview).not.toHaveBeenCalled();
    await waitFor(() => expect(onChanged).toHaveBeenCalled());
  });

  it('fails closed when Lead edit permission is revoked immediately before confirmation', async () => {
    renderPanel(<ClientIdentityPanel lead={suggested} />);

    const confirm = await screen.findByRole('button', { name: /Confirm Saudi Electricity Company/i });
    testAccess.denied.add('Leads:edit');
    confirm.click();

    await waitFor(() => expect(linkClient).not.toHaveBeenCalled());
    expect(submitReview).not.toHaveBeenCalled();
  });

  it('flags competing candidates rather than presenting one guess as settled', async () => {
    getClientCandidates.mockResolvedValue([
      { rank: 1, customerId: 42, customerName: 'Saudi Electricity Company', confidence: 0.75, reasonCode: 'NAME_FUZZY' },
      { rank: 2, customerId: 43, customerName: 'SEC Distribution Co.', confidence: 0.74, reasonCode: 'NAME_FUZZY' },
    ]);
    renderPanel(<ClientIdentityPanel lead={lead({ customerMatchStatus: 'AMBIGUOUS' })} />);

    expect(await screen.findByText(/1 other client also matches the evidence\./)).toBeInTheDocument();
  });

  it('stages the choice instead of writing it when the host owns the submission', async () => {
    const onSelect = vi.fn();
    renderPanel(<ClientIdentityPanel lead={suggested} onSelect={onSelect} />);

    (await screen.findByRole('button', { name: /Confirm Saudi Electricity Company/i })).click();

    await waitFor(() => expect(onSelect).toHaveBeenCalledWith(
      expect.objectContaining({ customerId: 42, customerName: 'Saudi Electricity Company' }),
    ));
    // Deferred mode must never write on its own — that would bump the lead's
    // review version and conflict with the reviewer's own save.
    expect(submitReview).not.toHaveBeenCalled();
  });
});

describe('ClientIdentityPanel — resolved', () => {
  it('links to the client and says why it was matched', async () => {
    renderPanel(
      <ClientIdentityPanel
        lead={lead({
          customerId: 42,
          customerName: 'Saudi Electricity Company',
          customerMatchStatus: 'CONFIRMED',
          customerMatchReasonCode: 'SENDER_EMAIL_EXACT',
          contactId: 7,
        })}
      />,
    );

    const link = await screen.findByRole('link', { name: 'Saudi Electricity Company' });
    expect(link).toHaveAttribute('href', '/customers/42');
    expect(screen.getByText('Confirmed by a person')).toBeInTheDocument();
    expect(screen.getByText('Why')).toBeInTheDocument();
    expect(screen.getByText(/Matched because the sender's email address is on file/)).toBeInTheDocument();
    // A resolved lead needs no suggestions.
    expect(getClientCandidates).not.toHaveBeenCalled();
  });

  /**
   * Lead 680 in production: an SEC portal print carrying ONLY a delivery address — no
   * sender, no company-name field, no portal block. The resolver auto-linked it at 0.88 and
   * wrote the sentence below, quoting the line it read. This panel discarded that and
   * printed the reason-code phrase, so the rep was told a decision existed but never what
   * the machine had actually read, and could not check it against the document.
   */
  it("shows the engine's own sentence, quoting the document, not the category phrase", async () => {
    renderPanel(
      <ClientIdentityPanel
        lead={lead({
          customerId: 42,
          customerName: 'Saudi Electricity Company',
          customerMatchStatus: 'AUTO_MATCHED',
          customerMatchReasonCode: 'NAME_IN_DOCUMENT',
          customerMatchConfidence: 0.88,
          customerMatchExplanation:
            '"Saudi Electricity Company" appears in the delivery address: "Saudi Electricity Company-DAMMAM".',
        })}
      />,
    );

    expect(await screen.findByText(/appears in the delivery address: "Saudi Electricity Company-DAMMAM"/))
      .toBeInTheDocument();
    expect(screen.getByText('Why')).toBeInTheDocument();
    expect(screen.queryByText(/Matched because/)).not.toBeInTheDocument();
  });

  /**
   * NAME_IN_DOCUMENT and HUMAN_RESOLVED had no case in the reason-code switch, so a lead
   * matched either way showed a name, a percentage and silence.
   */
  it('explains a colleague’s own decision instead of showing silence', async () => {
    renderPanel(
      <ClientIdentityPanel
        lead={lead({
          customerId: 42,
          customerName: 'Marafiq',
          customerMatchStatus: 'CONFIRMED',
          customerMatchReasonCode: 'HUMAN_RESOLVED',
        })}
      />,
    );

    expect(await screen.findByText(/a colleague already chose this customer/)).toBeInTheDocument();
  });

  /**
   * THE UNDO THAT COULD NOT WORK. "Change client" was offered on every resolved lead, but
   * the server's re-pointing guard refuses to move the customer once the lead has become an
   * RFQ and answers 409 — which the shared error layer renders as "This changed while you
   * were working… refresh and reapply". Nothing had changed, so refreshing and retrying
   * returned the identical refusal, forever. DecidePage already hid its picker once a
   * customer was set; the two screens contradicted each other on the same lead.
   */
  it('offers no undo it cannot honour on a confirmed lead, and says instead how to correct it', async () => {
    renderPanel(
      <ClientIdentityPanel
        lead={lead({
          customerId: 42,
          customerName: 'Saudi Electricity Company',
          customerMatchStatus: 'CONFIRMED',
          customerMatchReasonCode: 'SENDER_EMAIL_EXACT',
        })}
      />,
    );

    expect(await screen.findByRole('link', { name: 'Saudi Electricity Company' })).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /Change client/i })).not.toBeInTheDocument();
    expect(screen.getByText(/locked now that the inquiry is confirmed/i)).toBeInTheDocument();
    expect(screen.getByText(/reject this inquiry and raise it again/i)).toBeInTheDocument();
    expect(screen.getByText(/correct the client there/i)).toBeInTheDocument();
  });

  it('keeps the client choosable while the lead has no customer yet', async () => {
    renderPanel(<ClientIdentityPanel lead={lead()} />);

    expect(await screen.findByText('No client linked yet')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Choose the customer' })).toBeEnabled();
    expect(screen.queryByText(/locked now that the inquiry is confirmed/i)).not.toBeInTheDocument();
  });
});
