import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router-dom';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import AllRFQsPage from './AllRFQsPage';

const api = { getAll: vi.fn(), customers: vi.fn() };
vi.mock('react-i18next', () => ({ useTranslation: () => ({ t: (key: string) => key }) }));
vi.mock('../../../context/AuthContext', () => ({
  useAuth: () => ({ userData: { id: 7, businessUnitId: 1 }, hasPermission: () => true }),
}));
vi.mock('../../../api/services/rfqService', () => ({ default: { getAll: (...a: unknown[]) => api.getAll(...a) } }));
vi.mock('../../../api/services/customerService', () => ({ default: { getAll: (...a: unknown[]) => api.customers(...a) } }));
vi.mock('../../../hooks/useColumnPreferences', () => ({
  default: () => ({ arrangeColumns: <T,>(columns: T) => columns, columnVisibilityModel: {}, onColumnVisibilityModelChange: vi.fn() }),
}));
vi.mock('../../../components/layout/ViewTabs', () => ({ default: () => null }));

const renderPage = () => render(
  <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
    <MemoryRouter><AllRFQsPage /></MemoryRouter>
  </QueryClientProvider>,
);

const lastCall = () => api.getAll.mock.calls[api.getAll.mock.calls.length - 1][0] as Record<string, unknown>;

beforeEach(() => {
  vi.clearAllMocks();
  api.getAll.mockResolvedValue({ items: [], totalItems: 0, pageNumber: 1, pageSize: 25, totalPages: 0 });
  api.customers.mockResolvedValue({ items: [{ id: 44, name: 'ASMO' }], totalCount: 1, pageNumber: 1, pageSize: 20 });
});

describe('the RFQs list filters', () => {
  it('asks the server for everyone by default, then for unassigned or for mine', async () => {
    renderPage();
    await waitFor(() => expect(api.getAll).toHaveBeenCalled());
    expect(lastCall()).toMatchObject({ assignedToId: undefined, unassigned: undefined, customerId: undefined });

    fireEvent.click(screen.getByRole('button', { name: 'Unassigned' }));
    await waitFor(() => expect(lastCall()).toMatchObject({ unassigned: true, assignedToId: undefined }));

    fireEvent.click(screen.getByRole('button', { name: 'Mine' }));
    await waitFor(() => expect(lastCall()).toMatchObject({ assignedToId: 7, unassigned: undefined }));
  });

  it('keeps only the chosen client', async () => {
    renderPage();
    fireEvent.mouseDown(screen.getByLabelText('Client'));
    fireEvent.click(await screen.findByRole('option', { name: 'ASMO' }));

    await waitFor(() => expect(lastCall()).toMatchObject({ customerId: 44 }));
  });
});
