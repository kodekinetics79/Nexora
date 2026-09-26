import { beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { SnackbarProvider } from 'notistack';
import LeadsPage from './LeadsPage';

vi.setConfig({ testTimeout: 30_000 });

/**
 * "All inquiries" was selected while the Owner toggle opened on "Unassigned", so an administrator's
 * first screen read "0 inquiries · Every inquiry here already has an owner" under a tab that
 * promised everything. And the blue "You do not have a Sales Rep profile yet" notice — a fact an
 * administrator cannot act on for themselves — sat permanently above the grid.
 */

const getAll = vi.fn();
const getOwnerOptions = vi.fn();
const authUser: { id?: number; isManager?: boolean } = {};

vi.mock('../../api/services/leadService', () => ({
  default: { getAll: (params: unknown) => getAll(params), fetchEmails: vi.fn() },
}));
vi.mock('../../api/services/decisionService', () => ({
  default: { getDecisionSummaries: vi.fn().mockResolvedValue({ summaries: {} }) },
}));
vi.mock('../../api/services/commercialRoutingService', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../../api/services/commercialRoutingService')>();
  return { ...actual, default: { getOwnerOptions: () => getOwnerOptions(), changeLeadOwner: vi.fn(), getLeadAssignmentHistory: vi.fn().mockResolvedValue([]) } };
});
vi.mock('../../hooks/useColumnPreferences', () => ({
  default: () => ({ columnVisibilityModel: {}, onColumnVisibilityModelChange: vi.fn(), arrangeColumns: <T,>(defs: T) => defs, isLoading: false, isError: false }),
}));
vi.mock('../../components/common/ColumnPreferences', () => ({ default: () => null }));
vi.mock('../../context/AuthContext', () => ({
  useAuth: () => ({ hasPermission: () => true, userData: authUser }),
}));
vi.mock('react-i18next', () => ({ useTranslation: () => ({ t: (key: string) => key }) }));

/** The queue view the GRID last asked for (the pageSize-1 unfiltered total read is not the grid's). */
const lastGridView = (): unknown =>
  getAll.mock.calls.map((call) => call[0] as { view?: unknown; pageSize?: number }).filter((p) => p.pageSize !== 1).at(-1)?.view;

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

const NOTICE = /You do not have a Sales Rep profile yet/;

beforeEach(() => {
  vi.clearAllMocks();
  localStorage.clear();
  delete authUser.id;
  delete authUser.isManager;
  getAll.mockResolvedValue({ items: [], totalCount: 0, pageNumber: 1, pageSize: 10 });
  // Nobody is routing-eligible: the reader has no Sales Rep profile.
  getOwnerOptions.mockResolvedValue([]);
});

describe('LeadsPage — "All inquiries" opens on everyone', () => {
  it('asks the server for everyone’s open inquiries when a manager opens the tab', async () => {
    authUser.id = 5;
    authUser.isManager = true;
    renderPage();
    await waitFor(() => expect(lastGridView()).toBe('open'));
    expect(screen.getByRole('button', { name: 'Everyone' })).toHaveAttribute('aria-pressed', 'true');
    expect(screen.getByRole('button', { name: 'Unassigned' })).toHaveAttribute('aria-pressed', 'false');
    expect(screen.queryByText(/Every inquiry here already has an owner/)).not.toBeInTheDocument();
  });

  it('asks for everyone’s open inquiries for a rep too — "Mine" stays one click away', async () => {
    authUser.id = 2;
    authUser.isManager = false;
    renderPage();
    await waitFor(() => expect(lastGridView()).toBe('open'));
    expect(screen.getByRole('button', { name: 'Everyone' })).toHaveAttribute('aria-pressed', 'true');
    expect(screen.getByRole('button', { name: 'Mine' })).toBeEnabled();
  });
});

describe('LeadsPage — the "no Sales Rep profile" notice', () => {
  it('lets an administrator dismiss it, and remembers that', async () => {
    authUser.id = 5;
    authUser.isManager = true;
    renderPage();
    expect(await screen.findByText(NOTICE)).toBeInTheDocument();

    fireEvent.click(screen.getByRole('button', { name: 'Dismiss this notice' }));
    expect(screen.queryByText(NOTICE)).not.toBeInTheDocument();

    cleanup();
    renderPage();
    await waitFor(() => expect(lastGridView()).toBe('open'));
    expect(screen.queryByText(NOTICE)).not.toBeInTheDocument();
  });

  it('stays for a rep, who needs the sentence and the person to ask', async () => {
    authUser.id = 2;
    authUser.isManager = false;
    renderPage();
    expect(await screen.findByText(NOTICE)).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Dismiss this notice' })).not.toBeInTheDocument();
  });
});
