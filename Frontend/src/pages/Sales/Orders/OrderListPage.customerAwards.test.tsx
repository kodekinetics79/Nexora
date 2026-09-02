import { beforeEach, describe, expect, it, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';

/**
 * Orders are confirmed from the Client PO Inbox, a Customer Awards screen. For a reader without
 * that grant the list page embedded a guarded widget that rendered a full "Access Denied" panel
 * in its header, and its empty state offered a button that landed on the same panel. The door is
 * hidden and the key is named instead.
 */

const get = vi.fn();
const grants = { customerAwards: false };

vi.mock('../../../api/axiosInstance', () => ({
  default: { get: (url: string, config?: unknown) => get(url, config), post: vi.fn() },
}));
vi.mock('../../../context/AuthContext', () => ({
  useAuth: () => ({
    userData: { businessUnitId: 1 },
    token: 'synthetic-test-token',
    hasPermission: (module: string) => module !== 'Customer Awards' || grants.customerAwards,
    permissionsError: null,
    permissionsLoading: false,
    refreshPermissions: vi.fn(),
  }),
}));

import OrderListPage from './OrderListPage';
import CreateOrderPage from './CreateOrderPage';

const renderPage = (ui: React.ReactElement, route = '/sales/orders') => render(
  <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
    <MemoryRouter initialEntries={[route]}>{ui}</MemoryRouter>
  </QueryClientProvider>,
);

beforeEach(() => {
  vi.clearAllMocks();
  grants.customerAwards = false;
  get.mockImplementation((url: string) => Promise.resolve({ data: url === '/api/Order' ? [] : {} }));
});

describe('the order screens without Customer Awards', () => {
  it('the list hides the inbox buttons, shows no Access Denied panel, and names the access to ask for', async () => {
    renderPage(<OrderListPage />);

    expect(await screen.findByText(/no customer orders yet/i)).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /client po inbox/i })).not.toBeInTheDocument();
    expect(screen.queryByText(/access denied/i)).not.toBeInTheDocument();
    expect(screen.getAllByText(/ask your administrator for customer awards access to confirm customer orders/i).length).toBeGreaterThan(0);
  });

  it('the create screen keeps Back to orders and drops the inbox button', () => {
    renderPage(<CreateOrderPage />, '/sales/orders/create');

    expect(screen.queryByRole('button', { name: /open client po inbox/i })).not.toBeInTheDocument();
    expect(screen.getByRole('button', { name: /back to orders/i })).toBeInTheDocument();
    expect(screen.getByText(/ask your administrator for customer awards access/i)).toBeInTheDocument();
  });

  it('with the grant, both doors are back (the control)', async () => {
    grants.customerAwards = true;
    renderPage(<OrderListPage />);
    expect(await screen.findByRole('button', { name: /open client po inbox/i })).toBeInTheDocument();
    expect(screen.queryByText(/ask your administrator/i)).not.toBeInTheDocument();
  });
});
