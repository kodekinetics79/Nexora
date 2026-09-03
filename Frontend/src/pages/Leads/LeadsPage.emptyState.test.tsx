import { beforeEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, within } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { SnackbarProvider } from 'notistack';
import LeadsPage from './LeadsPage';

/**
 * An empty inquiries grid has two completely different causes and two completely different next
 * actions, and the grid's default overlay — a bare "No rows" — makes them identical:
 *
 *  - the reader has narrowed the list to nothing, and needs the filters cleared;
 *  - nothing has ever become an inquiry, and the place to find out why is Inbound Mail.
 *
 * Reading the first as the second is how a rep concludes the product is broken; reading the
 * second as the first is how a tenant with a dead mailbox keeps clearing filters that were never
 * set. So the overlay has to say which one it is.
 */

const getAll = vi.fn();
const fetchEmails = vi.fn();
const hasPermission = vi.fn();

vi.mock('../../api/services/leadService', () => ({
  default: {
    getAll: (params: unknown) => getAll(params),
    fetchEmails: () => fetchEmails(),
  },
}));

vi.mock('../../api/services/decisionService', () => ({
  default: { getDecisionSummaries: vi.fn().mockResolvedValue({ summaries: {} }) },
}));

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
  useAuth: () => ({ hasPermission }),
}));

const navigate = vi.fn();
vi.mock('react-router-dom', async (importOriginal) => {
  const actual = await importOriginal<typeof import('react-router-dom')>();
  return { ...actual, useNavigate: () => navigate };
});

vi.mock('react-i18next', () => ({
  useTranslation: () => ({ t: (key: string) => key }),
}));

const renderPage = (route = '/procurement/leads/all') => {
  const client = new QueryClient({
    defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
  });
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

describe('LeadsPage — an empty grid states which kind of empty it is', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    hasPermission.mockReturnValue(true);
    // The page asks twice: once for the page it shows, once (pageSize 1) for the unfiltered
    // total that decides whether "nothing here" is a filter or a truly empty tenant. Both empty
    // here; the "filtered" cases below answer the total with a real number.
    getAll.mockImplementation((params: { pageSize?: number }) => Promise.resolve(
      params.pageSize === 1
        ? { items: [], totalCount: 3, pageNumber: 1, pageSize: 1 }
        : { items: [], totalCount: 0, pageNumber: 1, pageSize: 10 },
    ));
  });

  it('saysNoInquiriesYet_andPointsAtInboundMailWhenNothingIsFiltered', async () => {
    renderPage();

    expect(await screen.findByText(/no inquiries yet/i)).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: /open inbound mail/i }));
    expect(navigate).toHaveBeenCalledWith('/procurement/leads/inbound-mail');
    fireEvent.click(screen.getByRole('button', { name: /upload a document/i }));
    expect(navigate).toHaveBeenCalledWith('/procurement/leads/manual-upload');
    fireEvent.click(screen.getByRole('button', { name: /connect the mailbox/i }));
    expect(navigate).toHaveBeenCalledWith('/setup/mailboxes');
  });

  it('saysTheFiltersMatchedNothing_andOffersToClearThem', async () => {
    renderPage();
    await screen.findByText(/no inquiries yet/i);

    const source = screen.getByLabelText(/lead source/i);
    fireEvent.mouseDown(source);
    const listbox = await screen.findByRole('listbox');
    fireEvent.click(within(listbox).getByRole('option', { name: /^email$/i }));

    expect(await screen.findByText(/no inquiries match these filters/i)).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: /clear filters/i }));
    expect(await screen.findByText(/no inquiries yet/i)).toBeInTheDocument();
  });

  it('countsAViewFromAnotherScreenAsAFilter', async () => {
    // A dashboard tile that links here with ?view= narrows the grid without the reader typing
    // anything, so "no inquiries yet" would be a plain lie.
    renderPage('/procurement/leads/all?view=needs-review');

    expect(await screen.findByText(/no inquiries match these filters/i)).toBeInTheDocument();
  });

  it('does not offer mailbox polling to a reader who lacks Leads Can Create', async () => {
    hasPermission.mockImplementation((_module: string, action?: string) => action !== 'create');
    renderPage();

    const button = await screen.findByRole('button', { name: /check for new leads/i });
    expect(button).toBeDisabled();
    expect(button.parentElement).toHaveAttribute('tabindex', '0');

    fireEvent.mouseOver(button.parentElement as HTMLElement);
    expect(await screen.findByText(/requires can create on leads/i)).toBeInTheDocument();
    expect(fetchEmails).not.toHaveBeenCalled();
  });
});
