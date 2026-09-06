import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, waitFor, within } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import commercialIntelligenceService, {
  type PerformanceDTO,
} from '../../../api/services/commercialIntelligenceService';
import dashboardService, { type Release01DashboardDTO } from '../../../api/services/dashboardService';
import VerdictBand from './VerdictBand';

const FROM = '2026-08-07';
const TO = '2026-09-06';
/** priorWindow(7 Aug – 6 Sep) is the 31 days that end the day before it starts. */
const PRIOR_FROM = '2026-07-07';

const performance = (over: Partial<PerformanceDTO> = {}): PerformanceDTO => ({
  generatedAt: '2026-09-06T09:15:00Z',
  from: FROM,
  to: TO,
  scope: 'assigned_to_me',
  minimumConversionSample: 5,
  metrics: [
    { key: 'won', label: 'Won', value: 3, unit: 'count' },
    { key: 'lost', label: 'Lost', value: 2, unit: 'count' },
    { key: 'decided', label: 'Decided outcomes', value: 5, unit: 'count' },
  ],
  outcomeReconciliation: {
    recordedOutcomes: 5,
    attributedOutcomes: 5,
    unattributedOutcomes: 0,
    completenessPercent: 100,
    isTenantComplete: false,
  },
  representatives: [],
  ...over,
});

const counts = (won: number, lost: number, decided: number): Pick<PerformanceDTO, 'metrics'> => ({
  metrics: [
    { key: 'won', label: 'Won', value: won, unit: 'count' },
    { key: 'lost', label: 'Lost', value: lost, unit: 'count' },
    { key: 'decided', label: 'Decided outcomes', value: decided, unit: 'count' },
  ],
});

const release = (over: Partial<Release01DashboardDTO> = {}): Release01DashboardDTO => ({
  definitionVersion: 'release-01',
  generatedAt: '2026-09-06T09:15:00Z',
  filter: { from: FROM, to: TO, boundary: '[from,to)' },
  roleScope: { scope: 'assigned_accounts', accountTeamIds: [4] },
  kpis: [
    {
      key: 'leads_received',
      label: 'Leads received',
      value: 12,
      state: 'available',
      unit: 'count',
      definition: 'Leads created in the window.',
      drillDownIdentifiers: [],
    },
  ],
  ...over,
});

/** The selected window resolves first, the window before it second. */
const servePerformance = (current: PerformanceDTO | Error, previous: PerformanceDTO | Error) =>
  vi.spyOn(commercialIntelligenceService, 'getPerformance').mockImplementation(async (from: string) => {
    const answer = from === PRIOR_FROM ? previous : current;
    if (answer instanceof Error) throw answer;
    return answer;
  });

const renderBand = () =>
  render(
    <MemoryRouter>
      <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retryDelay: 0 } } })}>
        <VerdictBand from={FROM} to={TO} />
      </QueryClientProvider>
    </MemoryRouter>,
  );

beforeEach(() => {
  vi.restoreAllMocks();
});

