import { beforeEach, describe, expect, it, vi } from 'vitest';
import { render, screen, waitFor, within } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { ReactNode } from 'react';
import ResolveClientDialog, { buildClientReviewPayload } from './ResolveClientDialog';
import type { LeadResponseDTO } from '../../api/services/leadService';

const getById = vi.fn();
const getClientCandidates = vi.fn();
const submitReview = vi.fn();
const getAll = vi.fn();
const getByCustomer = vi.fn();

vi.mock('../../api/services/leadService', () => ({
  default: {
    getById: (...args: unknown[]) => getById(...args),
    getClientCandidates: (...args: unknown[]) => getClientCandidates(...args),
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
  customerMatchStatus: 'AMBIGUOUS',
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
  itemCount: 2,
  reviewVersion: 3,
  requiresCommercialReview: false,
  commercialFactsVerified: false,
  currentRevisionNumber: 1,
  businessUnitId: 80101,
  lifecycleVersion: 1,
  leadItems: [
    { id: 900, quantity: 4, aiconfidence: 0.9, productShortName: 'Valve', currency: 'SAR' },
    // A non-positive quantity is an extraction defect, not a reason to block a
    // client link — see the payload test below.
    { id: 901, quantity: 0, aiconfidence: 0.4, productShortName: 'Gasket' },
  ],
  ...over,
});

const CANDIDATES = [
  { rank: 2, customerId: 43, customerName: 'SEC Distribution Co.', confidence: 0.74, reasonCode: 'NAME_FUZZY' },
  { rank: 1, customerId: 42, customerName: 'Saudi Electricity Company', confidence: 0.95, reasonCode: 'SENDER_DOMAIN' },
];

const wrapper = ({ children }: { children: ReactNode }) => {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return <QueryClientProvider client={client}>{children}</QueryClientProvider>;
};

beforeEach(() => {
  vi.clearAllMocks();
  getById.mockResolvedValue(lead());
  getClientCandidates.mockResolvedValue(CANDIDATES);
  getAll.mockResolvedValue({ items: [], totalCount: 0, pageNumber: 1, pageSize: 10 });
  getByCustomer.mockResolvedValue([]);
  submitReview.mockResolvedValue(lead({ customerId: 42 }));
});

describe('ResolveClientDialog', () => {
  it('ranks the machine candidates best-first and shows the evidence behind each', async () => {
    render(
      <ResolveClientDialog open leadId={501} lead={lead()} onClose={() => {}} />,
      { wrapper },
    );

    expect(await screen.findByText('Saudi Electricity Company')).toBeInTheDocument();

    const options = screen.getAllByRole('radio');
    // Rank 1 first, even though the payload listed rank 2 first.
    expect(options[0]).toHaveAttribute('value', '42');
    expect(options[1]).toHaveAttribute('value', '43');

    // Every candidate carries the reason it was proposed — a rep must be able to
    // judge the suggestion, not just accept it.
    expect(screen.getByText("The sender's email domain belongs to this client")).toBeInTheDocument();
    expect(screen.getByText('The company name on the document is a close match')).toBeInTheDocument();
    expect(screen.getByText('95% confident')).toBeInTheDocument();
    expect(screen.getByText('74% confident')).toBeInTheDocument();
  });

  it('offers an explicit "leave unresolved" escape that writes nothing', async () => {
    const onClose = vi.fn();
    render(<ResolveClientDialog open leadId={501} lead={lead()} onClose={onClose} />, { wrapper });

    const escape = await screen.findByRole('button', { name: /None of these — leave unresolved/i });
    escape.click();

    await waitFor(() => expect(onClose).toHaveBeenCalled());
    expect(submitReview).not.toHaveBeenCalled();
  });

  it('offers no way to create a customer — a wrong client is worse than an unresolved one', async () => {
    render(<ResolveClientDialog open leadId={501} lead={lead()} onClose={() => {}} />, { wrapper });

    await screen.findByText('Saudi Electricity Company');
    const dialog = screen.getByRole('dialog');
    expect(within(dialog).queryByRole('button', { name: /create|add new|new client|new customer/i })).not.toBeInTheDocument();
    expect(within(dialog).queryByText(/create a (new )?(client|customer)/i)).not.toBeInTheDocument();
  });

  it('cannot confirm until a client is actually chosen', async () => {
    render(<ResolveClientDialog open leadId={501} lead={lead()} onClose={() => {}} />, { wrapper });

    const confirm = await screen.findByRole('button', { name: /Confirm client/i });
    expect(confirm).toBeDisabled();

    (await screen.findAllByRole('radio'))[0].click();
    await waitFor(() => expect(confirm).toBeEnabled());
  });

  it('writes the chosen client through the review endpoint', async () => {
    const onResolved = vi.fn();
    render(
      <ResolveClientDialog open leadId={501} lead={lead()} onClose={() => {}} onResolved={onResolved} />,
      { wrapper },
    );

    (await screen.findAllByRole('radio'))[0].click();
    (await screen.findByRole('button', { name: /Confirm client/i })).click();

    await waitFor(() => expect(submitReview).toHaveBeenCalledTimes(1));
    expect(submitReview.mock.calls[0][1].header.customerId).toBe(42);
    await waitFor(() => expect(onResolved).toHaveBeenCalledWith(42));
  });

  it('reports the choice instead of writing it in deferred mode', async () => {
    const onSelect = vi.fn();
    render(
      <ResolveClientDialog open leadId={501} lead={lead()} onClose={() => {}} onSelect={onSelect} />,
      { wrapper },
    );

    (await screen.findAllByRole('radio'))[0].click();
    (await screen.findByRole('button', { name: /Confirm client/i })).click();

    await waitFor(() => expect(onSelect).toHaveBeenCalledWith(
      expect.objectContaining({ customerId: 42, customerName: 'Saudi Electricity Company' }),
    ));
    expect(submitReview).not.toHaveBeenCalled();
    // Deferred hosts already hold the lead; the dialog must not refetch it.
    expect(getById).not.toHaveBeenCalled();
  });
});

describe('buildClientReviewPayload', () => {
  it('echoes every stored line item back, because the endpoint deletes anything absent', () => {
    const payload = buildClientReviewPayload(lead(), { customerId: 42 });
    expect(payload.items.map((i) => i.id)).toEqual([900, 901]);
    expect(payload.action).toBe('save');
    expect(payload.header).toEqual({ customerId: 42 });
  });

  it('omits a non-positive quantity, which the server preserves, instead of failing the link', () => {
    const payload = buildClientReviewPayload(lead(), { customerId: 42 });
    expect(payload.items[0].quantity).toBe(4);
    expect(payload.items[1].quantity).toBeUndefined();
  });

  it('sends a contact only when one was chosen', () => {
    expect(buildClientReviewPayload(lead(), { customerId: 42, contactId: null }).header.contactId).toBeUndefined();
    expect(buildClientReviewPayload(lead(), { customerId: 42, contactId: 7 }).header.contactId).toBe(7);
  });

  it('never sends the review version the DTO rejects', () => {
    expect(buildClientReviewPayload(lead({ reviewVersion: 0 }), { customerId: 42 }).expectedVersion).toBe(1);
    expect(buildClientReviewPayload(lead({ reviewVersion: 5 }), { customerId: 42 }).expectedVersion).toBe(5);
  });
});
