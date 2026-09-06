import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router-dom';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import dayjs from 'dayjs';
import DashboardPage from './DashboardPage';
import { NOTHING_SCHEDULED_SENTENCE } from './glance/TodayBand';
import type { PerformanceDTO, SalesTodayDTO } from '../../api/services/commercialIntelligenceService';
import type {
  DashboardDataDTO,
  DeadlineBoardDTO,
  Release01DashboardDTO,
} from '../../api/services/dashboardService';

/**
 * What this file pins is the SCREEN rather than any band: the story order, the fact that the bands
 * are independent of one another, and the three product rules that only composition can break —
 * a band that blanks its neighbours, a removed figure that creeps back in, and a period control
 * that claims to govern more than it does.
 *
 * The empty case is the primary case (Nexora is pre-launch), so every band's empty state is
 * asserted together, on one screen, in the state a new tenant actually opens.
 */
const auth = vi.hoisted(() => ({ businessUnitId: 3 as number | undefined, grants: null as Set<string> | null }));
vi.mock('../../context/AuthContext', () => ({
  useAuth: () => ({
    userData: { businessUnitId: auth.businessUnitId, isManager: false },
    hasPermission: (moduleName: string) => auth.grants === null || auth.grants.has(moduleName),
    hasEntitlement: () => false,
  }),
}));

const getPerformance = vi.fn();
const getSalesToday = vi.fn();
vi.mock('../../api/services/commercialIntelligenceService', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../../api/services/commercialIntelligenceService')>();
  return {
    ...actual,
    default: {
      ...actual.default,
      getPerformance: (from: string, to: string) => getPerformance(from, to),
      getSalesToday: () => getSalesToday(),
    },
  };
});

const getRelease01 = vi.fn();
const getDeadlineBoard = vi.fn();
const getDashboard = vi.fn();
vi.mock('../../api/services/dashboardService', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../../api/services/dashboardService')>();
  return {
    ...actual,
    default: {
      ...actual.default,
      getRelease01: (params: unknown) => getRelease01(params),
      getDeadlineBoard: (params: unknown) => getDeadlineBoard(params),
      getDashboard: (businessUnitId: number) => getDashboard(businessUnitId),
    },
  };
});

const TODAY = dayjs().startOf('day');
const iso = (value: dayjs.Dayjs) => value.format('YYYY-MM-DD');

