import type { ReactNode } from 'react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { render, screen, waitFor, within } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import LossesBand from './LossesBand';
import type { PipelineAnalyticsDTO, PipelineLossReasonDTO } from '../../../api/services/dashboardService';

const getPipelineAnalytics = vi.fn();
vi.mock('../../../api/services/dashboardService', () => ({
  default: { getPipelineAnalytics: (...args: unknown[]) => getPipelineAnalytics(...args) },
}));

const wrapper = ({ children }: { children: ReactNode }) => {
  const client = new QueryClient({ defaultOptions: { queries: { retryDelay: 0 } } });
  return <QueryClientProvider client={client}>{children}</QueryClientProvider>;
};

const FROM = '2026-08-08';
const TO = '2026-09-06';

const analytics = (over: Partial<PipelineAnalyticsDTO> = {}): PipelineAnalyticsDTO => ({
  funnel: [
    { key: 'leads', label: 'Requests in', count: 0, value: null, valueCurrency: null, valueUnavailableReason: 'Nothing priced yet.' },
    { key: 'accepted', label: 'Accepted', count: 0, value: null, valueCurrency: null, valueUnavailableReason: 'Nothing priced yet.' },
    { key: 'quoted', label: 'Quotes written', count: 0, value: null, valueCurrency: null, valueUnavailableReason: 'Nothing priced yet.' },
    { key: 'won', label: 'Won', count: 0, value: null, valueCurrency: null, valueUnavailableReason: 'Nothing priced yet.' },
  ],
  lossReasons: [],
  weightedForecast: null,
  forecastCurrency: null,
  forecastUnavailableReason: 'No open quote carries an approved rate.',
  awaitingResponseQuotes: 0,
  awaitingResponseValue: null,
  respondedQuotes: 0,
  respondedValue: null,
  funnelScope: 'window',
  windowFrom: '2026-08-08T00:00:00Z',
  windowTo: '2026-09-06T00:00:00Z',
  roleScope: { scope: 'managed_scope', ownerUserId: 7, accountTeamIds: [2, 5], scopedUserIds: [7, 9] },
  unownedQuotesExcluded: 0,
  unownedQuotesExcludedReason: null,
  generatedAt: '2026-09-06T09:15:00Z',
  ...over,
});

/**
 * The server's own ordering: by count descending ACROSS both groups. That ordering is what makes a
 * single ranked list dangerous, so the fixture keeps it — an auto-expiry sits at the top of it.
 */
const REASONS: PipelineLossReasonDTO[] = [
  { code: 'AUTO_EXPIRED', reason: 'Expired without an answer', group: 'never_established', count: 14, value: null, valueCurrency: null, valueUnavailableReason: 'These quotes span currencies with no approved rate.' },
  { code: 'PRICE', reason: 'Price', group: 'customer_stated', count: 9, value: 210_000, valueCurrency: 'SAR', valueUnavailableReason: null },
  { code: 'UNRECORDED', reason: 'No reason recorded', group: 'never_established', count: 6, value: 88_000, valueCurrency: 'SAR', valueUnavailableReason: null },
  { code: 'LEAD_TIME', reason: 'Lead time', group: 'customer_stated', count: 4, value: 51_000, valueCurrency: 'SAR', valueUnavailableReason: null },
];

const band = () => screen.getByLabelText('Why we lost');

beforeEach(() => {
  vi.clearAllMocks();
  getPipelineAnalytics.mockResolvedValue(analytics());
});

describe('LossesBand — the horizon', () => {
  it('puts stated reasons above the line and never-established ones below it, on one scale', async () => {
    getPipelineAnalytics.mockResolvedValue(analytics({ lossReasons: REASONS }));
    render(<LossesBand from={FROM} to={TO} />, { wrapper });

    await waitFor(() => expect(screen.getByTestId('loss-bar-PRICE')).toBeInTheDocument());
    expect(screen.getByTestId('loss-bar-PRICE')).toHaveAttribute('data-side', 'above');
    expect(screen.getByTestId('loss-bar-LEAD_TIME')).toHaveAttribute('data-side', 'above');
    expect(screen.getByTestId('loss-bar-AUTO_EXPIRED')).toHaveAttribute('data-side', 'below');
    expect(screen.getByTestId('loss-bar-UNRECORDED')).toHaveAttribute('data-side', 'below');

    // One ruler: the tallest count sets it, and every other bar is that fraction of it. 9 of 14
    // over an 84px maximum is 54px, and 14 is the full 84 whichever side of the line it is on.
    expect(screen.getByTestId('loss-bar-AUTO_EXPIRED')).toHaveStyle({ height: '84px' });
    expect(screen.getByTestId('loss-bar-PRICE')).toHaveStyle({ height: '54px' });
  });

  it('colours the two halves apart: graphite above, oxide below', async () => {
    getPipelineAnalytics.mockResolvedValue(analytics({ lossReasons: REASONS }));
    render(<LossesBand from={FROM} to={TO} />, { wrapper });

    await waitFor(() => expect(screen.getByTestId('loss-bar-PRICE')).toBeInTheDocument());
    expect(screen.getByTestId('loss-bar-PRICE')).toHaveStyle({ backgroundColor: 'var(--nx-series-graphite)' });
    expect(screen.getByTestId('loss-bar-AUTO_EXPIRED')).toHaveStyle({ backgroundColor: 'var(--nx-series-oxide)' });
  });

  it('subtotals each group on its own side and gives the actionable one the larger figure', async () => {
    getPipelineAnalytics.mockResolvedValue(analytics({ lossReasons: REASONS }));
    render(<LossesBand from={FROM} to={TO} />, { wrapper });

    const stated = await screen.findByTestId('losses-stated-total');
    const never = screen.getByTestId('losses-never-total');
    expect(stated).toHaveTextContent('13');
    expect(never).toHaveTextContent('20');
    // "We never found out" is the number a manager can act on, so it is the bigger of the two in
    // type as well as in meaning — it does not depend on which group happens to be larger today.
    expect(Number(getComputedStyle(never).fontSize.replace('px', '')))
      .toBeGreaterThan(Number(getComputedStyle(stated).fontSize.replace('px', '')));
  });

  // An auto-expiry at the top of a single ranking reads as the market rejecting us, when what it
  // means is that nobody followed up. The band must never present one ordering over both groups.
  it('never ranks the two groups against each other in one list', async () => {
    getPipelineAnalytics.mockResolvedValue(analytics({ lossReasons: REASONS }));
    render(<LossesBand from={FROM} to={TO} />, { wrapper });

    await waitFor(() => expect(screen.getByTestId('loss-column-PRICE')).toBeInTheDocument());
    const columns = Array.from(band().querySelectorAll('[data-testid^="loss-column-"]'))
      .map((node) => node.getAttribute('data-testid'));
    expect(columns).toEqual([
      'loss-column-PRICE',
      'loss-column-LEAD_TIME',
      'loss-column-AUTO_EXPIRED',
      'loss-column-UNRECORDED',
    ]);
  });

  it('says so plainly when nothing hangs below the line', async () => {
    getPipelineAnalytics.mockResolvedValue(analytics({
      lossReasons: REASONS.filter((r) => r.group === 'customer_stated'),
    }));
    render(<LossesBand from={FROM} to={TO} />, { wrapper });

    expect(await screen.findByText(/Nothing hangs below the line/)).toBeInTheDocument();
    expect(screen.getByTestId('losses-never-total')).toHaveTextContent('0');
  });
});

