import { beforeEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { SnackbarProvider } from 'notistack';
import LeadsPage from './LeadsPage';

/**
 * "All inquiries" must contain the inquiries someone has already advanced.
 *
 * The page sent no queue view, and the server's default for no view is the untriaged inbox
 * (`LeadStatusId == null`, LeadRepository.GetLeadListAsync :185-192). Every lifecycle transition
 * stamps a status, so the list called "All" lost each inquiry the moment a rep started on it. The
 * rep qualified a lead, came back the next morning, and it was gone from the only list they knew.
 */

const getAll = vi.fn();

vi.mock('../../api/services/leadService', () => ({
  default: { getAll: (params: unknown) => getAll(params), fetchEmails: vi.fn() },
}));
vi.mock('../../api/services/decisionService', () => ({
  default: { getDecisionSummaries: vi.fn().mockResolvedValue({ summaries: {} }) },
}));
vi.mock('../../api/services/commercialRoutingService', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../../api/services/commercialRoutingService')>();
  return { ...actual, default: { getOwnerOptions: vi.fn().mockResolvedValue([]), changeLeadOwner: vi.fn(), getLeadAssignmentHistory: vi.fn().mockResolvedValue([]) } };
});
vi.mock('../../hooks/useColumnPreferences', () => ({
  default: () => ({ columnVisibilityModel: {}, onColumnVisibilityModelChange: vi.fn(), arrangeColumns: <T,>(defs: T) => defs, isLoading: false, isError: false }),
}));
vi.mock('../../components/common/ColumnPreferences', () => ({ default: () => null }));
vi.mock('../../context/AuthContext', () => ({
  // No identity: the owner filter opens on Everyone, so the queue token is the whole view.
  useAuth: () => ({ hasPermission: () => true, userData: {} }),
}));
vi.mock('react-i18next', () => ({ useTranslation: () => ({ t: (key: string) => key }) }));

/** The queue view the GRID last asked for (the pageSize-1 unfiltered total read is not the grid's). */
const lastGridView = (): unknown =>
  getAll.mock.calls.map((call) => call[0] as { view?: unknown; pageSize?: number }).filter((p) => p.pageSize !== 1).at(-1)?.view;

const renderPage = (route = '/procurement/leads/all') => {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } });
  return render(
    <MemoryRouter initialEntries={[route]}>
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
  // The tenant holds inquiries (the pageSize-1 unfiltered read says 3); the grid page is empty,
  // so the empty state must name the filter rather than say "No inquiries yet".
  getAll.mockImplementation((params: { pageSize?: number }) => Promise.resolve(
    params.pageSize === 1
      ? { items: [], totalCount: 3, pageNumber: 1, pageSize: 1 }
      : { items: [], totalCount: 0, pageNumber: 1, pageSize: 10 },
  ));
});

describe('LeadsPage — All inquiries asks for the open pipeline', () => {
  it('requests the "open" view by default, so advanced inquiries stay in the list', async () => {
    renderPage();
    await waitFor(() => expect(lastGridView()).toBe('open'));
  });

  it('offers "Untriaged only" for the old behaviour and clears it with the other filters', async () => {
    renderPage();
    await waitFor(() => expect(lastGridView()).toBe('open'));

    const toggle = screen.getByRole('button', { name: /untriaged only/i });
    fireEvent.click(toggle);
    await waitFor(() => expect(lastGridView()).toBeUndefined());
    expect(toggle).toHaveAttribute('aria-pressed', 'true');
    // The empty state names the filter that emptied the list, not "no inquiries yet".
    expect(await screen.findByText(/nothing is waiting to be looked at/i)).toBeInTheDocument();

    fireEvent.click(screen.getByRole('button', { name: /show inquiries in progress/i }));
    await waitFor(() => expect(lastGridView()).toBe('open'));
  });

  it('leaves a queue named on the URL alone and hides the toggle there', async () => {
    renderPage('/procurement/leads/all?view=revisions');
    await waitFor(() => expect(lastGridView()).toBe('revisions'));
    expect(screen.queryByRole('button', { name: /untriaged only/i })).not.toBeInTheDocument();
  });
});
