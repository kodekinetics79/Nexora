import type { ReactNode } from 'react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { render, screen, waitFor, within } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import OutstandingBand from './OutstandingBand';
import type { PipelineAnalyticsDTO } from '../../../api/services/dashboardService';

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

/**
 * The shape the endpoint actually emits, every field present.
 *
 * This matters more than usual here: the band's whole job is telling a stated 0 apart from a
 * figure the server would not state, so a fixture that simply omitted `windowFrom`, `roleScope` or
 * `unownedQuotesExcludedReason` would let assertions pass on a payload production never produces.
 */
const analytics = (over: Partial<PipelineAnalyticsDTO> = {}): PipelineAnalyticsDTO => ({
  funnel: [
    { key: 'leads', label: 'Requests in', count: 0, value: null, valueCurrency: null, valueUnavailableReason: 'No request in this window carries a priced line.' },
    { key: 'accepted', label: 'Accepted', count: 0, value: null, valueCurrency: null, valueUnavailableReason: 'No request in this window carries a priced line.' },
    { key: 'quoted', label: 'Quotes written', count: 0, value: null, valueCurrency: null, valueUnavailableReason: 'No quote has been written in this window.' },
    { key: 'won', label: 'Won', count: 0, value: null, valueCurrency: null, valueUnavailableReason: 'No quote has been won in this window.' },
  ],
  lossReasons: [],
  weightedForecast: 41200,
  forecastCurrency: 'SAR',
  forecastUnavailableReason: null,
  awaitingResponseQuotes: 0,
  awaitingResponseValue: null,
  respondedQuotes: 0,
  respondedValue: null,
  funnelScope: 'window',
  windowFrom: '2026-08-08T00:00:00Z',
  windowTo: '2026-09-06T00:00:00Z',
  roleScope: { scope: 'tenant', ownerUserId: null, accountTeamIds: [], scopedUserIds: [] },
  unownedQuotesExcluded: 0,
  unownedQuotesExcludedReason: null,
  generatedAt: '2026-09-06T09:15:00Z',
  ...over,
});

/** A tenant with a real book: some won, some lost, some out with suppliers, some with customers. */
const populated = (): PipelineAnalyticsDTO => analytics({
  funnel: [
    { key: 'leads', label: 'Requests in', count: 148, value: 3_100_000, valueCurrency: 'SAR', valueUnavailableReason: null },
    { key: 'accepted', label: 'Accepted', count: 61, value: 1_400_000, valueCurrency: 'SAR', valueUnavailableReason: null },
    { key: 'quoted', label: 'Quotes written', count: 44, value: null, valueCurrency: null, valueUnavailableReason: 'Quotes in this window span three currencies with no approved rate between them.' },
    { key: 'won', label: 'Won', count: 12, value: 380_000, valueCurrency: 'SAR', valueUnavailableReason: null },
  ],
  lossReasons: [
    { code: 'PRICE', reason: 'Price', group: 'customer_stated', count: 9, value: 210_000, valueCurrency: 'SAR', valueUnavailableReason: null },
    { code: 'AUTO_EXPIRED', reason: 'Expired without an answer', group: 'never_established', count: 7, value: null, valueCurrency: null, valueUnavailableReason: 'No approved rate covers these quotes.' },
  ],
  awaitingResponseQuotes: 15,
  awaitingResponseValue: 610_000,
  respondedQuotes: 3,
  respondedValue: 90_000,
});

const band = () => screen.getByLabelText("What's out with customers, and where it stops");
const rectOf = (testId: string) =>
  band().querySelector(`[data-testid="${testId}"] rect`) as SVGRectElement | null;

beforeEach(() => {
  vi.clearAllMocks();
  getPipelineAnalytics.mockResolvedValue(analytics());
});

