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

    // A dead end is the failure mode being fixed: there is always a way out.
    expect(screen.getByRole('button', { name: /Find client/i })).toBeEnabled();
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

  it('names the suggestion, its confidence and its evidence', async () => {
    renderPanel(<ClientIdentityPanel lead={suggested} />);

    expect(await screen.findByText('Saudi Electricity Company')).toBeInTheDocument();
    expect(screen.getByText(/\(95%\)/)).toBeInTheDocument();
    expect(screen.getByText(/Matched because the sender's email domain belongs to this client\./)).toBeInTheDocument();
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
    expect(screen.getByText(/Matched because the sender's email address is on file/)).toBeInTheDocument();
    expect(screen.getByRole('button', { name: /Change client/i })).toBeInTheDocument();
    // A resolved lead needs no suggestions.
    expect(getClientCandidates).not.toHaveBeenCalled();
  });
});
