import { fireEvent, render, screen, within } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router-dom';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import TodayBand, { NOTHING_SCHEDULED_SENTENCE, axisPlacement, readDue } from './TodayBand';
import type { CommercialAttentionItem, SalesTodayDTO } from '../../../api/services/commercialIntelligenceService';
import dayjs from 'dayjs';

/**
 * What is locked down here is the band's honesty rather than its looks:
 *  - no total is ever printed, because the server's follow-up count saturates at 100;
 *  - "nothing is scheduled" is said in words, never left to read as "all clear";
 *  - a failure shows the server's reason with a retry, and looks nothing like the empty state;
 *  - the shared axis puts late on the left and still-to-come on the right, from the SERVER's clock.
 */
const navigate = vi.fn();
vi.mock('react-router-dom', async (importOriginal) => {
  const actual = await importOriginal<typeof import('react-router-dom')>();
  return { ...actual, useNavigate: () => navigate };
});

const permissions = { granted: true };
vi.mock('../../../context/AuthContext', () => ({
  useAuth: () => ({ hasPermission: () => permissions.granted }),
}));

const getSalesToday = vi.fn();
vi.mock('../../../api/services/commercialIntelligenceService', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../../../api/services/commercialIntelligenceService')>();
  return { ...actual, default: { ...actual.default, getSalesToday: () => getSalesToday() } };
});

const GENERATED_AT = '2026-09-05T09:00:00Z';

const item = (over: Partial<CommercialAttentionItem> = {}): CommercialAttentionItem => ({
  id: 1,
  recordType: 'Quotation',
  recordId: 41,
  nexoraSerial: 'NX-QUO-000041',
  reference: 'QUO-41',
  customerName: 'Aramco',
  ownerName: 'Zahid',
  reason: 'Quote awaiting customer response',
  dueAt: dayjs(GENERATED_AT).subtract(3, 'day').toISOString(),
  priority: 'Critical',
  actionRoute: '/sales/quotes/view/41',
  requiredModule: 'Quotations',
  ...over,
});

const payload = (items: CommercialAttentionItem[]): SalesTodayDTO => ({
  generatedAt: GENERATED_AT,
  scope: 'assigned_to_me',
  // The saturating open-follow-ups metric the band must never print.
  metrics: [{ key: 'open_follow_ups', label: 'Open follow-ups', value: 100, unit: 'count' } as never],
  attentionItems: items,
});

const mount = () => render(
  <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false, retryDelay: 0 } } })}>
    <MemoryRouter>
      <TodayBand />
    </MemoryRouter>
  </QueryClientProvider>,
);

beforeEach(() => {
  navigate.mockReset();
  getSalesToday.mockReset();
  permissions.granted = true;
});

describe('TodayBand rows', () => {
  it('shows at most five rows, each with its reference, customer, owner and when it is due', async () => {
    getSalesToday.mockResolvedValue(payload(Array.from({ length: 7 }, (_, i) => item({ id: i + 1, nexoraSerial: `NX-QUO-00004${i}` }))));
    mount();

    await screen.findByText('NX-QUO-000040');
    expect(screen.getAllByRole('button', { name: /NX-QUO/ })).toHaveLength(5);
    expect(screen.queryByText('NX-QUO-000045')).not.toBeInTheDocument();
    const row = screen.getAllByRole('button', { name: /NX-QUO-000040/ })[0];
    expect(row).toHaveAccessibleName(/Quote awaiting customer response/);
    expect(row).toHaveAccessibleName(/Aramco/);
    expect(row).toHaveAccessibleName(/Owner Zahid/);
    expect(row).toHaveAccessibleName(/3 days late/);
  });

  // The reason the band exists in this shape: it must never state a figure the server computes
  // off a truncated list.
  it('never prints a total, only a way through to the full list', async () => {
    getSalesToday.mockResolvedValue(payload([item()]));
    mount();

    await screen.findByText('NX-QUO-000041');
    expect(screen.queryByText('100')).not.toBeInTheDocument();
    expect(screen.queryByText(/open follow-ups/i)).not.toBeInTheDocument();
    expect(screen.getByRole('button', { name: /see all in sales today/i })).toBeInTheDocument();
  });

  it('opens the record behind a row', async () => {
    getSalesToday.mockResolvedValue(payload([item()]));
    mount();

    fireEvent.click(await screen.findByRole('button', { name: /NX-QUO-000041/ }));
    expect(navigate).toHaveBeenCalledWith('/sales/quotes/view/41');
  });

  it('does not offer a dead press when the reader lacks the record’s module', async () => {
    permissions.granted = false;
    getSalesToday.mockResolvedValue(payload([item()]));
    mount();

    await screen.findByText('NX-QUO-000041');
    expect(screen.queryByRole('button', { name: /NX-QUO-000041/ })).not.toBeInTheDocument();
    expect(screen.getByText('Permission required')).toBeInTheDocument();
  });

  it('places an overdue row left of the rule and a coming row right of it', async () => {
    getSalesToday.mockResolvedValue(payload([
      item({ id: 1, nexoraSerial: 'NX-LATE' }),
      item({ id: 2, nexoraSerial: 'NX-SOON', dueAt: dayjs(GENERATED_AT).add(2, 'day').toISOString() }),
    ]));
    mount();

    await screen.findByText('NX-LATE');
    const [late, soon] = screen.getAllByTestId('today-axis-mark');
    expect(late).toHaveAttribute('data-overdue', 'true');
    expect(soon).toHaveAttribute('data-overdue', 'false');
    expect(screen.getByText('3 days late')).toBeInTheDocument();
    expect(screen.getByText('due in 2 days')).toBeInTheDocument();
  });

  it('draws the axis but no mark for a row with no date, and says so in words', async () => {
    getSalesToday.mockResolvedValue(payload([item({ dueAt: null })]));
    mount();

    await screen.findByText('NX-QUO-000041');
    expect(screen.getByText('No date set')).toBeInTheDocument();
    expect(screen.queryByTestId('today-axis-mark')).not.toBeInTheDocument();
  });

  it('measures the axis from the server’s clock, not this device’s', async () => {
    // A payload generated years ago, carrying a date five days after that. Against the device
    // clock it is long overdue; against the clock the seal states, it is still to come.
    const stale = '2020-01-01T09:00:00Z';
    getSalesToday.mockResolvedValue({
      ...payload([item({ dueAt: dayjs(stale).add(5, 'day').toISOString() })]),
      generatedAt: stale,
    });
    mount();

    await screen.findByText('NX-QUO-000041');
    expect(screen.getByTestId('today-axis-mark')).toHaveAttribute('data-overdue', 'false');
    expect(screen.getByText('due in 5 days')).toBeInTheDocument();
  });
});