describe('OutstandingBand — the sent book', () => {
  it('direct-labels all four states with their counts and their words, and carries no legend', async () => {
    getPipelineAnalytics.mockResolvedValue(populated());
    render(<OutstandingBand from={FROM} to={TO} />, { wrapper });

    await waitFor(() => expect(screen.getAllByText('Won').length).toBeGreaterThan(0));
    for (const [words, count] of [
      ['Won', '12'],
      ['Lost or expired', '16'],
      ['Supplier responded', '3'],
      ['Awaiting the customer', '15'],
    ] as const) {
      expect(screen.getAllByText(words).length).toBeGreaterThan(0);
      expect(screen.getAllByText(count).length).toBeGreaterThan(0);
    }
  });

  // The one thing a reader has to learn once. Hatch means "not decided yet" everywhere on this
  // screen, so the awaiting segment must be the hatch fill and nothing else may be.
  it('draws the awaiting segment hollow and every decided segment solid', async () => {
    getPipelineAnalytics.mockResolvedValue(populated());
    render(<OutstandingBand from={FROM} to={TO} />, { wrapper });

    await waitFor(() => expect(rectOf('book-segment-awaiting')).not.toBeNull());
    expect(rectOf('book-segment-awaiting')!.getAttribute('fill')).toMatch(/^url\(#nx-hatch-/);
    expect(rectOf('book-segment-won')!.getAttribute('fill')).toBe('var(--nx-series-brass-mark)');
    expect(rectOf('book-segment-lost')!.getAttribute('fill')).toBe('var(--nx-series-oxide)');
    expect(rectOf('book-segment-responded')!.getAttribute('fill')).toBe('var(--nx-series-graphite)');
  });

  // A count of one beside a count of a hundred is a sub-pixel sliver, and an invisible segment
  // next to the numeral "1" reads as a rendering fault rather than as a small number.
  it('keeps a one-quote segment visible without letting the four lengths overrun the bar', async () => {
    getPipelineAnalytics.mockResolvedValue(analytics({
      funnel: populated().funnel.map((s) => (s.key === 'won' ? { ...s, count: 400 } : { ...s, count: 0 })),
      lossReasons: [{ code: 'PRICE', reason: 'Price', group: 'customer_stated', count: 1, value: null, valueCurrency: null, valueUnavailableReason: 'No approved rate.' }],
      respondedQuotes: 0,
      awaitingResponseQuotes: 0,
    }));
    render(<OutstandingBand from={FROM} to={TO} />, { wrapper });

    await waitFor(() => expect(rectOf('book-segment-lost')).not.toBeNull());
    const widths = ['won', 'lost'].map((key) =>
      Number(band().querySelector(`[data-testid="book-segment-${key}"]`)!.getAttribute('data-width')));
    expect(widths[1]).toBeGreaterThanOrEqual(5);
    expect(widths[0] + widths[1]).toBeCloseTo(560, 5);
  });

  it('keeps the frame and states the reason when the server sent no won stage at all', async () => {
    getPipelineAnalytics.mockResolvedValue(analytics({
      funnel: populated().funnel.filter((s) => s.key !== 'won'),
    }));
    render(<OutstandingBand from={FROM} to={TO} />, { wrapper });

    expect(await screen.findByText(/did not state a won stage/)).toBeInTheDocument();
    expect(screen.getByText('Not available')).toBeInTheDocument();
  });
});

describe('OutstandingBand — the funnel', () => {
  it('draws four bars on one axis and states no share of one stage against another', async () => {
    getPipelineAnalytics.mockResolvedValue(populated());
    render(<OutstandingBand from={FROM} to={TO} />, { wrapper });

    await waitFor(() => expect(screen.getByTestId('funnel-count-leads')).toHaveTextContent('148'));
    expect(screen.getByTestId('funnel-count-accepted')).toHaveTextContent('61');
    expect(screen.getByTestId('funnel-count-quoted')).toHaveTextContent('44');
    expect(screen.getByTestId('funnel-count-won')).toHaveTextContent('12');

    // 61/148 is 41%, and the shape this band replaces printed exactly that. The stages count
    // different populations over different spans, so no ratio between them may appear.
    expect(band().textContent).not.toContain('%');
    expect(band().textContent).not.toMatch(/of previous/i);
  });

  it('names a stage whose value the server would not state, with the server\'s reason in full', async () => {
    getPipelineAnalytics.mockResolvedValue(populated());
    render(<OutstandingBand from={FROM} to={TO} />, { wrapper });

    const quoted = await screen.findByTestId('funnel-value-quoted');
    expect(quoted).toHaveTextContent('value not available');
    expect(quoted).toHaveTextContent('span three currencies with no approved rate between them');
    // Not a 0 and not a dash. The distinction is the whole product rule.
    expect(quoted.textContent).not.toMatch(/[—-]\s*$/);
    expect(screen.getByTestId('funnel-value-won')).toHaveTextContent('380,000');
  });

  it('discloses the quotes deliberately left out of every figure, with the server\'s reason', async () => {
    getPipelineAnalytics.mockResolvedValue(analytics({
      roleScope: { scope: 'assigned_accounts', ownerUserId: 7, accountTeamIds: [2], scopedUserIds: [7] },
      unownedQuotesExcluded: 6,
      unownedQuotesExcludedReason: 'Their customers have no account owner, so no scope contains them.',
    }));
    render(<OutstandingBand from={FROM} to={TO} />, { wrapper });

    const note = await screen.findByTestId('unowned-excluded');
    expect(note).toHaveTextContent('6 quotes are not in any figure on this band.');
    expect(note).toHaveTextContent('no account owner');
  });
});

describe('OutstandingBand — whose numbers, over what window', () => {
  it('heads the figures with the endpoint\'s own scope and seals the window it actually applied', async () => {
    getPipelineAnalytics.mockResolvedValue(analytics({
      roleScope: { scope: 'assigned_accounts', ownerUserId: 7, accountTeamIds: [2], scopedUserIds: [7] },
    }));
    render(<OutstandingBand from={FROM} to={TO} />, { wrapper });

    const seal = await screen.findByTestId('band-seal');
    // `windowTo` is the exclusive end, so the last day these figures cover is 5 September. Sealing
    // the bound itself would claim a day of trading the band did not count — and the assertion is
    // written in a fixed zone-independent form because the server states UTC instants.
    await waitFor(() => expect(seal).toHaveTextContent('Your assigned accounts · 8 Aug – 5 Sep'));
    expect(seal).toHaveAttribute('data-governed', 'true');
  });

  // The seal is drawn from what the server says it applied, never from what we asked for.
  it('draws an outlined seal reading All time when the server ignored the window', async () => {
    getPipelineAnalytics.mockResolvedValue(analytics({
      funnelScope: 'all_time', windowFrom: null, windowTo: null,
    }));
    render(<OutstandingBand from={FROM} to={TO} />, { wrapper });

    const seal = await screen.findByTestId('band-seal');
    await waitFor(() => expect(seal).toHaveTextContent('All time'));
    expect(seal).toHaveAttribute('data-governed', 'false');
  });

  it('never draws the weighted forecast the payload still carries', async () => {
    getPipelineAnalytics.mockResolvedValue(populated());
    render(<OutstandingBand from={FROM} to={TO} />, { wrapper });

    await waitFor(() => expect(screen.getByTestId('funnel-count-won')).toHaveTextContent('12'));
    expect(band().textContent).not.toMatch(/weighted|forecast/i);
    expect(band().textContent).not.toContain('41,200');
  });
});

/**
 * The primary case. Nexora is pre-launch, so the band a new tenant opens is this one, and it must
 * be a measured empty rather than something that looks broken: the rail, the four states, the four
 * bars and the full height all stay.
 */
describe('OutstandingBand — the empty book a new tenant opens', () => {
  it('keeps the rail, both axes and every label at full height with nothing sent', async () => {
    render(<OutstandingBand from={FROM} to={TO} />, { wrapper });

    expect(await screen.findByTestId('book-empty')).toHaveTextContent(/the book is empty rather than balanced/);
    for (const words of ['Won', 'Lost or expired', 'Supplier responded', 'Awaiting the customer']) {
      expect(screen.getAllByText(words).length).toBeGreaterThan(0);
    }
    for (const key of ['leads', 'accepted', 'quoted', 'won']) {
      expect(screen.getByTestId(`funnel-bar-${key}`)).toHaveAttribute('data-zero', 'true');
      expect(screen.getByTestId(`funnel-count-${key}`)).toHaveTextContent('0');
    }
    // Empty is not an error and not a figure the server withheld.
    expect(screen.queryByText('We could not load this')).toBeNull();
    expect(screen.queryByRole('button', { name: 'Retry' })).toBeNull();
    expect(screen.queryByText('Not available')).toBeNull();
  });
});

describe('OutstandingBand — when the aggregate cannot be loaded', () => {
  it('shows the failure with a retry and prints no zero that could read as an empty book', async () => {
    getPipelineAnalytics.mockRejectedValue(new Error('pipeline-analytics is down'));
    render(<OutstandingBand from={FROM} to={TO} />, { wrapper });

    expect(await screen.findByText('We could not load this', {}, { timeout: 4000 })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Retry' })).toBeInTheDocument();
    expect(within(band()).queryByTestId('book-empty')).toBeNull();
    expect(within(band()).queryByTestId('funnel-count-won')).toBeNull();
  });
});
