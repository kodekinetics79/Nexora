import { beforeEach, describe, expect, it, vi } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { ReactNode } from 'react';
import DeadlineBoardPage from './DeadlineBoardPage';
import type { LeadResponseDTO } from '../../api/services/leadService';

const getAllLeads = vi.fn();
const getClientCandidates = vi.fn();
const linkClient = vi.fn();
const resolveClients = vi.fn();

vi.mock('../../api/services/leadService', () => ({
  default: {
    getAll: (...args: unknown[]) => getAllLeads(...args),
    getClientCandidates: (...args: unknown[]) => getClientCandidates(...args),
    linkClient: (...args: unknown[]) => linkClient(...args),
    resolveClients: (...args: unknown[]) => resolveClients(...args),
    getById: vi.fn(),
  },
}));
vi.mock('../../api/services/customerService', () => ({
  default: { getAll: vi.fn().mockResolvedValue({ items: [], totalCount: 0, pageNumber: 1, pageSize: 10 }), create: vi.fn() },
}));
vi.mock('../../api/services/contactService', () => ({
  default: { getByCustomer: vi.fn().mockResolvedValue([]) },
}));

const hasPermission = vi.fn();
const userData = { isManager: false as boolean };
vi.mock('../../context/AuthContext', () => ({
  useAuth: () => ({
    hasPermission: (module: string, action?: string) => hasPermission(module, action),
    userData,
  }),
}));

const toastSuccess = vi.fn();
vi.mock('react-hot-toast', () => ({
  toast: { success: (...a: unknown[]) => toastSuccess(...a), error: vi.fn() },
  default: { success: (...a: unknown[]) => toastSuccess(...a), error: vi.fn() },
}));

const navigate = vi.fn();
vi.mock('react-router-dom', async (importOriginal) => {
  const actual = await importOriginal<typeof import('react-router-dom')>();
  return { ...actual, useNavigate: () => navigate };
});

/** An enquiry with a real deadline and no client record — the live tenant's common case. */
const unlinkedLead = (over: Partial<LeadResponseDTO> = {}): LeadResponseDTO => ({
  id: 488,
  customerMatchStatus: 'UNRESOLVED',
  rfqno: 'FC-2026-0088',
  buyersName: 'Jr. Steven Scott',
  customerCompanyNameExtracted: 'Fulton County Government',
  clientemail: 'bids@fultoncountyga.gov',
  leadSource: 'Email',
  recDate: '2026-08-01T00:00:00Z',
  bidClosingDate: '2099-01-01T00:00:00Z',
  emailSource: 'inbox',
  status: 'New',
  isAccepted: false,
  isRejected: false,
  aiconfidence: 0.8,
  itemCount: 12,
  reviewVersion: 1,
  requiresCommercialReview: false,
  commercialFactsVerified: false,
  currentRevisionNumber: 1,
  businessUnitId: 7,
  lifecycleVersion: 1,
  leadItems: [],
  ...over,
});

const wrapper = ({ children }: { children: ReactNode }) => {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return <QueryClientProvider client={client}>{children}</QueryClientProvider>;
};

beforeEach(() => {
  vi.clearAllMocks();
  hasPermission.mockReturnValue(true);
  userData.isManager = false;
  getClientCandidates.mockResolvedValue([]);
  linkClient.mockResolvedValue(unlinkedLead({ customerId: 42 }));
  resolveClients.mockResolvedValue({
    examined: 31, autoMatched: 0, suggested: 0, ambiguous: 0, unresolved: 31, failed: 0,
  });
  getAllLeads.mockResolvedValue({
    items: [unlinkedLead()], totalCount: 1, pageNumber: 1, pageSize: 500,
  });
});

/**
 * The board is where a rep sees what closes when. An enquiry with no client record cannot be
 * qualified, cannot become an RFQ and therefore cannot be quoted — so on this board an
 * unlinked row is the reason that deadline gets missed, not a cosmetic gap.
 *
 * It used to render as a greyed-out caption reading "Not linked to a client record": the
 * blocker was named and nothing was offered. These tests pin it as a control.
 */
