import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router-dom';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import AllRFQsPage from './AllRFQsPage';

vi.setConfig({ testTimeout: 30_000 });

/**
 * The owner and client narrowing the old filter row carried (Unassigned / Mine / Everyone and the
 * Client picker) now live in the Owner and Customer column headers. The questions are the same.
 */

const api = { getAll: vi.fn(), choices: vi.fn() };
vi.mock('react-i18next', () => ({ useTranslation: () => ({ t: (key: string) => key }) }));
vi.mock('../../../context/AuthContext', () => ({
  useAuth: () => ({ userData: { id: 7, businessUnitId: 1 }, hasPermission: () => true }),
}));
vi.mock('../../../api/services/rfqService', () => ({
  default: {
    getAll: (...a: unknown[]) => api.getAll(...a),
    getListChoices: (...a: unknown[]) => api.choices(...a),
  },
}));
vi.mock('../../../hooks/useColumnPreferences', () => ({
  default: () => ({ arrangeColumns: <T,>(columns: T) => columns, columnVisibilityModel: {}, onColumnVisibilityModelChange: vi.fn() }),
}));
vi.mock('../../../components/layout/ViewTabs', () => ({ default: () => null }));

const renderPage = () => render(
  <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
    <MemoryRouter><AllRFQsPage /></MemoryRouter>
  </QueryClientProvider>,
);

// The last LIST request: the page also asks for a one-row total (pageSize 1) to tell "no RFQs at
// all" from "every RFQ already quoted", and that one is never filtered.
const lastCall = () => api.getAll.mock.calls.map((c) => c[0] as Record<string, unknown>).filter((p) => p.pageSize !== 1).at(-1)!;

const pick = (noun: string, entry: string) => {
  fireEvent.click(screen.getByRole('button', { name: `Filter by ${noun}` }));
  fireEvent.click(within(screen.getByRole('menu')).getByRole('menuitem', { name: entry }));
};

beforeEach(() => {
  vi.clearAllMocks();
  api.getAll.mockResolvedValue({ items: [], totalItems: 0, pageNumber: 1, pageSize: 25, totalPages: 0 });
  api.choices.mockResolvedValue({
    customers: [{ value: '44', label: 'ASMO', count: 2 }], noCustomer: 0, statuses: [], rfqTypes: [], inquiryTypes: [], biddingDecisions: [],
  });
});

describe('the RFQs list owner and client filters', () => {
  it('asks the server for everyone by default, then for unassigned or for mine', async () => {
    renderPage();
    await waitFor(() => expect(api.getAll).toHaveBeenCalled());
    expect(lastCall()).not.toHaveProperty('assignedToId');
    expect(lastCall()).not.toHaveProperty('unassigned');
    expect(lastCall()).not.toHaveProperty('customer');

    pick('owner', 'Unassigned');
    await waitFor(() => expect(lastCall()).toMatchObject({ unassigned: true }));
    expect(lastCall()).not.toHaveProperty('assignedToId');

    pick('owner', 'Mine');
    await waitFor(() => expect(lastCall()).toMatchObject({ assignedToId: 7 }));
    expect(lastCall()).not.toHaveProperty('unassigned');

    pick('owner', 'Anyone');
    await waitFor(() => expect(lastCall()).not.toHaveProperty('assignedToId'));
  });

  it('keeps only the chosen client', async () => {
    renderPage();
    await waitFor(() => expect(api.choices).toHaveBeenCalled());
    fireEvent.click(screen.getByRole('button', { name: 'Filter by customer' }));
    fireEvent.click(await screen.findByRole('menuitem', { name: 'ASMO (2)' }));

    await waitFor(() => expect(lastCall()).toMatchObject({ customer: '44' }));
    expect(lastCall()).not.toHaveProperty('customerId');
  });
});
