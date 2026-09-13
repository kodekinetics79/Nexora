import { beforeEach, describe, expect, it, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router-dom';
import OutstandingLeadsPage from './OutstandingLeadsPage';

/**
 * The empty queue explained the journey as "only quoted lines become the RFQ". No line is quoted to
 * anyone at that point: the owner marks lines Quote or Skip, and the marked ones go into the RFQ.
 */

const getOutstandingLeads = vi.fn();

vi.mock('../../api/services/leadService', () => ({
  default: { getOutstandingLeads: (...a: unknown[]) => getOutstandingLeads(...a) },
  assignabilityNote: () => 'note',
}));

vi.mock('../../api/services/commercialRoutingService', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../../api/services/commercialRoutingService')>();
  return {
    ...actual,
    default: { getOwnerOptions: () => Promise.resolve([]), changeLeadOwner: vi.fn() },
  };
});

vi.mock('../../context/AuthContext', () => ({
  useAuth: () => ({
    userData: { id: 7, businessUnitId: 1, isManager: false, isSuperAdmin: false },
    hasPermission: () => true,
  }),
}));

vi.mock('notistack', () => ({ useSnackbar: () => ({ enqueueSnackbar: vi.fn() }) }));
vi.mock('./ResolveClientDialog', () => ({ default: () => null }));
vi.mock('./ClientCell', () => ({ default: () => null, clientDisplayName: () => 'Acme' }));
vi.mock('../../components/layout/ViewTabs', () => ({ default: () => null }));

beforeEach(() => {
  vi.clearAllMocks();
});

describe('the empty outstanding queue explains the journey in job words', () => {
  it('says lines marked to quote go into the RFQ, not that lines are quoted', async () => {
    getOutstandingLeads.mockResolvedValue({ items: [], totalCount: 0 });
    render(
      <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
        <MemoryRouter><OutstandingLeadsPage /></MemoryRouter>
      </QueryClientProvider>,
    );

    expect(await screen.findByText(
      'Accepted Leads appear here until assigned. The owner then chooses which lines to quote on the Decide screen; only lines marked to quote go into the RFQ.',
    )).toBeInTheDocument();
    expect(screen.queryByText(/only quoted lines/i)).toBeNull();
  });
});
