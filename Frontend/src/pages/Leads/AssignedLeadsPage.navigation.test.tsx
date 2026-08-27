import type { ReactNode } from 'react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MemoryRouter, Route, Routes, useLocation } from 'react-router-dom';
import { describe, expect, it, vi } from 'vitest';

vi.mock('react-i18next', () => ({
  useTranslation: () => ({ t: (key: string) => key }),
}));

vi.mock('notistack', () => ({
  useSnackbar: () => ({ enqueueSnackbar: vi.fn() }),
}));

vi.mock('../../context/AuthContext', () => ({
  useAuth: () => ({
    userData: { id: 8, businessUnitId: 1, isManager: false, isSuperAdmin: false },
    hasPermission: () => true,
  }),
}));

vi.mock('../../api/services/leadService', () => ({
  default: {
    getAssignedLeads: vi.fn().mockResolvedValue({
      items: [{
        id: 492,
        rfqno: 'P34086',
        rfqtype: 'Goods',
        buyersName: 'Test Buyer',
        clientemail: 'buyer@example.test',
        assignedToId: 8,
        assignedToFullName: 'Sales Rep',
        assignedOn: '2026-08-25T12:00:00Z',
        acceptedDate: '2026-08-25T12:00:00Z',
        recDate: '2026-08-25T12:00:00Z',
        bidClosingDate: '2026-08-30T12:00:00Z',
        requiredDeliveryDate: null,
        itemCount: 3,
      }],
      totalCount: 1,
    }),
    getUsersForAssignment: vi.fn().mockResolvedValue([]),
    assignLead: vi.fn(),
  },
  assignabilityNote: () => '',
}));

vi.mock('../../components/common/SearchField', () => ({
  default: ({ value, onChange }: { value: string; onChange: (value: string) => void }) => (
    <input aria-label="Filter assigned leads" value={value} onChange={(event) => onChange(event.target.value)} />
  ),
}));

vi.mock('../../components/layout/ViewTabs', () => ({ default: () => null }));
vi.mock('./ClientCell', () => ({ default: () => <span>Test client</span> }));
vi.mock('./ResolveClientDialog', () => ({ default: () => null }));

vi.mock('@mui/x-data-grid', () => ({
  DataGrid: ({ rows, columns }: {
    rows: Array<Record<string, unknown>>;
    columns: Array<{ field: string; renderCell?: (params: { row: Record<string, unknown> }) => ReactNode }>;
  }) => {
    const actionColumn = columns.find((column) => column.field === 'actions');
    return (
      <div>
        {rows.map((row) => <div key={String(row.id)}>{actionColumn?.renderCell?.({ row })}</div>)}
      </div>
    );
  },
}));

import AssignedLeadsPage from './AssignedLeadsPage';

const Destination = () => {
  const location = useLocation();
  return <output data-testid="destination">{location.pathname}</output>;
};

describe('Assigned Lead next action', () => {
  it('makes the governed decision workbench visible and navigates there', async () => {
    const queryClient = new QueryClient({
      defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
    });

    render(
      <QueryClientProvider client={queryClient}>
        <MemoryRouter initialEntries={['/procurement/leads/assigned']}>
          <Routes>
            <Route path="/procurement/leads/assigned" element={<AssignedLeadsPage />} />
            <Route path="/procurement/leads/:id/workbench" element={<Destination />} />
          </Routes>
        </MemoryRouter>
      </QueryClientProvider>,
    );

    const workbenchButton = await screen.findByRole('button', {
      name: 'Open decision workbench for P34086',
    });
    expect(workbenchButton).toHaveTextContent('Decision workbench');

    fireEvent.click(workbenchButton);

    await waitFor(() => {
      expect(screen.getByTestId('destination')).toHaveTextContent('/procurement/leads/492/workbench');
    });
  });
});
