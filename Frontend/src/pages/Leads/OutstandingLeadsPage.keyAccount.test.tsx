import { beforeEach, describe, expect, it, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router-dom';
import OutstandingLeadsPage from './OutstandingLeadsPage';

/**
 * The grid may not assert a commercial fact it did not compute.
 *
 * Rows used to be stamped KEY ACCOUNT — in the same red the grid uses for overdue work — whenever
 * the buyer name contained "aramco" or "sec". That badge came from no customer record, so no
 * administrator could grant it, remove it, or explain it: Secure Piping Supplies got the label and
 * a genuinely strategic customer named otherwise did not, and a rep working the queue top-down was
 * being told to prioritise on a substring.
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

const lead = (id: number, buyersName: string) => ({
  id,
  rfqno: `RFQ-${id}`,
  buyersName,
  clientemail: `buyer${id}@example.test`,
  recDate: '2026-08-01T00:00:00Z',
  acceptedDate: '2026-08-01T00:00:00Z',
  leadSource: 'Email',
  businessUnitId: 1,
  itemCount: 3,
  assignedToId: null,
  assignedToFullName: null,
  assignmentVersion: 1,
});

const renderPage = () => {
  const client = new QueryClient({
    defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
  });
  return render(
    <QueryClientProvider client={client}>
      <MemoryRouter><OutstandingLeadsPage /></MemoryRouter>
    </QueryClientProvider>,
  );
};

beforeEach(() => {
  vi.clearAllMocks();
});

describe('the outstanding queue never labels a buyer it has no field for', () => {
  it('does not call a buyer a key account because of what their name contains', async () => {
    getOutstandingLeads.mockResolvedValue({
      items: [lead(801, 'Secure Piping Supplies'), lead(802, 'Second Industrial Co')],
      totalCount: 2,
    });

    renderPage();

    // Both buyer cells are on screen, so the absence below is about the badge and not about an
    // unrendered column.
    expect(await screen.findByText('Secure Piping Supplies')).toBeInTheDocument();
    expect(screen.getByText('Second Industrial Co')).toBeInTheDocument();
    expect(screen.queryByText('KEY ACCOUNT')).toBeNull();
  });

  it('does not stamp one on a name the heuristic happened to get right either', async () => {
    // Aramco may well be a key account. The grid still has no field that says so, and a badge
    // that is right by coincidence cannot be turned off when it is wrong.
    getOutstandingLeads.mockResolvedValue({
      items: [lead(803, 'Aramco Overseas Company')],
      totalCount: 1,
    });

    renderPage();

    expect(await screen.findByText('Aramco Overseas Company')).toBeInTheDocument();
    expect(screen.queryByText('KEY ACCOUNT')).toBeNull();
  });
});
