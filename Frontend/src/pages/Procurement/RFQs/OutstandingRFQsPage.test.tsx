import { render, screen } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router-dom';
import type { ReactNode } from 'react';
import { beforeEach, describe, expect, it, vi } from 'vitest';

const api = {
  getOutstandingLeads: vi.fn(),
  getUsersForAssignment: vi.fn(),
};

vi.mock('../../../api/services/leadService', () => ({
  assignabilityNote: () => null,
  default: {
    getOutstandingLeads: (...args: unknown[]) => api.getOutstandingLeads(...args),
    getUsersForAssignment: (...args: unknown[]) => api.getUsersForAssignment(...args),
    assignLead: vi.fn(),
  },
}));

vi.mock('../../../context/AuthContext', () => ({
  useAuth: () => ({ userData: { id: 8, businessUnitId: 1, roleName: 'Sales Rep' } }),
}));

vi.mock('../../../components/layout/ViewTabs', () => ({ default: () => null }));
vi.mock('../../../components/common/SearchField', () => ({ default: () => null }));
vi.mock('notistack', () => ({ useSnackbar: () => ({ enqueueSnackbar: vi.fn() }) }));
vi.mock('react-i18next', () => ({ useTranslation: () => ({ t: (key: string) => key }) }));

vi.mock('@mui/x-data-grid', () => ({
  DataGrid: ({ rows, columns }: { rows: Array<{ id: number }>; columns: Array<{ field: string; renderCell?: (params: { row: { id: number } }) => ReactNode }> }) => {
    const actions = columns.find((column) => column.field === 'actions');
    return <div>{rows.map((row) => <div key={row.id}>{actions?.renderCell?.({ row })}</div>)}</div>;
  },
}));

import OutstandingRFQsPage from './OutstandingRFQsPage';

const renderPage = () => {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={client}>
      <MemoryRouter>
        <OutstandingRFQsPage />
      </MemoryRouter>
    </QueryClientProvider>,
  );
};

describe('Outstanding RFQ actions', () => {
  beforeEach(() => {
    api.getOutstandingLeads.mockResolvedValue({
      items: [{ id: 492, rfqno: 'P34086' }],
      totalCount: 1,
    });
    api.getUsersForAssignment.mockResolvedValue([]);
  });

  it('offers the Lead record without exposing the retired direct RFQ creator', async () => {
    renderPage();

    expect(await screen.findByRole('button', { name: 'View Lead' })).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Process to RFQ' })).not.toBeInTheDocument();
  });
});