describe('VerdictBand, populated', () => {
  it('reads as one sentence built only from figures the server stated', async () => {
    servePerformance(performance(), performance({ ...counts(1, 4, 5), from: PRIOR_FROM, to: '2026-08-06' }));
    vi.spyOn(dashboardService, 'getRelease01').mockResolvedValue(release());

    renderBand();

    expect(await screen.findByTestId('verdict-sentence')).toHaveTextContent(
      '12 leads received. Your accounts decided 5 quotes between 7 Aug and 6 Sep, and 3 went your way.',
    );
    expect(screen.getByTestId('band-seal')).toHaveTextContent('Your assigned accounts · 7 Aug – 6 Sep');
  });

  it('draws the prior window on the same axis and states no delta of any kind', async () => {
    servePerformance(performance(), performance({ ...counts(1, 4, 5), from: PRIOR_FROM, to: '2026-08-06' }));
    vi.spyOn(dashboardService, 'getRelease01').mockResolvedValue(release());

    renderBand();

    // Both rows exist, both are on the shared scale of 4 (the longest bar anywhere): won 3 is
    // 75% of the half-track, the prior lost 4 is the full 100%.
    await waitFor(() => expect(screen.getByTestId('current-won-bar')).toHaveAttribute('data-length', '75%'));
    expect(screen.getByTestId('current-lost-bar')).toHaveAttribute('data-length', '50%');
    expect(screen.getByTestId('ghost-won-bar')).toHaveAttribute('data-length', '25%');
    expect(screen.getByTestId('ghost-lost-bar')).toHaveAttribute('data-length', '100%');
    expect(screen.getByTestId('verdict-ghost-note')).toHaveTextContent(
      'The thinner bar is the 31 days before, 7 Jul – 6 Aug. Both bars use the same scale.',
    );

    // The band's whole reason for existing: two lengths, never a computed change. Nothing on it
    // may render a percentage change or an arrow.
    const band = screen.getByRole('region', { name: 'Did we win what we decided?' });
    expect(band.textContent).not.toMatch(/[+-]\s?\d+(\.\d+)?%/);
    expect(band.textContent).not.toMatch(/[↑↓▲▼]|\bup\b|\bdown\b|vs\.?\s|versus/i);
  });

  it('never divides won by decided — the conversion slot shows the sample against the server minimum', async () => {
    servePerformance(performance(), performance({ ...counts(1, 4, 5), from: PRIOR_FROM, to: '2026-08-06' }));
    vi.spyOn(dashboardService, 'getRelease01').mockResolvedValue(release());

    renderBand();

    expect(await screen.findByText('A win rate is published once 5 quotes have been decided. You have 5.')).toBeInTheDocument();
    // 3 won of 5 decided would be 60%: that figure exists nowhere on the band.
    expect(screen.getByRole('region', { name: 'Did we win what we decided?' }).textContent).not.toMatch(/60\s?%/);
    expect(screen.getAllByTestId('pip-filled')).toHaveLength(5);
  });

  it('leaves the leads clause out when the two endpoints scope themselves differently', async () => {
    servePerformance(performance(), performance({ ...counts(1, 4, 5), from: PRIOR_FROM, to: '2026-08-06' }));
    vi.spyOn(dashboardService, 'getRelease01').mockResolvedValue(
      release({ roleScope: { scope: 'tenant', accountTeamIds: [] } }),
    );

    renderBand();

    const sentence = await screen.findByTestId('verdict-sentence');
    await waitFor(() => expect(sentence).toHaveTextContent(/^Your accounts decided 5 quotes/));
    expect(sentence).not.toHaveTextContent('12 leads received');
  });
});

describe('VerdictBand, empty — the primary case', () => {
  const serveEmpty = () => {
    servePerformance(
      performance(counts(0, 0, 0)),
      performance({ ...counts(0, 0, 0), from: PRIOR_FROM, to: '2026-08-06' }),
    );
    vi.spyOn(dashboardService, 'getRelease01').mockResolvedValue(
      release({
        kpis: [
          {
            key: 'leads_received',
            label: 'Leads received',
            value: null,
            state: 'insufficient_data',
            unit: 'count',
            definition: 'Leads created in the window.',
            insufficientDataReason: 'No leads have been received yet.',
            drillDownIdentifiers: [],
          },
        ],
      }),
    );
  };

  it('says nothing happened, in the server-stated window, with no number invented', async () => {
    serveEmpty();

    renderBand();

    expect(await screen.findByTestId('verdict-sentence')).toHaveTextContent(
      'Nothing has been marked won or lost for your accounts between 7 Aug and 6 Sep.',
    );
  });

  it('keeps the axis, the centre rule and a stated zero at each end', async () => {
    serveEmpty();

    renderBand();

    const axis = await screen.findByTestId('verdict-axis');
    expect(screen.getByTestId('verdict-centre-rule')).toBeInTheDocument();
    // Zero is a figure the server stated, so it is printed — at both ends, against its own label.
    expect(within(axis).getAllByText('0').length).toBeGreaterThanOrEqual(2);
    expect(within(axis).getByText('Won')).toBeInTheDocument();
    expect(within(axis).getByText('Lost')).toBeInTheDocument();
    // Zero-length bars, not absent bars: the geometry is unchanged, only the lengths are.
    expect(screen.getByTestId('current-won-bar')).toHaveAttribute('data-length', '0%');
    expect(screen.getByTestId('ghost-lost-bar')).toHaveAttribute('data-length', '0%');
    // Calm, not alarming: an empty window is not a failure.
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
  });

  it('shows an empty sample and one way to fix it', async () => {
    serveEmpty();

    renderBand();

    expect(await screen.findByText('A win rate is published once 5 quotes have been decided. You have 0.')).toBeInTheDocument();
    expect(screen.getAllByTestId('pip-empty')).toHaveLength(5);
    expect(screen.queryAllByTestId('pip-filled')).toHaveLength(0);
    expect(screen.getByRole('link', { name: 'Record an outcome' })).toHaveAttribute('href', '/sales/quotes?state=sent');
  });
});

