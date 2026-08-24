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

vi.mock('../../api/services/leadService', () => ({
  default: {
    getAll: (params: unknown) => getAll(params),
    fetchEmails: vi.fn(),
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
  useAuth: () => ({ hasPermission: () => true }),
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
    getAll.mockResolvedValue({ items: [], totalCount: 0, pageNumber: 1, pageSize: 10 });
  });

  it('saysNoInquiriesYet_andPointsAtInboundMailWhenNothingIsFiltered', async () => {
    renderPage();

    expect(await screen.findByText(/no inquiries yet/i)).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: /open inbound mail/i }));
    expect(navigate).toHaveBeenCalledWith('/procurement/leads/inbound-mail');
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
});
