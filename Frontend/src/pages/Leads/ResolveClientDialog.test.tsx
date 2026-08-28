import { beforeEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { ReactNode } from 'react';
import ResolveClientDialog from './ResolveClientDialog';
import type { LeadResponseDTO } from '../../api/services/leadService';

const getById = vi.fn();
const getClientCandidates = vi.fn();
const linkClient = vi.fn();
const submitReview = vi.fn();
const getAll = vi.fn();
const createCustomer = vi.fn();
const getByCustomer = vi.fn();
const testAccess = vi.hoisted(() => ({ denied: new Set<string>(), check: vi.fn() }));

vi.mock('../../context/AuthContext', () => ({
  useAuth: () => ({
    hasPermission: (moduleName: string, action = 'view') => {
      testAccess.check(moduleName, action);
      return !testAccess.denied.has(`${moduleName}:${action}`);
    },
  }),
}));

vi.mock('../../api/services/leadService', () => ({
  default: {
    getById: (...args: unknown[]) => getById(...args),
    getClientCandidates: (...args: unknown[]) => getClientCandidates(...args),
    linkClient: (...args: unknown[]) => linkClient(...args),
  },
}));
vi.mock('../../api/services/extractionReviewService', () => ({
  default: { submitReview: (...args: unknown[]) => submitReview(...args) },
}));
vi.mock('../../api/services/customerService', () => ({
  default: {
    getAll: (...args: unknown[]) => getAll(...args),
    create: (...args: unknown[]) => createCustomer(...args),
  },
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
  testAccess.denied.clear();
  getById.mockResolvedValue(lead());
  getClientCandidates.mockResolvedValue(CANDIDATES);
  getAll.mockResolvedValue({ items: [], totalCount: 0, pageNumber: 1, pageSize: 10 });
  getByCustomer.mockResolvedValue([]);
  linkClient.mockResolvedValue(lead({ customerId: 42 }));
  createCustomer.mockResolvedValue({ id: 77, name: 'Fulton County Government' });
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
    expect(linkClient).not.toHaveBeenCalled();
  });

  it('cannot confirm until a client is actually chosen', async () => {
    render(<ResolveClientDialog open leadId={501} lead={lead()} onClose={() => {}} />, { wrapper });

    const confirm = await screen.findByRole('button', { name: /Confirm client/i });
    expect(confirm).toBeDisabled();

    (await screen.findAllByRole('radio'))[0].click();
    await waitFor(() => expect(confirm).toBeEnabled());
  });

  /**
   * The regression this whole change exists for.
   *
   * The client link used to be written through `extractionReviewService.submitReview`,
   * and the backend refuses that endpoint for any lead whose extraction already
   * succeeded — which, on the happy path, is every lead. Linking must go through the
   * dedicated client endpoint, which has no extraction-review preconditions.
   */
  it('links the client through the dedicated client endpoint, never through extraction review', async () => {
    const onResolved = vi.fn();
    render(
      <ResolveClientDialog open leadId={501} lead={lead()} onClose={() => {}} onResolved={onResolved} />,
      { wrapper },
    );

    (await screen.findAllByRole('radio'))[0].click();
    (await screen.findByRole('button', { name: /Confirm client/i })).click();

    await waitFor(() => expect(linkClient).toHaveBeenCalledTimes(1));
    expect(linkClient.mock.calls[0][0]).toBe(501);
    expect(linkClient.mock.calls[0][1]).toEqual({ customerId: 42, contactId: null });
    expect(submitReview).not.toHaveBeenCalled();
    await waitFor(() => expect(onResolved).toHaveBeenCalledWith(42));
  });

  /**
   * The dialog used to block Confirm on a full `getById` of the lead, because the review
   * payload had to echo every stored line item back or the server would delete them. The
   * dedicated endpoint writes two scalars, so the dialog neither needs the lead nor may
   * be held hostage by a failure to load it.
   */
  it('does not need the whole lead in order to link a client', async () => {
    getById.mockRejectedValue(new Error('lead detail is down'));
    render(<ResolveClientDialog open leadId={501} lead={lead()} onClose={() => {}} />, { wrapper });

    (await screen.findAllByRole('radio'))[0].click();
    const confirm = await screen.findByRole('button', { name: /Confirm client/i });
    await waitFor(() => expect(confirm).toBeEnabled());
    confirm.click();

    await waitFor(() => expect(linkClient).toHaveBeenCalledTimes(1));
    expect(getById).not.toHaveBeenCalled();
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
    expect(linkClient).not.toHaveBeenCalled();
    expect(submitReview).not.toHaveBeenCalled();
    expect(getById).not.toHaveBeenCalled();
  });

  /**
   * A buyer with no customer record is the single most common reason an enquiry is
   * stranded, so the create path is the exit from that state and has to be covered.
   *
   * The previous test in this slot asserted the OPPOSITE — "offers no way to create a
   * customer" — and had been passing vacuously for some time: it rendered without a
   * search term, and the create affordance only mounts once a search of two or more
   * characters comes back empty, so it was asserting the absence of something that was
   * never mounted. A test that cannot fail is not coverage.
   */
  describe('creating a client that does not exist yet', () => {
    const searchForNothing = async () => {
      render(
        <ResolveClientDialog
          open
          leadId={501}
          lead={lead()}
          prefill={{ name: 'Fulton County Government', email: 'bids@fultoncountyga.gov' }}
          onClose={() => {}}
        />,
        { wrapper },
      );
      await screen.findByText('Saudi Electricity Company');
      fireEvent.change(screen.getByLabelText(/Search all clients by name/i), {
        target: { value: 'Fulton County Government' },
      });
      return screen.findByRole('button', { name: /Create .*Fulton County Government.* as a new client/i });
    };

    it('offers to create the buyer only once a search has proved no such client exists', async () => {
      const create = await searchForNothing();
      expect(create).toBeInTheDocument();
      // It is offered because the search came back empty, not merely because text was typed.
      await waitFor(() => expect(getAll).toHaveBeenCalledWith(
        expect.objectContaining({ name: 'Fulton County Government' }),
      ));
    });

    it('hides customer creation when Customers:create is denied', async () => {
      testAccess.denied.add('Customers:create');
      render(
        <ResolveClientDialog
          open
          leadId={501}
          lead={lead()}
          prefill={{ name: 'Fulton County Government' }}
          onClose={() => {}}
        />,
        { wrapper },
      );

      await screen.findByText('Saudi Electricity Company');
      fireEvent.change(screen.getByLabelText(/Search all clients by name/i), {
        target: { value: 'Fulton County Government' },
      });
      await waitFor(() => expect(getAll).toHaveBeenCalledWith(
        expect.objectContaining({ name: 'Fulton County Government' }),
      ));
      expect(screen.queryByRole('button', {
        name: /Create .*Fulton County Government.* as a new client/i,
      })).not.toBeInTheDocument();
      expect(createCustomer).not.toHaveBeenCalled();
    });

    it('selects the new client but does not link it — that stays a separate, explicit act', async () => {
      const create = await searchForNothing();
      create.click();

      const submit = await screen.findByRole('button', { name: /^Create client$/i });
      submit.click();

      await waitFor(() => expect(createCustomer).toHaveBeenCalledTimes(1));
      const form = createCustomer.mock.calls[0][0] as FormData;
      expect(form.get('Name')).toBe('Fulton County Government');
      expect(form.get('ContactEmail')).toBe('bids@fultoncountyga.gov');

      // Creating a client must never link it in the same breath: a mis-typed name has to
      // be correctable before anything is attached to the enquiry.
      expect(linkClient).not.toHaveBeenCalled();
    });

    it('re-checks Customers:create at the write boundary', async () => {
      const create = await searchForNothing();
      create.click();
      const submit = await screen.findByRole('button', { name: /^Create client$/i });

      // Simulates revocation between the rendered affordance and the click event.
      testAccess.denied.add('Customers:create');
      testAccess.check.mockClear();
      submit.click();

      await waitFor(() => expect(testAccess.check).toHaveBeenCalledWith('Customers', 'create'));
      expect(createCustomer).not.toHaveBeenCalled();
    });
  });

  it('closes a retained host dialog when Lead edit authority becomes stale or revoked', async () => {
    const view = render(
      <ResolveClientDialog open leadId={501} lead={lead()} onClose={() => {}} />,
      { wrapper },
    );
    await screen.findByRole('dialog');

    testAccess.denied.add('Leads:edit');
    view.rerender(<ResolveClientDialog open leadId={501} lead={lead()} onClose={() => {}} />);

    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
  });

  it('re-checks Lead edit authority at the link boundary', async () => {
    render(<ResolveClientDialog open leadId={501} lead={lead()} onClose={() => {}} />, { wrapper });
    (await screen.findAllByRole('radio'))[0].click();
    const confirm = await screen.findByRole('button', { name: /Confirm client/i });

    // No rerender: this is the last-millisecond race after a stale/revoked snapshot is known.
    testAccess.denied.add('Leads:edit');
    testAccess.check.mockClear();
    confirm.click();

    await waitFor(() => expect(testAccess.check).toHaveBeenCalledWith('Leads', 'edit'));
    expect(linkClient).not.toHaveBeenCalled();
  });
});