describe('TodayBand empty', () => {
  it('says nothing is scheduled rather than letting an empty band read as all clear', async () => {
    getSalesToday.mockResolvedValue(payload([]));
    mount();

    expect(await screen.findByText(NOTHING_SCHEDULED_SENTENCE)).toBeInTheDocument();
    expect(NOTHING_SCHEDULED_SENTENCE).toContain('That is not the same as nothing to do');
    // Calm outline, not an alert: "nothing happened" and "we could not load" are different designs.
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Retry' })).not.toBeInTheDocument();
  });

  it('keeps the axis and its labels when there is nothing to plot', async () => {
    getSalesToday.mockResolvedValue(payload([]));
    mount();

    await screen.findByText(NOTHING_SCHEDULED_SENTENCE);
    expect(screen.getByText('Late')).toBeInTheDocument();
    expect(screen.getByText('Now')).toBeInTheDocument();
    expect(screen.getByText('To come')).toBeInTheDocument();
  });

  it('offers the unassigned enquiries, which is where the work actually is', async () => {
    getSalesToday.mockResolvedValue(payload([]));
    mount();

    fireEvent.click(await screen.findByRole('button', { name: /see unassigned enquiries/i }));
    expect(navigate).toHaveBeenCalledWith('/sales/routing');
  });
});

describe('TodayBand unavailable', () => {
  it('shows the server’s reason with a retry, and no rows', async () => {
    getSalesToday.mockRejectedValue({
      response: { status: 503, data: { detail: 'Sales today is warming up.' } },
      config: { url: '/api/commercial-intelligence/sales-today' },
      request: {},
    });
    mount();

    const alert = await screen.findByRole('alert');
    expect(within(alert).getByText(/temporarily unavailable|not responding/i)).toBeInTheDocument();
    expect(screen.queryByText(NOTHING_SCHEDULED_SENTENCE)).not.toBeInTheDocument();

    getSalesToday.mockResolvedValue(payload([item()]));
    fireEvent.click(screen.getByRole('button', { name: 'Retry' }));
    expect(await screen.findByText('NX-QUO-000041')).toBeInTheDocument();
  });

  it('states a refusal calmly and offers no retry', async () => {
    getSalesToday.mockRejectedValue({
      response: { status: 403, data: { detail: 'Not yours.' } },
      config: { url: '/api/commercial-intelligence/sales-today' },
      request: {},
    });
    mount();

    expect(await screen.findByText(/does not permit/i)).toBeInTheDocument();
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Retry' })).not.toBeInTheDocument();
  });
});

describe('TodayBand axis maths', () => {
  const now = dayjs(GENERATED_AT);

  it('reads a due date into the row’s own words', () => {
    expect(readDue(dayjs(GENERATED_AT).subtract(1, 'day').toISOString(), now)?.text).toBe('1 day late');
    expect(readDue(dayjs(GENERATED_AT).add(90, 'minute').toISOString(), now)?.text).toBe('due in 2 h');
    expect(readDue(dayjs(GENERATED_AT).add(20, 'minute').toISOString(), now)?.text).toBe('due in 20 min');
    expect(readDue(null, now)).toBeNull();
    // DateTime.MinValue must not plant a dot two thousand years overdue.
    expect(readDue('0001-01-01T00:00:00', now)).toBeNull();
  });

  it('clamps beyond the axis instead of claiming a position it does not have', () => {
    expect(axisPlacement(0)).toEqual({ x: 60, clamped: false });
    expect(axisPlacement(-14).x).toBeCloseTo(8);
    expect(axisPlacement(14).x).toBeCloseTo(112);
    expect(axisPlacement(40)).toEqual({ x: 112, clamped: true });
    expect(axisPlacement(-40)).toEqual({ x: 8, clamped: true });
  });
});
