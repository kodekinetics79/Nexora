import { beforeEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
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

/** What the GRID last asked for (the pageSize-1 unfiltered total read is not the grid's). */
const lastGrid = () =>
  getAll.mock.calls.map((call) => call[0] as { view?: unknown; status?: unknown; pageSize?: number }).filter((p) => p.pageSize !== 1).at(-1);
const lastGridView = (): unknown => lastGrid()?.view;

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
  it('requests the "queue" view by default: advanced inquiries stay, ones that became an RFQ leave', async () => {
    renderPage();
    await waitFor(() => expect(lastGridView()).toBe('queue'));
  });

  it('lets a typed search reach an inquiry that already became an RFQ', async () => {
    renderPage();
    await waitFor(() => expect(lastGridView()).toBe('queue'));
    fireEvent.change(screen.getByPlaceholderText(/search by serial/i), { target: { value: '6000000028' } });
    await waitFor(() => expect(lastGridView()).toBe('open'));
  });

  it('offers "Not opened yet" for the old behaviour in the Status header, and widens back in one click', async () => {
    renderPage();
    await waitFor(() => expect(lastGridView()).toBe('queue'));

    const statusFilter = screen.getByRole('button', { name: 'Filter by status' });
    fireEvent.click(statusFilter);
    fireEvent.click(within(screen.getByRole('menu')).getByRole('menuitem', { name: /^not opened yet/i }));
    // Within the normal queue, not the server's old no-view default.
    await waitFor(() => expect(lastGrid()).toEqual(expect.objectContaining({ view: 'queue', status: 'none' })));
    expect(statusFilter).toHaveAttribute('aria-pressed', 'true');
    // The empty state names the filter that emptied the list, not "no inquiries yet".
    expect(await screen.findByText(/nothing is waiting to be looked at/i)).toBeInTheDocument();

    fireEvent.click(screen.getByRole('button', { name: /show inquiries in progress/i }));
    await waitFor(() => expect(lastGrid()?.status).toBeUndefined());
    expect(lastGridView()).toBe('queue');
  });

  it('counts every live inquiry ("open") to tell a true zero from a worked-through queue', async () => {
    renderPage();
    await waitFor(() => expect(getAll).toHaveBeenCalledWith(expect.objectContaining({ pageSize: 1, view: 'open' })));
    expect(getAll).not.toHaveBeenCalledWith(expect.objectContaining({ pageSize: 1, view: 'queue' }));
  });

  it('says "Nothing to decide" and offers the RFQs when every live inquiry is already an RFQ', async () => {
    renderPage();
    expect(await screen.findByText('Nothing to decide')).toBeInTheDocument();
    expect(screen.queryByText('No inquiries yet')).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /connect the mailbox/i })).not.toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Open RFQs' })).toBeInTheDocument();
  });

  it('keeps "No inquiries yet" when nothing is live at all', async () => {
    getAll.mockImplementation((params: { pageSize?: number }) => Promise.resolve(
      { items: [], totalCount: 0, pageNumber: 1, pageSize: params.pageSize ?? 10 },
    ));
    renderPage();
    expect(await screen.findByText('No inquiries yet')).toBeInTheDocument();
    expect(screen.queryByText('Nothing to decide')).not.toBeInTheDocument();
  });

  it('goes back to the first page when the search changes', async () => {
    const rows = Array.from({ length: 25 }, (_, i) => ({ id: i + 1, rfqno: `RFQ-${i + 1}` }));
    getAll.mockImplementation((params: { pageSize?: number }) => Promise.resolve(
      params.pageSize === 1
        ? { items: [], totalCount: 60, pageNumber: 1, pageSize: 1 }
        : { items: rows, totalCount: 60, pageNumber: 1, pageSize: 25 },
    ));
    renderPage();
    const next = await screen.findByRole('button', { name: /go to next page/i });
    await waitFor(() => expect(next).toBeEnabled());
    fireEvent.click(next);
    await waitFor(() => expect(getAll).toHaveBeenCalledWith(expect.objectContaining({ pageNumber: 2, pageSize: 25 })));

    getAll.mockClear();
    fireEvent.change(screen.getByPlaceholderText(/search by serial/i), { target: { value: '6000000028' } });
    await waitFor(() => expect(getAll).toHaveBeenCalledWith(expect.objectContaining({ search: '6000000028', pageSize: 25 })));
    const searched = getAll.mock.calls
      .map((call) => call[0] as { search?: string; pageNumber?: number; pageSize?: number })
      .filter((p) => p.search === '6000000028' && p.pageSize === 25);
    expect(searched.every((p) => p.pageNumber === 1)).toBe(true);
  });

  it('seeds the search from ?search= on the URL', async () => {
    renderPage('/procurement/leads/all?search=REF-42');
    await waitFor(() => expect(getAll).toHaveBeenCalledWith(expect.objectContaining({ search: 'REF-42', view: 'open' })));
    expect(screen.getByPlaceholderText(/search by serial/i)).toHaveValue('REF-42');
  });

  it('leaves a queue named on the URL alone and narrows it by nothing the reader did not pick', async () => {
    renderPage('/procurement/leads/all?view=revisions');
    await waitFor(() => expect(lastGridView()).toBe('revisions'));
    expect(lastGrid()?.status).toBeUndefined();
    expect(screen.queryByRole('button', { name: /not opened yet/i })).not.toBeInTheDocument();
  });
});
