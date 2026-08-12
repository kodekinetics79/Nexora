import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { SnackbarProvider } from 'notistack';
import { MemoryRouter } from 'react-router-dom';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { platformApi } from '../api/client';
import type { OverviewMetrics } from '../types';
import OverviewPage from './OverviewPage';
import { ThemeContextProvider } from '../../context/ThemeContext';

const navigate = vi.hoisted(() => vi.fn());
vi.mock('react-router-dom', async () => ({
  ...(await vi.importActual<typeof import('react-router-dom')>('react-router-dom')),
  useNavigate: () => navigate,
}));

/** A fleet that exists but has done nothing — the state the old overview reported as 0%. */
const quietFleet = (overrides: Partial<OverviewMetrics> = {}): OverviewMetrics => ({
  asOfUtc: '2026-08-12T01:00:00Z',
  windowDays: 14,
  windowStartUtc: '2026-07-30T00:00:00Z',
  tenantCount: 5,
  activeTenants: 0,
  tenantsByStatus: [
    { status: 'Provisioning', count: 5 },
    { status: 'Active', count: 0 },
    { status: 'Suspended', count: 0 },
    { status: 'Archived', count: 0 },
    { status: 'PastDue', count: 0 },
  ],
  newTenantsInWindow: 5,
  docsProcessedMtd: 0,
  docsProcessedInWindow: 0,
  failuresInWindow: 0,
  extractionSuccessRate: null,
  extractionSuccessRateWindow: null,
  queueDepth: 0,
  inFlight: 0,
  deadLetter: 0,
  oldestPendingMinutes: null,
  llmCostMtdUsd: 0,
  llmCostTrendPct: null,
  activeUsersFleetWide: 2,
  commercial: {
    leadsCaptured: 0,
    rfqsCaptured: 0,
    quotesIssued: 0,
    ordersWon: 0,
    rfqsQuotedPct: null,
    quotesOrderedPct: null,
    orderValueByCurrency: [],
  },
  health: { worst: 'healthy', healthy: 3, degraded: 0, down: 0 },
  services: [
    { key: 'database', name: 'Database', status: 'healthy', latencyMs: 4, detail: 'Database reachable' },
  ],
  throughput: Array.from({ length: 14 }, (_, i) => ({
    date: `2026-07-${30 + i}`.slice(0, 10), docs: 0, failures: 0,
  })),
  costTrend: Array.from({ length: 14 }, (_, i) => ({ date: `2026-07-${30 + i}`.slice(0, 10), costUsd: 0 })),
  tenantsByPlan: [{ tier: 'growth', count: 5 }],
  topTenants: [],
  ...overrides,
});

const busyFleet = (): OverviewMetrics => quietFleet({
  activeTenants: 4,
  tenantsByStatus: [
    { status: 'Provisioning', count: 1 },
    { status: 'Active', count: 4 },
    { status: 'Suspended', count: 0 },
    { status: 'Archived', count: 0 },
    { status: 'PastDue', count: 0 },
  ],
  docsProcessedInWindow: 75,
  failuresInWindow: 9,
  extractionSuccessRate: 0.868,
  extractionSuccessRateWindow: 0.892,
  queueDepth: 6,
  oldestPendingMinutes: 90,
  commercial: {
    leadsCaptured: 36,
    rfqsCaptured: 30,
    quotesIssued: 21,
    ordersWon: 9,
    rfqsQuotedPct: 0.7,
    quotesOrderedPct: 0.2857,
    orderValueByCurrency: [
      { currency: 'SAR', orders: 6, amount: 324000 },
      { currency: 'USD', orders: 3, amount: 162000 },
    ],
  },
  health: { worst: 'down', healthy: 9, degraded: 1, down: 1 },
  throughput: Array.from({ length: 14 }, (_, i) => ({
    date: `2026-07-${30 + i}`.slice(0, 10), docs: 5, failures: i === 0 ? 9 : 0,
  })),
  topTenants: [
    {
      tenantId: 7, name: 'Northwind Aerospace', slug: 'northwind', status: 'Active',
      plan: 'growth', docs: 25, failures: 3, rfqs: 10, quotes: 7, orders: 3,
    },
  ],
});

