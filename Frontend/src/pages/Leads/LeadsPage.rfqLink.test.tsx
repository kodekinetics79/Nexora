import { beforeEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { SnackbarProvider } from 'notistack';
import LeadsPage from './LeadsPage';

vi.setConfig({ testTimeout: 30_000 });

/**
 * The RFQ number in every row rendered as a link-styled button that, on the SDET's day-one walk,
 * did nothing a rep could see. A row has exactly one thing "open" can mean — the place its own
 * Decide button goes — and the number must go there too.
 */

const getAll = vi.fn();
const navigate = vi.fn();

vi.mock('../../api/services/leadService', () => ({
  default: { getAll: (params: unknown) => getAll(params), fetchEmails: vi.fn() },
}));
vi.mock('../../api/services/decisionService', () => ({
  default: { getDecisionSummaries: vi.fn().mockResolvedValue({ summaries: {} }) },
}));
vi.mock('../../api/services/commercialRoutingService', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../../api/services/commercialRoutingService')>();
  return { ...actual, default: { getOwnerOptions: vi.fn().mockResolvedValue([]), changeLeadOwner: vi.fn(), getLeadAssignmentHistory: vi.fn().mockResolvedValue([]) } };
});
vi.mock('../../hooks/useColumnPreferences', () => ({
  default: () => ({ columnVisibilityModel: {}, onColumnVisibilityModelChange: vi.fn(), arrangeColumns: <T,>(defs: T) => defs, isLoading: false, isError: false }),
}));
vi.mock('../../components/common/ColumnPreferences', () => ({ default: () => null }));
vi.mock('../../context/AuthContext', () => ({
  useAuth: () => ({ hasPermission: () => true, userData: { id: 2, isManager: true } }),
}));
vi.mock('react-router-dom', async (importOriginal) => {
  const actual = await importOriginal<typeof import('react-router-dom')>();
  return { ...actual, useNavigate: () => navigate };
});
vi.mock('react-i18next', () => ({ useTranslation: () => ({ t: (key: string) => key }) }));

const LEAD = {
  id: 101,
  nexoraSerial: 'NOOR-SONS-LLC-2026-000101',
  rfqno: 'RFQ-101',
  buyersName: 'Aramco',
  clientemail: 'buyer@aramco.test',
  leadSource: 'Email',
  recDate: '2026-08-01T00:00:00Z',
  bidClosingDate: '2026-09-01T00:00:00Z',
  customerMatchStatus: 'UNRESOLVED',
  itemCount: 4,
  assignedToId: null,
  assignedToFullName: null,
  isAccepted: false,
  isRejected: false,
};

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

beforeEach(() => {
  vi.clearAllMocks();
  getAll.mockResolvedValue({ items: [LEAD], totalCount: 1, pageNumber: 1, pageSize: 10 });
});

describe('LeadsPage — the RFQ number opens the inquiry', () => {
  it('takes the reader where the row’s own Decide button goes', async () => {
    renderPage();
    const rfqLink = await screen.findByRole('button', { name: 'RFQ-101' });

    fireEvent.click(screen.getByRole('button', { name: /^Decide RFQ-101$/ }));
    const decideTarget = navigate.mock.calls.at(-1)?.[0];
    expect(typeof decideTarget).toBe('string');

    fireEvent.click(rfqLink);
    expect(navigate).toHaveBeenCalledTimes(2);
    expect(navigate.mock.calls.at(-1)?.[0]).toBe(decideTarget);
    // Never the legacy alias that only some screens knew about.
    expect(decideTarget).not.toMatch(/^\/leads\/view\//);
  });
});