const performance = (over: Partial<PerformanceDTO> = {}): PerformanceDTO => ({
  generatedAt: '2026-09-06T09:15:00Z',
  from: iso(TODAY.subtract(29, 'day')),
  to: iso(TODAY),
  scope: 'tenant',
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

const emptyPerformance = (): PerformanceDTO => performance({
  metrics: [
    { key: 'won', label: 'Won', value: 0, unit: 'count' },
    { key: 'lost', label: 'Lost', value: 0, unit: 'count' },
    { key: 'decided', label: 'Decided outcomes', value: 0, unit: 'count' },
  ],
});

const release = (): Release01DashboardDTO => ({
  definitionVersion: 'release-01',
  generatedAt: '2026-09-06T09:15:00Z',
  filter: { from: iso(TODAY.subtract(29, 'day')), to: iso(TODAY), boundary: '[from,to)' },
  roleScope: { scope: 'tenant', accountTeamIds: [] },
  kpis: [],
});

const board = (over: Partial<DeadlineBoardDTO> = {}): DeadlineBoardDTO => ({
  generatedAt: '2026-09-06T09:15:00Z',
  openLeads: 0,
  openLineItems: 0,
  leadsWithoutClosingDate: 0,
  lateIngestedExcludedLeads: 0,
  buckets: [
    { key: 'overdue', label: 'Past deadline', leads: 0, lineItems: 0 },
    { key: 'today', label: 'Closing today', leads: 0, lineItems: 0 },
    { key: 'days_1_3', label: '1–3 days', leads: 0, lineItems: 0 },
    { key: 'days_4_7', label: '4–7 days', leads: 0, lineItems: 0 },
    { key: 'days_8_30', label: '8–30 days', leads: 0, lineItems: 0 },
    { key: 'later', label: 'More than 30 days', leads: 0, lineItems: 0 },
    { key: 'unknown', label: 'No closing date', leads: 0, lineItems: 0 },
  ],
  leads: [],
  ...over,
});

const salesToday = (items: SalesTodayDTO['attentionItems']): SalesTodayDTO => ({
  generatedAt: '2026-09-06T09:00:00Z',
  scope: 'tenant',
  // The saturating open-follow-ups metric. It is served here precisely so the screen can be
  // asserted never to print it: the server counts follow-ups after `.Take(100)`.
  metrics: [{ key: 'open_follow_ups', label: 'Open follow-ups', value: 100, unit: 'count' } as never],
  attentionItems: items,
});

/** The band reads `volumeTrend` and nothing else on this payload, so the rest is not invented. */
const monthly = (rows: { month: string; count: number; value: number }[]): DashboardDataDTO =>
  ({ volumeTrend: rows } as unknown as DashboardDataDTO);

const renderPage = () =>
  render(
    <MemoryRouter>
      <QueryClientProvider
        client={new QueryClient({ defaultOptions: { queries: { retry: false, retryDelay: 0 } } })}
      >
        <DashboardPage />
      </QueryClientProvider>
    </MemoryRouter>,
  );

const bandTitles = () =>
  screen.getAllByRole('heading', { level: 2 }).map((heading) => heading.textContent);

beforeEach(() => {
  vi.clearAllMocks();
  auth.businessUnitId = 3;
  auth.grants = null;
  getPerformance.mockResolvedValue(performance());
  getRelease01.mockResolvedValue(release());
  getDeadlineBoard.mockResolvedValue(board());
  getSalesToday.mockResolvedValue(salesToday([]));
  getDashboard.mockResolvedValue(monthly([{ month: '2026-09', count: 4, value: 1200 }]));
});

describe('the dashboard reads as one sentence', () => {
  it('puts the bands in the story order, and leaves bands 2 and 3 unbuilt rather than broken', async () => {
    renderPage();

    await waitFor(() => expect(bandTitles()).toEqual([
      'Did we win what we decided?',
      "What's closing on us",
      'What needs you today',
      'The last six months',
    ]));
    // The seam for "what's out there" and "why we lost" is a comment, not a card: a placeholder
    // would be one more thing on the screen that looks like a band which failed to load.
    expect(screen.queryByText(/coming soon|not yet available|placeholder/i)).toBeNull();
  });

  it('states whose numbers these are in the server\'s own words, and offers nothing to press there', async () => {
    renderPage();

    const strip = screen.getByLabelText('Whose numbers, and over what period');
    const sentence = within(strip).getByTestId('scope-sentence');
    await waitFor(() => expect(sentence).toHaveTextContent('Company-wide — every account in this workspace'));
    // The scope is the server's answer, not the reader's choice. Nothing inside the sentence is
    // pressable, so it cannot read as a filter somebody forgot to set.
    expect(within(sentence).queryByRole('button')).toBeNull();
  });

  it('says the scope is not stated, with the reason, rather than guessing one', async () => {
    getPerformance.mockResolvedValue(performance({ scope: 'region_west' as never }));
    renderPage();

    const sentence = screen.getByTestId('scope-sentence');
    await waitFor(() => expect(sentence).toHaveTextContent('Scope not stated'));
    expect(sentence).toHaveTextContent('The server did not name a scope for these figures.');
    expect(sentence.textContent).not.toContain('region_west');
  });

  it('names the band the dates govern, next to the dates', async () => {
    renderPage();

    expect(await screen.findByText('Dates govern · Did we win')).toBeInTheDocument();
  });
});

describe('the period control', () => {
  it('moves the governed band to the chosen period in one press', async () => {
    renderPage();
    await waitFor(() => expect(getPerformance).toHaveBeenCalled());

    fireEvent.click(screen.getByRole('button', { name: 'Last 90 days' }));

    // Inclusive of both ends: today and the eighty-nine days before it.
    await waitFor(() =>
      expect(getPerformance).toHaveBeenCalledWith(iso(TODAY.subtract(89, 'day')), iso(TODAY)));
  });

  it('leaves the bands with their own fixed windows alone', async () => {
    renderPage();
    await waitFor(() => expect(getDeadlineBoard).toHaveBeenCalledTimes(1));

    fireEvent.click(screen.getByRole('button', { name: 'This year' }));
    await waitFor(() => expect(getPerformance).toHaveBeenCalledWith(iso(TODAY.startOf('year')), iso(TODAY)));

    // The deadline board and sales-today take no window at all. If the chips refetched them, the
    // strip's promise about which band the dates govern would be false.
    expect(getDeadlineBoard).toHaveBeenCalledTimes(1);
    expect(getSalesToday).toHaveBeenCalledTimes(1);
  });

  it('keeps the last usable window when a custom range is inverted, and says so', async () => {
    renderPage();
    await waitFor(() => expect(getPerformance).toHaveBeenCalled());
    getPerformance.mockClear();

    fireEvent.click(screen.getByRole('button', { name: /Custom/ }));
    fireEvent.change(screen.getByLabelText('To'), { target: { value: iso(TODAY.subtract(400, 'day')) } });

    expect(await screen.findByText(/start date must be on or before the end date/)).toBeInTheDocument();
    expect(getPerformance).not.toHaveBeenCalledWith(expect.anything(), iso(TODAY.subtract(400, 'day')));
  });
});

describe('one band failing leaves the rest of the screen standing', () => {
  it('shows the failed band its own alert and keeps every other band\'s figures', async () => {
    getSalesToday.mockRejectedValue(new Error('sales-today is down'));
    getDeadlineBoard.mockResolvedValue(board({
      openLeads: 4,
      buckets: board().buckets.map((bucket) =>
        (bucket.key === 'overdue' ? { ...bucket, leads: 4, lineItems: 9 } : bucket)),
    }));
    renderPage();

    expect(await screen.findByText('We could not load this')).toBeInTheDocument();
    // Exactly one band failed, so exactly one band says so.
    expect(screen.getAllByText('We could not load this')).toHaveLength(1);

    // The four bands are all still on the screen, and the ones that loaded still show their data.
    expect(bandTitles()).toHaveLength(4);
    expect(await screen.findByText(/3 went our way/)).toBeInTheDocument();
    expect(screen.getByLabelText(/Past deadline/)).toBeInTheDocument();
  });

  it('does not let a failed band print a zero that reads as good news', async () => {
    getDeadlineBoard.mockRejectedValue(new Error('deadline board is down'));
    renderPage();

    const closing = await screen.findByLabelText("What's closing on us");
    expect(await within(closing).findByText('We could not load this')).toBeInTheDocument();
    expect(within(closing).queryByText(/Nothing is scheduled yet/)).toBeNull();
    expect(within(closing).queryByText('0')).toBeNull();
  });
});

describe('the empty screen a new tenant opens', () => {
  it('keeps all four bands at full height, each saying what has not happened yet', async () => {
    getPerformance.mockResolvedValue(emptyPerformance());
    getDashboard.mockResolvedValue(monthly([]));
    renderPage();

    expect(await screen.findByText(/Nothing has been marked won or lost/)).toBeInTheDocument();
    expect(screen.getByText(/Nothing is scheduled yet/)).toBeInTheDocument();
    expect(screen.getByText(NOTHING_SCHEDULED_SENTENCE)).toBeInTheDocument();
    expect(screen.getByText('No requests or orders were recorded in the last six months.')).toBeInTheDocument();

    // Empty is not an error anywhere on the screen: no Alert, nothing to retry.
    expect(bandTitles()).toHaveLength(4);
    expect(screen.queryByText('We could not load this')).toBeNull();
    expect(screen.queryByRole('button', { name: 'Retry' })).toBeNull();
  });

  it('states why the six-month history cannot be asked for, instead of drawing an empty one', async () => {
    auth.businessUnitId = undefined;
    renderPage();

    expect(await screen.findByText(/carries no business unit/)).toBeInTheDocument();
    expect(getDashboard).not.toHaveBeenCalled();
    // Nothing to retry: the same question would be just as unanswerable a second time.
    expect(screen.queryByRole('button', { name: 'Retry' })).toBeNull();
  });
});

describe('the figures this screen refuses to show', () => {
  it('never renders the weighted forecast, a follow-up total, or a trend flag', async () => {
    getSalesToday.mockResolvedValue(salesToday([
      {
        id: 1,
        recordType: 'Quotation',
        recordId: 41,
        nexoraSerial: 'NX-QUO-000041',
        reference: 'QUO-41',
        customerName: 'Aramco',
        ownerName: 'Zahid',
        reason: 'Quote awaiting customer response',
        dueAt: dayjs('2026-09-06T09:00:00Z').add(2, 'day').toISOString(),
        priority: 'Due',
        actionRoute: '/sales/quotes/view/41',
        requiredModule: 'Quotations',
      },
    ]));
    renderPage();
    await screen.findByText(/3 went our way/);

    const page = document.body.textContent ?? '';
    // The 0.3/0.5 heuristic, removed from the product rather than relabelled.
    expect(page).not.toMatch(/weighted/i);
    // The server's own follow-up count saturates at exactly 100, so no total is printed anywhere.
    expect(page).not.toContain('Open follow-ups');
    // `CalculateTrend` reports a literal "100%" up against a zero previous period, which pre-launch
    // is the default. No band renders a trend flag, so the string cannot appear.
    expect(page).not.toContain('100%');
  });

  it('offers only the deeper screens the reader\'s own grants would open', async () => {
    auth.grants = new Set(['Dashboard']);
    renderPage();
    await waitFor(() => expect(getPerformance).toHaveBeenCalled());

    expect(screen.getByRole('button', { name: 'Performance by rep' })).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Every deadline in full' })).toBeNull();
    expect(screen.queryByRole('button', { name: 'Documents to check' })).toBeNull();
  });
});
