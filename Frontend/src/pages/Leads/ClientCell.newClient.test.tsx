import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import ClientCell from './ClientCell';

const api = { getAll: vi.fn(), create: vi.fn(), linkClient: vi.fn() };
vi.mock('react-hot-toast', () => ({ toast: { success: vi.fn(), error: vi.fn() } }));
vi.mock('../../context/AuthContext', () => ({ useAuth: () => ({ hasPermission: () => true }) }));
vi.mock('../../api/services/customerService', () => ({
  default: { getAll: (...a: unknown[]) => api.getAll(...a), create: (...a: unknown[]) => api.create(...a) },
}));
vi.mock('../../api/services/leadService', () => ({ default: { linkClient: (...a: unknown[]) => api.linkClient(...a) } }));

const renderCell = (lead: Record<string, unknown>) => render(
  <QueryClientProvider client={new QueryClient()}>
    <ClientCell lead={{ id: 20, customerId: null, customerMatchStatus: 'UNRESOLVED', ...lead }} onResolve={vi.fn()} />
  </QueryClientProvider>,
);

beforeEach(() => {
  vi.clearAllMocks();
  api.getAll.mockResolvedValue({ items: [], totalCount: 0, pageNumber: 1, pageSize: 10 });
  api.create.mockResolvedValue({ id: 91, name: 'ASMO' });
  api.linkClient.mockResolvedValue({});
});

describe('a client the document names but no client on file matches', () => {
  it('shows the name as a new client and adds it with one click', async () => {
    renderCell({ customerCompanyNameExtracted: 'ASMO' });

    expect(screen.getByText('New client')).toBeInTheDocument();
    expect(screen.getByText('ASMO')).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: 'Add ASMO as the client' }));

    await waitFor(() => expect(api.linkClient).toHaveBeenCalledWith(20, { customerId: 91, contactId: null }));
    const form = api.create.mock.calls[0][0] as FormData;
    expect(form.get('Name')).toBe('ASMO');
  });

  it('links the client already on file with that name instead of making a second one', async () => {
    api.getAll.mockResolvedValue({ items: [{ id: 44, name: 'asmo' }], totalCount: 1, pageNumber: 1, pageSize: 10 });
    renderCell({ customerCompanyNameExtracted: 'ASMO' });

    fireEvent.click(screen.getByRole('button', { name: 'Add ASMO as the client' }));

    await waitFor(() => expect(api.linkClient).toHaveBeenCalledWith(20, { customerId: 44, contactId: null }));
    expect(api.create).not.toHaveBeenCalled();
  });

  it('says unknown client when the document names none', () => {
    renderCell({ customerCompanyNameExtracted: null });
    expect(screen.getByText('Unknown client')).toBeInTheDocument();
    expect(screen.queryByText('New client')).toBeNull();
  });
});
