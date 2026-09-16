import { beforeEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { SnackbarProvider } from 'notistack';
import CustomerDetailPage from './CustomerDetailPage';

/**
 * The customer page said "Account owner — Unassigned" and "Account team — No account team —
 * readable tenant-wide" with nothing to press, and printed the intelligence endpoint's tokens
 * ("insufficient-evidence", "unavailable: No immutable quote-time landed-cost evidence …") as if
 * they were sentences. A manager sets the owner here, is sent to the form that sets the team, and
 * reads "Not enough history yet".
 */

const navigate = vi.fn();
const getAccountOwnerOptions = vi.fn();
const assignAccount = vi.fn();

vi.mock('../../api/services/customerService', () => ({
  default: {
    getById: vi.fn().mockResolvedValue({
      id: 30, name: 'Saudi Aramco', contactEmail: 'buyer@aramco.test', isActive: true, concurrencyToken: 'v1',
      accountTeamId: null, accountTeamName: null, sector: null, regionName: null,
    }),
  },
}));
vi.mock('../../api/services/contactService', () => ({ default: { getByCustomer: vi.fn().mockResolvedValue([]) } }));
vi.mock('../../api/services/intelligenceService', () => ({
  default: {
    getCustomerContext: vi.fn().mockResolvedValue({
      totalQuotes: 0, ordersLast24Months: 0, orderValueStatus: 'no_data', orderValueByCurrency: [],
      recentRfqs: [], recentQuotes: [], recentOrders: [], demandProfile: [], completeness: null,
    }),
  },
}));
vi.mock('../../api/services/commercialLearningService', () => ({
  default: { getCustomer: vi.fn().mockResolvedValue({ inquiryCount: 0, wonCount: 0, lostCount: 0, conversionRatePercent: null, lossReasons: [] }) },
}));
vi.mock('../../api/services/commercialIntelligenceService', () => ({
  default: {
    getAccountOwnership: vi.fn().mockResolvedValue([
      { customerId: 30, customerName: 'Saudi Aramco', ownerUserId: null, ownerName: null, openLeads: 0, openQuotes: 0, pipelineGroups: [], lastActivityAt: null, version: 1 },
    ]),
    getCustomerHealth: vi.fn().mockResolvedValue({
      customerId: 30, generatedAt: '2026-09-15T12:00:00Z',
      dataCompleteness: { status: 'complete', incompleteSources: [] },
      period: { from: '2026-06-17', to: '2026-09-15' },
      rfqTrend: { status: 'insufficient-evidence', reason: 'The denominator is zero for this cohort.', currentCount: 0, previousCount: 0, changePercent: null },
      quoteCoverage: { status: 'insufficient-evidence', reason: null, rfqCount: 0, quotedRfqCount: 0, coveragePercent: null },
      quoteDecisions: { status: 'insufficient-evidence', decidedCount: 0, wonCount: 0, lostCount: 0 },
      conversion: { status: 'insufficient-evidence', ratePercent: null, sampleSize: 0 },
      acceptedPrices: [],
      margin: { status: 'unavailable', reason: 'No immutable quote-time landed-cost evidence is linked to accepted customer quote lines.', grossMarginPercent: null },
      revisionBurden: { status: 'insufficient-evidence', revisionCount: 0, inquiryCount: 0, changedFieldCount: 0, comparedFieldCount: 0, fieldChangePercent: null, changedLineCount: 0, comparedLineCount: 0, lineChangePercent: null },
      followUp: { status: 'insufficient-evidence', openCount: 0, overdueCount: 0, effectivenessPercent: null },
      lastCommercialActivity: null,
      healthBand: 'insufficient-evidence',
      healthReasons: [],
      opportunities: [],
      nextBestAction: null,
    }),
    getAccountOwnerOptions: () => getAccountOwnerOptions(),
    assignAccount: (...args: unknown[]) => assignAccount(...args),
  },
}));
vi.mock('../../components/common/ChangeHistoryPanel', () => ({ default: () => null }));
vi.mock('../../context/AuthContext', () => ({
  useAuth: () => ({ hasPermission: () => true, userData: { id: 5, isManager: true } }),
}));
vi.mock('react-router-dom', async (importOriginal) => {
  const actual = await importOriginal<typeof import('react-router-dom')>();
  return { ...actual, useNavigate: () => navigate };
});

const renderPage = () => render(
  <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } })}>
    <SnackbarProvider>
      <MemoryRouter initialEntries={['/customers/30']}>
        <Routes><Route path="/customers/:id" element={<CustomerDetailPage />} /></Routes>
      </MemoryRouter>
    </SnackbarProvider>
  </QueryClientProvider>,
);

beforeEach(() => {
  vi.clearAllMocks();
  getAccountOwnerOptions.mockResolvedValue([
    { userId: 7, name: 'Sara Bin Ali', email: 'sara@nexora.test', isAvailable: true, capacityPercent: 40, workload: { workloadPoints: 12 }, hasGovernedProfile: true, eligibilityReason: '', measuredAtUtc: '', policyVersion: '' },
  ]);
});

describe('Customer page — account owner, account team and health words', () => {
  it('lets a manager set the account owner from the page, through the account-ownership endpoint', async () => {
    renderPage();
    fireEvent.click(await screen.findByRole('button', { name: 'Set account owner' }));
    const dialog = await screen.findByRole('dialog', { name: 'Set account owner' });
    expect(within(dialog).getByText('Saudi Aramco')).toBeInTheDocument();

    fireEvent.mouseDown(within(dialog).getByRole('combobox', { name: 'Owner' }));
    fireEvent.click(await screen.findByRole('option', { name: /Sara Bin Ali/ }));
    fireEvent.click(within(dialog).getByRole('button', { name: 'Confirm owner' }));

    await waitFor(() => expect(assignAccount).toHaveBeenCalledTimes(1));
    const [customerId, ownerUserId, expectedVersion] = assignAccount.mock.calls[0];
    expect(customerId).toBe(30);
    expect(ownerUserId).toBe(7);
    expect(expectedVersion).toBe(1);
  });

  it('says why "No account team" matters and sends the reader to the form that sets it', async () => {
    renderPage();
    expect(await screen.findByText('No account team')).toBeInTheDocument();
    expect(screen.getByText(/route to nobody by default/)).toBeInTheDocument();
    expect(screen.queryByText(/readable tenant-wide/)).not.toBeInTheDocument();

    fireEvent.click(screen.getByRole('button', { name: 'Set account team' }));
    expect(navigate).toHaveBeenCalledWith('/customers?edit=30');
  });

  it('speaks about missing history in plain words, never in wire tokens', async () => {
    renderPage();
    await screen.findByRole('button', { name: 'Set account owner' });
    expect((await screen.findAllByText('Not enough history yet')).length).toBeGreaterThan(0);
    expect(screen.queryByText(/insufficient[-_ ]evidence/i)).not.toBeInTheDocument();
    expect(screen.queryByText(/unavailable/i)).not.toBeInTheDocument();
    expect(screen.queryByText(/landed-cost/i)).not.toBeInTheDocument();
  });
});
