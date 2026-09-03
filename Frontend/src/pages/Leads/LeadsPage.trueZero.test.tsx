import { beforeEach, describe, expect, it, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { SnackbarProvider } from 'notistack';
import LeadsPage from './LeadsPage';

/**
 * A tenant with NO inquiries, read by the people the list opens narrowed for.
 *
 * The grid opens on a working set — "Unassigned" for a manager, "Mine" for a rep — and the
 * empty-state copy keyed only on whether a filter was active. So a brand-new tenant's manager
 * read "Every inquiry here already has an owner" over a list that had nothing to own, and the
 * banner for a reader without a rep profile pointed at "Sales > Rep directory", a menu path that
 * does not exist in the rail. Both told a day-one user to look for something that was not there.
 */

const getAll = vi.fn();
const getOwnerOptions = vi.fn();
const hasPermission = vi.fn();
const authUser: { id?: number; isManager?: boolean; roleName?: string } = {};

vi.mock('../../api/services/leadService', () => ({
  default: {
    getAll: (params: unknown) => getAll(params),
    fetchEmails: vi.fn(),
  },
}));

vi.mock('../../api/services/decisionService', () => ({
  default: { getDecisionSummaries: vi.fn().mockResolvedValue({ summaries: {} }) },
}));

vi.mock('../../api/services/commercialRoutingService', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../../api/services/commercialRoutingService')>();
  return {
    ...actual,
    default: {
      getOwnerOptions: () => getOwnerOptions(),
      changeLeadOwner: vi.fn(),
      getLeadAssignmentHistory: vi.fn().mockResolvedValue([]),
    },
  };
});

vi.mock('../../hooks/useColumnPreferences', () => ({
  default: () => ({
    columnVisibilityModel: {},
    onColumnVisibilityModelChange: vi.fn(),
    arrangeColumns: <T,>(defs: T) => defs,
    isLoading: false,
    isError: false,
  }),
}));

vi.mock('../../components/common/ColumnPreferences', () => ({ default: () => null }));

vi.mock('../../context/AuthContext', () => ({
  useAuth: () => ({ hasPermission, userData: authUser }),
}));

const navigate = vi.fn();
vi.mock('react-router-dom', async (importOriginal) => {
  const actual = await importOriginal<typeof import('react-router-dom')>();
  return { ...actual, useNavigate: () => navigate };
});

vi.mock('react-i18next', () => ({
  useTranslation: () => ({ t: (key: string) => key }),
}));

const page = (totalCount: number) => ({ items: [], totalCount, pageNumber: 1, pageSize: 10 });

/** The page asks twice: the page it shows, and a pageSize-1 read for the unfiltered total. */
const tenantHolds = (unfilteredTotal: number) => getAll.mockImplementation(
  (params: { pageSize?: number }) => Promise.resolve(params.pageSize === 1 ? page(unfilteredTotal) : page(0)),
);

const renderPage = () => {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } });
  return render(
    <MemoryRouter initialEntries={['/procurement/leads/all']}>
      <SnackbarProvider>
        <QueryClientProvider client={client}>
          <LeadsPage />
        </QueryClientProvider>
      </SnackbarProvider>
    </MemoryRouter>,
  );
};

beforeEach(() => {
  vi.clearAllMocks();
  hasPermission.mockReturnValue(true);
  getOwnerOptions.mockResolvedValue([]);
  authUser.id = 7;
  authUser.isManager = true;
  authUser.roleName = 'Sales Manager';
});

describe('LeadsPage — a tenant with no inquiries at all', () => {
  it('says "No inquiries yet" to a manager even though the list opened narrowed to Unassigned', async () => {
    tenantHolds(0);
    renderPage();

    expect(await screen.findByText(/no inquiries yet/i)).toBeInTheDocument();
    expect(screen.queryByText(/every inquiry here already has an owner/i)).not.toBeInTheDocument();
    expect(screen.getByRole('button', { name: /upload a document/i })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: /connect the mailbox/i })).toBeInTheDocument();
  });

  it('still says the filter emptied the list when the tenant does hold inquiries', async () => {
    tenantHolds(3);
    renderPage();

    expect(await screen.findByText(/every inquiry here already has an owner/i)).toBeInTheDocument();
    expect(screen.queryByText(/no inquiries yet/i)).not.toBeInTheDocument();
  });

  it('tells a reader without mailbox access who to ask, instead of a button that lands on Access Denied', async () => {
    tenantHolds(0);
    hasPermission.mockImplementation((module: string) => module !== 'Email & SMTP');
    renderPage();

    await screen.findByText(/no inquiries yet/i);
    expect(screen.queryByRole('button', { name: /connect the mailbox/i })).not.toBeInTheDocument();
    expect(screen.getByText(/ask your administrator to connect a mailbox under setup > email inboxes/i)).toBeInTheDocument();
  });
});

describe('LeadsPage — the "no rep profile" banner', () => {
  it('names the menu path that actually exists and gives a manager the link', async () => {
    tenantHolds(0);
    renderPage();

    expect(await screen.findByText(/team & exceptions > sales reps/i)).toBeInTheDocument();
    expect(screen.queryByText(/rep directory/i)).not.toBeInTheDocument();
    screen.getByRole('button', { name: /open sales reps/i }).click();
    expect(navigate).toHaveBeenCalledWith('/sales/reps');
  });

  it('gives a rep the menu path only — the directory is a manager screen', async () => {
    tenantHolds(0);
    authUser.isManager = false;
    authUser.roleName = 'Sales Rep';
    renderPage();

    expect(await screen.findByText(/team & exceptions > sales reps/i)).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /open sales reps/i })).not.toBeInTheDocument();
  });
});