describe('DeadlineBoardPage — the unresolved client is work, not a label', () => {
  it('offers to link the client from the row itself', async () => {
    render(<DeadlineBoardPage />, { wrapper });

    const link = await screen.findByRole('button', { name: /Not linked to a client record — link it/i });
    expect(link).toBeInTheDocument();

    link.click();

    // The resolve dialog opens on THIS enquiry, pre-filled from what the document said, so
    // the operator is not retyping a company name the enquiry already carried.
    expect(await screen.findByRole('dialog')).toBeInTheDocument();
    await waitFor(() => expect(getClientCandidates).toHaveBeenCalledWith(488));
    expect(screen.getByDisplayValue('Fulton County Government')).toBeInTheDocument();
  });

  it('stays a plain label for someone who may not edit leads', async () => {
    hasPermission.mockImplementation((module: string, action?: string) =>
      !(module === 'Leads' && action === 'edit'));
    render(<DeadlineBoardPage />, { wrapper });

    expect(await screen.findByText('Not linked to a client record')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /link it/i })).not.toBeInTheDocument();
  });

  it('says nothing about linking once the enquiry has a client', async () => {
    getAllLeads.mockResolvedValue({
      items: [unlinkedLead({ customerId: 42, customerName: 'Fulton County Government' })],
      totalCount: 1, pageNumber: 1, pageSize: 500,
    });
    render(<DeadlineBoardPage />, { wrapper });

    expect(await screen.findByText('FC-2026-0088')).toBeInTheDocument();
    expect(screen.queryByText(/Not linked to a client record/i)).not.toBeInTheDocument();
  });

  it('states how much work the missing clients are actually blocking', async () => {
    getAllLeads.mockResolvedValue({
      items: [unlinkedLead(), unlinkedLead({ id: 489, customerId: 42, itemCount: 3 })],
      totalCount: 2, pageNumber: 1, pageSize: 500,
    });
    render(<DeadlineBoardPage />, { wrapper });

    // One of two open enquiries, and the twelve lines it carries — not a vague warning.
    expect(await screen.findByText(/1 of 2 open enquiry, carrying\s*12 lines, are not linked to a client record/i))
      .toBeInTheDocument();
  });
});

/**
 * `POST /api/Lead/resolve-clients` shipped with the client-identity release and had no caller
 * anywhere in the product, so the one action that can clear an accumulated backlog was
 * unreachable. These pin it as reachable, correctly gated, and honest about what it did.
 */
describe('DeadlineBoardPage — the tenant-wide re-run', () => {
  it('is offered to a manager and reports the real counts, including none', async () => {
    userData.isManager = true;
    render(<DeadlineBoardPage />, { wrapper });

    const run = await screen.findByRole('button', { name: /Match clients automatically/i });
    run.click();

    await waitFor(() => expect(resolveClients).toHaveBeenCalledTimes(1));
    // "Matched 0 of 31" is the answer that tells an operator the customer record does not
    // exist yet — a cheerful "done" would hide exactly the fact they need.
    await waitFor(() => expect(toastSuccess).toHaveBeenCalledWith(
      expect.stringMatching(/Checked 31 unresolved enquiries and matched none/i),
      expect.anything(),
    ));
  });

  it('is not offered to a non-manager, whom the server would refuse', async () => {
    userData.isManager = false;
    render(<DeadlineBoardPage />, { wrapper });

    expect(await screen.findByText('FC-2026-0088')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /Match clients automatically/i })).not.toBeInTheDocument();
  });

  it('is not offered when every enquiry already has a client', async () => {
    userData.isManager = true;
    getAllLeads.mockResolvedValue({
      items: [unlinkedLead({ customerId: 42, customerName: 'Fulton County Government' })],
      totalCount: 1, pageNumber: 1, pageSize: 500,
    });
    render(<DeadlineBoardPage />, { wrapper });

    expect(await screen.findByText('FC-2026-0088')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /Match clients automatically/i })).not.toBeInTheDocument();
  });
});