const renderPage = () => render(
  <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
    <MemoryRouter initialEntries={['/platform/overview']}>
      <ThemeContextProvider>
        <SnackbarProvider><OverviewPage /></SnackbarProvider>
      </ThemeContextProvider>
    </MemoryRouter>
  </QueryClientProvider>,
);

beforeEach(() => {
  vi.restoreAllMocks();
  navigate.mockReset();
});

describe('platform overview', () => {
  it('reports a missing rate as an em dash, never as total failure', async () => {
    // A fleet that has never run a job used to read "Extraction Success 0.0%".
    vi.spyOn(platformApi, 'getOverview').mockResolvedValue(quietFleet());
    renderPage();

    expect(await screen.findByText('—')).toBeVisible();
    expect(screen.getByText('no jobs finished in 14 days')).toBeVisible();
    expect(screen.queryByText('0.0%')).not.toBeInTheDocument();
  });

  it('names the lifecycle states the fleet is actually in', async () => {
    // "5 tenants / 0 active" hid that every one of them was stuck provisioning.
    vi.spyOn(platformApi, 'getOverview').mockResolvedValue(quietFleet());
    renderPage();

    expect(await screen.findByText('0 active · 5 provisioning')).toBeVisible();
  });

  it('says nothing happened rather than drawing a flat line at zero', async () => {
    vi.spyOn(platformApi, 'getOverview').mockResolvedValue(quietFleet());
    renderPage();

    expect(await screen.findByText('No documents processed in the last 14 days.')).toBeVisible();
    expect(screen.getByText('No metered gateway spend in the last 14 days.')).toBeVisible();
    expect(screen.getByText('No commercial activity anywhere in the fleet in the last 14 days.')).toBeVisible();
    expect(screen.getByText('No tenants have been provisioned yet.')).toBeVisible();
  });

  it('asks the server for the window the operator selected', async () => {
    const getOverview = vi.spyOn(platformApi, 'getOverview').mockResolvedValue(quietFleet());
    renderPage();
    await screen.findByText('System Health');
    expect(getOverview).toHaveBeenCalledWith(14);

    getOverview.mockResolvedValue({ ...quietFleet(), windowDays: 30 });
    fireEvent.click(screen.getByRole('button', { name: '30 day window' }));

    await waitFor(() => expect(getOverview).toHaveBeenCalledWith(30));
    expect(await screen.findByText('no jobs finished in 30 days')).toBeVisible();
  });

  it('shows order value per currency and never a blended total', async () => {
    vi.spyOn(platformApi, 'getOverview').mockResolvedValue(busyFleet());
    renderPage();

    expect(await screen.findByText('SAR 324,000 · 6 orders')).toBeVisible();
    expect(screen.getByText('USD 162,000 · 3 orders')).toBeVisible();
    // 324000 + 162000 must appear nowhere: the two currencies are not addable.
    expect(screen.queryByText(/486,000/)).not.toBeInTheDocument();
  });

  it('publishes the linked conversion rates alongside the counts', async () => {
    vi.spyOn(platformApi, 'getOverview').mockResolvedValue(busyFleet());
    renderPage();

    expect(await screen.findByText('70.0% of those RFQs quoted')).toBeVisible();
    expect(screen.getByText('28.6% of those quotes ordered')).toBeVisible();
  });

  it('rolls up service health instead of leaving one red card to be spotted', async () => {
    vi.spyOn(platformApi, 'getOverview').mockResolvedValue(busyFleet());
    renderPage();

    expect(await screen.findByText('1 down · 1 degraded · 9 healthy')).toBeVisible();
  });

  it('opens the tenant record from the activity table', async () => {
    vi.spyOn(platformApi, 'getOverview').mockResolvedValue(busyFleet());
    renderPage();

    fireEvent.click(await screen.findByText('Northwind Aerospace'));
    expect(navigate).toHaveBeenCalledWith('/platform/tenants/7');
  });
});
