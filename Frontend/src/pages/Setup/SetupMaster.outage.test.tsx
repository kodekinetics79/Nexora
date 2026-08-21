import { render, screen } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { describe, expect, it, vi, beforeEach } from 'vitest';

/**
 * The escape hatch must not fail the way it exists to cure.
 *
 * When Users or Roles & Permissions finds no roles, both hand the administrator a button pointing
 * at /setup/master?type=role — this screen is the named remedy for "no roles configured". Its list
 * read was destructured without `isError`, so a 500 left `allSetups` undefined, `groupedData`
 * returned {}, and the remedy announced "Nothing on this list yet." on a tenant whose roles are
 * all present and all granting access.
 *
 * An administrator sent here by that button, told the list is empty, does the only thing the
 * screen offers: starts recreating roles that already exist.
 */

const { getAll, create, update } = vi.hoisted(() => ({
  getAll: vi.fn(), create: vi.fn(), update: vi.fn(),
}));

vi.mock('../../api/services/setupService', () => ({
  default: { getAll, create, update, getById: vi.fn(), delete: vi.fn() },
}));

vi.mock('react-i18next', () => ({
  useTranslation: () => ({ t: (k: string, f?: string) => f ?? k }),
}));

vi.mock('../../context/AuthContext', () => ({
  useAuth: () => ({
    userData: { userName: 'admin', isSuperAdmin: true, isManager: true, businessUnitId: 1 },
    hasPermission: () => true,
  }),
}));

import SetupMaster from './SetupMaster';

/** What `src/api/axiosInstance.ts` re-rejects on a 500 — the AxiosError, untouched. */
const serverOutage = () => Object.assign(new Error('Request failed with status code 500'), {
  isAxiosError: true,
  code: 'ERR_BAD_RESPONSE',
  config: { method: 'get', url: '/api/SetupMaster' },
  request: {},
  response: { status: 500, data: '', headers: {} },
});

/** `PaginatedSetupResponse` from api/services/setupService.ts — not a bare array. */
const page = (items: unknown[]) => ({
  items, totalItems: items.length, pageNumber: 1, pageSize: 5000, totalPages: 1,
});

const role = {
  setupId: 84, setupType: 'Role', setupCode: 'SALES_REP', setupName: 'Sales Representative',
  description: 'Quotes and leads', parentSetupId: null, roleRank: 0, isActive: true,
};

function renderPage(route = '/setup/master?type=role') {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={client}>
      <MemoryRouter initialEntries={[route]}>
        <SetupMaster />
      </MemoryRouter>
    </QueryClientProvider>,
  );
}

beforeEach(() => vi.clearAllMocks());

describe('Setup › Lists & Picklists, when the read fails', () => {
  it('does not say the list is empty', async () => {
    getAll.mockRejectedValue(serverOutage());
    renderPage();

    await screen.findByText(/could not read this list/i);
    expect(screen.queryByText(/Nothing on this list yet/i)).not.toBeInTheDocument();
  });

  it('does not say no values of the requested type exist', async () => {
    // The scoped variant is the one the "no roles configured" button links to, and its copy is
    // even more specific: "No Role values exist yet. Create the first one".
    getAll.mockRejectedValue(serverOutage());
    renderPage('/setup/master?type=role');

    await screen.findByText(/could not read this list/i);
    expect(screen.queryByText(/values exist yet/i)).not.toBeInTheDocument();
  });

  it('states that nothing is missing — only unread — and offers a retry', async () => {
    getAll.mockRejectedValue(serverOutage());
    renderPage();

    expect(await screen.findByText(/Nothing is missing from your configuration/i)).toBeInTheDocument();
    expect(screen.getByRole('button', { name: /Reload list/i })).toBeInTheDocument();
  });
});

describe('Setup › Lists & Picklists, when the read succeeds', () => {
  it('still says the list is empty when the tenant genuinely has nothing', async () => {
    // CONTROL: withholding the empty copy on a real empty list would strand a first-run admin.
    getAll.mockResolvedValue(page([]));
    renderPage('/setup/master');

    expect(await screen.findByText(/Nothing on this list yet/i)).toBeInTheDocument();
  });

  it('renders the rows it read, with no error surface', async () => {
    getAll.mockResolvedValue(page([role]));
    renderPage();

    expect(await screen.findByText('Sales Representative')).toBeInTheDocument();
    expect(screen.queryByText(/could not read this list/i)).not.toBeInTheDocument();
    expect(screen.queryByText(/Nothing on this list yet/i)).not.toBeInTheDocument();
  });
});
