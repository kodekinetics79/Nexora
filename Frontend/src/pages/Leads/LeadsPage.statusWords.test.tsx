import { beforeAll, beforeEach, describe, expect, it, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { SnackbarProvider } from 'notistack';
import LeadsPage from './LeadsPage';

// A full DataGrid page through jsdom; 5s is not enough on a cold machine.
vi.setConfig({ testTimeout: 30_000 });

/**
 * The list's status words come from the one shared map the Deadline board and the Decide screen
 * also read. The only wording that changed is QUOTED: "Quoted" read as "these lines are quoted",
 * so it says "Quote sent". Every other label is the one the list already showed.
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

vi.mock('../../api/services/commercialRoutingService', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../../api/services/commercialRoutingService')>();
  return {
    ...actual,
    default: {
      getOwnerOptions: vi.fn().mockResolvedValue([]),
      changeLeadOwner: vi.fn(),
      getLeadAssignmentHistory: vi.fn().mockResolvedValue([]),
    },
  };
});

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
  useAuth: () => ({ hasPermission: () => true, userData: { id: 2, roleName: 'Sales Rep', isManager: false } }),
}));

vi.mock('react-router-dom', async (importOriginal) => {
  const actual = await importOriginal<typeof import('react-router-dom')>();
  return { ...actual, useNavigate: () => vi.fn() };
});

vi.mock('react-i18next', () => ({
  useTranslation: () => ({ t: (key: string) => key }),
}));

const row = (id: number, leadStatusCode: string) => ({
  id,
  nexoraSerial: `NOOR-SONS-LLC-2026-000${id}`,
  rfqno: `RFQ-${id}`,
  buyersName: 'Aramco',
  clientemail: 'buyer@aramco.test',
  leadSource: 'Email',
  recDate: '2026-08-01T00:00:00Z',
  bidClosingDate: '2026-09-01T00:00:00Z',
  customerMatchStatus: 'UNRESOLVED',
  itemCount: 4,
  assignedToId: null,
  assignedToFullName: null,
  assignmentMethod: 'AUTOMATIC',
  assignmentVersion: 1,
  isAccepted: false,
  isRejected: false,
  leadStatusCode,
});

const renderPage = () => {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } });
  return render(
    <MemoryRouter initialEntries={['/procurement/leads/all']}>
      <SnackbarProvider>
        <QueryClientProvider client={client}>
          <LeadsPage />
        </QueryClientProvider>
      </SnackbarProvider>
    </MemoryRouter>,
  );
};

describe('LeadsPage — status words', () => {
  beforeAll(() => {
    class NoopObserver {
      observe() {}
      unobserve() {}
      disconnect() {}
    }
    (globalThis as Record<string, unknown>).ResizeObserver ??= NoopObserver;
    (globalThis as Record<string, unknown>).IntersectionObserver ??= NoopObserver;
  });

  beforeEach(() => {
    vi.clearAllMocks();
    getAll.mockImplementation((params: { pageSize?: number }) => {
      const items = [row(201, 'QUOTED'), row(202, 'CONVERTED_TO_RFQ'), row(203, 'DISQUALIFIED')];
      return Promise.resolve({ items: params?.pageSize === 1 ? items.slice(0, 1) : items, totalCount: items.length, pageNumber: 1, pageSize: 10 });
    });
  });

  it('says "Quote sent" for a sent quote, and keeps the words it already used for the rest', async () => {
    renderPage();

    expect((await screen.findAllByText('Quote sent')).length).toBeGreaterThanOrEqual(1);
    expect(screen.queryByText('Quoted')).toBeNull();
    expect(screen.getAllByText('Became an RFQ').length).toBeGreaterThanOrEqual(1);
    expect(screen.getAllByText('Declined').length).toBeGreaterThanOrEqual(1);
  });
});