describe('LossesBand — the figures the server would not state', () => {
  it('offers a reason\'s value as the server\'s own sentence rather than as a zero', async () => {
    getPipelineAnalytics.mockResolvedValue(analytics({ lossReasons: REASONS }));
    render(<LossesBand from={FROM} to={TO} />, { wrapper });

    const expired = await screen.findByTestId('loss-column-AUTO_EXPIRED');
    expect(expired).toHaveAttribute('aria-label', expect.stringContaining('Value not available'));
    expect(expired).toHaveAttribute('aria-label', expect.stringContaining('no approved rate'));
    expect(screen.getByTestId('loss-column-PRICE')).toHaveAttribute('aria-label', expect.stringContaining('210,000'));
  });

  it('leaves a reason it cannot place out of both halves and discloses it', async () => {
    getPipelineAnalytics.mockResolvedValue(analytics({
      lossReasons: [
        ...REASONS,
        { code: 'REASON_88', reason: 'Withdrawn by procurement', group: 'internally_closed', count: 3, value: null, valueCurrency: null, valueUnavailableReason: 'No approved rate.' },
      ],
    }));
    render(<LossesBand from={FROM} to={TO} />, { wrapper });

    expect(await screen.findByText(/cannot place on either side of the line/)).toBeInTheDocument();
    expect(screen.getByText(/3 losses are\s*not in the figures above/)).toBeInTheDocument();
    expect(screen.queryByTestId('loss-bar-REASON_88')).toBeNull();
    // The two subtotals are unchanged by a group nobody could place.
    expect(screen.getByTestId('losses-never-total')).toHaveTextContent('20');
  });

  it('heads the figures with the endpoint\'s own scope and the window it applied', async () => {
    getPipelineAnalytics.mockResolvedValue(analytics({ lossReasons: REASONS }));
    render(<LossesBand from={FROM} to={TO} />, { wrapper });

    const seal = await screen.findByTestId('band-seal');
    await waitFor(() => expect(seal).toHaveTextContent('Your managed scope · 8 Aug – 5 Sep'));
    expect(seal).toHaveAttribute('data-governed', 'true');
  });
});

/**
 * The primary case. A pre-launch tenant has lost nothing, and the band still has to be the same
 * object: the horizon, both halves at full height, and both subtotals reading a measured zero.
 */
describe('LossesBand — the clean horizon a new tenant opens', () => {
  it('keeps the line and both subtotals with nothing lost yet', async () => {
    render(<LossesBand from={FROM} to={TO} />, { wrapper });

    expect(await screen.findByTestId('losses-empty')).toHaveTextContent(/nothing to explain yet/);
    expect(screen.getByTestId('losses-horizon')).toBeInTheDocument();
    expect(screen.getByTestId('losses-stated-total')).toHaveTextContent('0');
    expect(screen.getByTestId('losses-never-total')).toHaveTextContent('0');
    expect(screen.getByText('Reasons the customer gave')).toBeInTheDocument();
    expect(screen.getByText('We never found out')).toBeInTheDocument();

    // Empty is neither an error nor a withheld figure.
    expect(screen.queryByText('We could not load this')).toBeNull();
    expect(screen.queryByRole('button', { name: 'Retry' })).toBeNull();
  });
});

describe('LossesBand — when the aggregate cannot be loaded', () => {
  it('shows the failure with a retry and prints no zero subtotal that could read as a clean horizon', async () => {
    getPipelineAnalytics.mockRejectedValue(new Error('pipeline-analytics is down'));
    render(<LossesBand from={FROM} to={TO} />, { wrapper });

    expect(await screen.findByText('We could not load this', {}, { timeout: 4000 })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Retry' })).toBeInTheDocument();
    expect(within(band()).queryByTestId('losses-never-total')).toBeNull();
    expect(within(band()).queryByTestId('losses-empty')).toBeNull();
  });
});