describe('VerdictBand, not stated and not loaded', () => {
  it('renders the sentence the error boundary allows, and a Retry, when the window fails', async () => {
    servePerformance(
      Object.assign(new Error('Request failed'), {
        isAxiosError: true,
        response: { status: 400, data: { message: 'That window is longer than this report accepts.' } },
      }),
      performance({ ...counts(1, 4, 5), from: PRIOR_FROM, to: '2026-08-06' }),
    );
    vi.spyOn(dashboardService, 'getRelease01').mockResolvedValue(release());

    renderBand();

    const alert = await screen.findByRole('alert', {}, { timeout: 4000 });
    expect(alert).toHaveTextContent('That window is longer than this report accepts.');
    expect(screen.getByRole('button', { name: 'Retry' })).toBeInTheDocument();
    // A failure looks nothing like an empty window: no axis, no zeros to misread as counts.
    expect(screen.queryByTestId('verdict-axis')).not.toBeInTheDocument();
    expect(screen.queryByTestId('verdict-sentence')).not.toBeInTheDocument();
    // The seal still states whose numbers these would have been and how fresh they are not.
    expect(screen.getByTestId('band-seal')).toHaveTextContent('Scope not stated · 7 Aug – 6 Sep · freshness not stated');
  });

  // `toPresentableError` refuses to print a 5xx body — those are exception dumps far more often
  // than product copy — so the band must not pass a fallback sentence of its own either, or an
  // outage would read as a vaguer message than the boundary already has for it.
  it('defers to the boundary wording on a 5xx rather than a house sentence', async () => {
    servePerformance(
      Object.assign(new Error('Request failed'), {
        isAxiosError: true,
        response: { status: 503, data: { message: 'NullReferenceException at Nexora.Reporting' } },
      }),
      performance({ ...counts(1, 4, 5), from: PRIOR_FROM, to: '2026-08-06' }),
    );
    vi.spyOn(dashboardService, 'getRelease01').mockResolvedValue(release());

    renderBand();

    const alert = await screen.findByRole('alert', {}, { timeout: 4000 });
    expect(alert).toHaveTextContent('This part of Nexora is not responding right now. Your data is safe — try again shortly.');
    expect(alert).not.toHaveTextContent('NullReferenceException');
  });

  it('says the figure is not available, never zero, when the server states no won or lost row', async () => {
    servePerformance(
      performance({ metrics: [{ key: 'quote_sent', label: 'Quotes sent', value: 9, unit: 'count' }] }),
      performance({ ...counts(1, 4, 5), from: PRIOR_FROM, to: '2026-08-06' }),
    );
    vi.spyOn(dashboardService, 'getRelease01').mockResolvedValue(release());

    renderBand();

    // Both figures the band lives on are unstated, so both slots say so — in the server's terms,
    // with no zero standing in for either.
    expect(await screen.findAllByText('Not available')).toHaveLength(2);
    expect(screen.getByText('The server did not state won or lost counts for this window.')).toBeInTheDocument();
    expect(
      screen.getByText(/did not state how many quotes were decided in this window/),
    ).toBeInTheDocument();
    // The axis keeps its height behind the reason, but it is inert and states no numbers.
    expect(screen.queryByRole('img', { name: /Won/ })).not.toBeInTheDocument();
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
  });

  it('keeps the current window when only the prior window fails', async () => {
    servePerformance(
      performance(),
      Object.assign(new Error('Request failed'), {
        isAxiosError: true,
        response: { status: 400, data: { message: 'The previous period could not be aggregated.' } },
      }),
    );
    vi.spyOn(dashboardService, 'getRelease01').mockResolvedValue(release());

    renderBand();

    expect(await screen.findByTestId('verdict-sentence')).toHaveTextContent('and 3 went your way.');
    await waitFor(() =>
      expect(screen.getByTestId('verdict-ghost-note')).toHaveTextContent('The previous period could not be aggregated.'),
    );
    // No ghost counts are drawn for a window we do not have — and no zeros stand in for them.
    expect(screen.getByTestId('ghost-won-bar')).toHaveAttribute('data-length', '0%');
    expect(screen.getByTestId('current-won-bar')).toHaveAttribute('data-length', '100%');
  });
});

describe('VerdictBand never renders the removed forecast', () => {
  it('shows no weighted or predicted figure anywhere', async () => {
    servePerformance(performance(), performance({ ...counts(1, 4, 5), from: PRIOR_FROM, to: '2026-08-06' }));
    vi.spyOn(dashboardService, 'getRelease01').mockResolvedValue(release());

    renderBand();

    await screen.findByTestId('verdict-sentence');
    expect(screen.getByRole('region', { name: 'Did we win what we decided?' }).textContent).not.toMatch(
      /weighted|forecast|predict|likely to close/i,
    );
  });
});
