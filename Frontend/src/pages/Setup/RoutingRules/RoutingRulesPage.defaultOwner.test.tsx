import { beforeEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';

/**
 * The fallback owner.
 *
 * `GET/PUT /api/commercial-routing/default-owner` existed with no frontend caller, so "who gets
 * an inquiry that no rule claims" could only be set by editing the database, and the routing
 * page described the default in prose only. One select, bound to those endpoints, on the page
 * a manager already opens to decide routing.
 */

const getDefaultOwner = vi.fn();
const setDefaultOwner = vi.fn();
const auth = { isManager: false, grants: null as Set<string> | null };

vi.mock('../../../api/services/commercialRoutingService', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../../../api/services/commercialRoutingService')>();
  return {
    ...actual,
    default: {
      getDefaultOwner: () => getDefaultOwner(),
      setDefaultOwner: (userId: number | null) => setDefaultOwner(userId),
      getCustomerProfile: vi.fn(),
      createOwnership: vi.fn(),
      upsertIdentifier: vi.fn(),
    },
  };
});
vi.mock('../../../api/services/customerService', () => ({
  default: { getAll: vi.fn().mockResolvedValue({ items: [] }) },
}));
vi.mock('../../../api/services/userService', () => ({
  default: {
    getAll: vi.fn().mockResolvedValue({
      items: [
        { id: 11, firstName: 'Sara', lastName: 'Bin Ali', email: 'sara@example.test' },
        { id: 12, firstName: 'Tariq', lastName: 'Al-Harbi', email: 'tariq@example.test' },
      ],
    }),
  },
}));
vi.mock('../../../api/services/categoryService', () => ({ categoryService: { getAll: vi.fn() } }));
vi.mock('../../../api/services/businessUnitService', () => ({ default: { getDropdown: vi.fn() } }));
vi.mock('../../../context/AuthContext', () => ({
  useAuth: () => ({
    userData: { isManager: auth.isManager, businessUnitId: 1 },
    hasPermission: (module: string, action?: string) =>
      auth.grants === null || auth.grants.has(action ? `${module}:${action}` : module),
  }),
}));
vi.mock('react-hot-toast', () => ({
  toast: { success: vi.fn(), error: vi.fn() },
  default: { success: vi.fn(), error: vi.fn() },
}));

import RoutingRulesPage from './RoutingRulesPage';

const renderPage = () => {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } });
  return render(
    <QueryClientProvider client={client}>
      <MemoryRouter initialEntries={['/setup/routing-rules']}>
        <RoutingRulesPage />
      </MemoryRouter>
    </QueryClientProvider>,
  );
};

const nobody = { defaultOwnerUserId: null, name: null, email: null, isEligible: false, eligibilityReason: 'No fallback owner is set.', setByUserId: null, setOn: null };
const sara = { defaultOwnerUserId: 11, name: 'Sara Bin Ali', email: 'sara@example.test', isEligible: true, eligibilityReason: 'Eligible.', setByUserId: 1, setOn: '2026-09-01T00:00:00Z' };

beforeEach(() => {
  vi.clearAllMocks();
  auth.isManager = true;
  auth.grants = null;
  getDefaultOwner.mockResolvedValue(nobody);
  setDefaultOwner.mockImplementation((userId: number | null) => Promise.resolve(userId === 11 ? sara : nobody));
});

describe('RoutingRulesPage — the fallback owner', () => {
  it('explains what the fallback owner does and lets a manager choose one from the active users', async () => {
    renderPage();

    expect(await screen.findByText(/when an inquiry arrives and no rule below claims it/i)).toBeInTheDocument();
    const select = await screen.findByRole('combobox', { name: /fallback owner/i });
    fireEvent.mouseDown(select);
    const listbox = await screen.findByRole('listbox');
    fireEvent.click(within(listbox).getByRole('option', { name: /sara bin ali/i }));

    await waitFor(() => expect(setDefaultOwner).toHaveBeenCalledWith(11));
    expect(await screen.findByRole('combobox', { name: /fallback owner/i })).toHaveTextContent(/sara bin ali/i);
  });

  it('warns when the chosen person is one routing will not actually use', async () => {
    getDefaultOwner.mockResolvedValue({ ...sara, isEligible: false, eligibilityReason: 'Sara Bin Ali has no Sales Rep profile, so routing will skip her.' });
    renderPage();

    expect(await screen.findByText(/has no sales rep profile, so routing will skip her/i)).toBeInTheDocument();
  });

  it('shows a rep the current setting read-only, with the reason it cannot be changed here', async () => {
    auth.isManager = false;
    getDefaultOwner.mockResolvedValue(sara);
    renderPage();

    expect(await screen.findByText('Sara Bin Ali')).toBeInTheDocument();
    expect(screen.queryByRole('combobox', { name: /fallback owner/i })).not.toBeInTheDocument();
    expect(screen.getByText(/only a manager with can edit on leads can change the fallback owner/i)).toBeInTheDocument();
  });
});
