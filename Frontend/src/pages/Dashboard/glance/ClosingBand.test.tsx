import type { ReactNode } from 'react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import ClosingBand, { CLOSING_COLUMNS } from './ClosingBand';
import type { DeadlineBoardDTO } from '../../../api/services/dashboardService';

const getDeadlineBoard = vi.fn();
vi.mock('../../../api/services/dashboardService', () => ({
  default: { getDeadlineBoard: (...args: unknown[]) => getDeadlineBoard(...args) },
}));

const navigate = vi.fn();
vi.mock('react-router-dom', async (importOriginal) => {
  const actual = await importOriginal<typeof import('react-router-dom')>();
  return { ...actual, useNavigate: () => navigate };
});

const wrapper = ({ children }: { children: ReactNode }) => {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return <QueryClientProvider client={client}>{children}</QueryClientProvider>;
};

/** The server's own bucket list, in its own order, with its own labels. */
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

/** A tenant with real work: overdue, some near, some far, and two with no date at all. */
const populated = (): DeadlineBoardDTO => board({
  openLeads: 27,
  openLineItems: 412,
  leadsWithoutClosingDate: 2,
  lateIngestedExcludedLeads: 3,
  buckets: [
    { key: 'overdue', label: 'Past deadline', leads: 4, lineItems: 51 },
    { key: 'today', label: 'Closing today', leads: 1, lineItems: 9 },
    { key: 'days_1_3', label: '1–3 days', leads: 6, lineItems: 88 },
    { key: 'days_4_7', label: '4–7 days', leads: 0, lineItems: 0 },
    { key: 'days_8_30', label: '8–30 days', leads: 12, lineItems: 220 },
    { key: 'later', label: 'More than 30 days', leads: 2, lineItems: 30 },
    { key: 'unknown', label: 'No closing date', leads: 2, lineItems: 14 },
  ],
});

const barFor = (key: string) => screen.getByTestId(`closing-bar-${key}`);
const valueFor = (key: string) => screen.getByTestId(`closing-value-${key}`);

beforeEach(() => {
  vi.clearAllMocks();
  getDeadlineBoard.mockResolvedValue(board());
});

describe('ClosingBand — populated', () => {
  it('draws every bucket the server sent, with its count and its line total', async () => {
    getDeadlineBoard.mockResolvedValue(populated());
    render(<ClosingBand />, { wrapper });

    await waitFor(() => expect(valueFor('overdue')).toHaveTextContent('4'));
    expect(valueFor('days_1_3')).toHaveTextContent('6');
    expect(valueFor('days_8_30')).toHaveTextContent('12');
    expect(screen.getByText('Past deadline')).toBeInTheDocument();
    expect(screen.getByText('220 lines')).toBeInTheDocument();
    expect(screen.getByText(/27 open enquiries carrying 412 lines\./)).toBeInTheDocument();
  });

  // The band's core promise. A bucket that happens to be empty this morning keeps its slot, so the
  // column a rep has learned to look at does not move under her when the data changes.
  it('keeps a zero column in place as a baseline tick with its label and its 0', async () => {
    getDeadlineBoard.mockResolvedValue(populated());
    render(<ClosingBand />, { wrapper });

    await waitFor(() => expect(valueFor('days_4_7')).toHaveTextContent('0'));
    expect(barFor('days_4_7')).toHaveAttribute('data-zero', 'true');
    expect(barFor('days_4_7')).toHaveStyle({ height: '3px' });
    expect(screen.getByText('4–7 days')).toBeInTheDocument();
    // The tallest column is the scale, and it is not a tick.
    expect(barFor('days_8_30')).toHaveAttribute('data-zero', 'false');
    expect(barFor('days_8_30')).toHaveStyle({ height: '148px' });
  });

  // The acceptance test for the whole screen: a figure that cannot open the rows it counted does
  // not ship.
  it('opens the enquiries a column counted', async () => {
    getDeadlineBoard.mockResolvedValue(populated());
    render(<ClosingBand />, { wrapper });

    await waitFor(() => expect(valueFor('overdue')).toHaveTextContent('4'));
    fireEvent.click(screen.getByRole('button', { name: /Past deadline: 4 open enquiries, 51 lines/ }));

    expect(navigate).toHaveBeenCalledWith('/analytics/deadlines?bucket=overdue');
  });

  it('states the late-arrival caveat the server published rather than absorbing it', async () => {
    getDeadlineBoard.mockResolvedValue(populated());
    render(<ClosingBand />, { wrapper });

    expect(await screen.findByText(/3 of them reached Nexora after their/)).toBeInTheDocument();
  });

  // A bucket key from a newer server must not vanish into the gaps between our fixed columns.
  it('discloses buckets it does not know how to draw', async () => {
    getDeadlineBoard.mockResolvedValue(board({
      openLeads: 5,
      openLineItems: 40,
      buckets: [...board().buckets, { key: 'next_quarter', label: 'Next quarter', leads: 5, lineItems: 40 }],
    }));
    render(<ClosingBand />, { wrapper });

    expect(await screen.findByText(/1 group of enquiries this\s+screen does not yet know how to show/)).toBeInTheDocument();
  });

  // The board looks forward from today over everything open and takes no from/to, so the period
  // control above it does not reach this band and the seal must not claim otherwise.
  it('seals itself as a fixed window with the server freshness', async () => {
    getDeadlineBoard.mockResolvedValue(populated());
    render(<ClosingBand />, { wrapper });

    await waitFor(() => expect(valueFor('overdue')).toHaveTextContent('4'));
    const seal = screen.getByTestId('band-seal');
    expect(seal).toHaveAttribute('data-governed', 'false');
    expect(seal).toHaveTextContent('Every open enquiry');
    expect(seal).toHaveTextContent(/as of \d{2}:\d{2}/);
  });
});

describe('ClosingBand — empty', () => {
  it('holds all seven columns at full height with a 0 above each', async () => {
    render(<ClosingBand />, { wrapper });

    await waitFor(() => expect(valueFor('overdue')).toHaveTextContent('0'));
    for (const column of CLOSING_COLUMNS) {
      expect(valueFor(column.key)).toHaveTextContent('0');
      expect(barFor(column.key)).toHaveAttribute('data-zero', 'true');
      expect(barFor(column.key)).toHaveStyle({ height: '3px' });
    }
    expect(screen.getByText('No closing date')).toBeInTheDocument();
    expect(screen.getAllByRole('button')).toHaveLength(CLOSING_COLUMNS.length);
  });

  // "All clear" would be the screen's first lie: on a pre-launch tenant an empty urgency board
  // means nothing is scheduled, not that everything is under control.
  it('says nothing is scheduled, and never that everything is under control', async () => {
    render(<ClosingBand />, { wrapper });

    expect(await screen.findByText(/Nothing is scheduled yet/)).toBeInTheDocument();
    expect(screen.queryByText(/all clear/i)).not.toBeInTheDocument();
    expect(screen.queryByText(/under control/i)).not.toBeInTheDocument();
    // Empty is a calm sentence, not a failure.
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
  });
});

describe('ClosingBand — could not load', () => {
  // Failure copy comes through utils/apiErrors, the product's error-presentation boundary: it
  // renders the server's own sentence where the status allows one and substitutes governed copy
  // where it does not (401/403 permission wording, and 503, whose bodies are usually operator
  // diagnostics). The band does not go around it.
  it("shows the server's reason with a Retry, and states no figures at all", async () => {
    getDeadlineBoard.mockRejectedValue({
      isAxiosError: true,
      response: { status: 400, data: 'The deadline board cannot answer for a business unit with no calendar.' },
    });
    render(<ClosingBand />, { wrapper });

    const alert = await screen.findByRole('alert');
    expect(alert).toHaveTextContent('The deadline board cannot answer for a business unit with no calendar.');
    expect(screen.getByRole('button', { name: 'Retry' })).toBeInTheDocument();
    // A failure states nothing — a 0 in these columns would be read as "nothing is due".
    expect(screen.queryByTestId('closing-value-overdue')).not.toBeInTheDocument();
    // And it must not be mistaken for the empty state, which is the other reason a rep sees no bars.
    expect(screen.queryByText(/Nothing is scheduled yet/)).not.toBeInTheDocument();
  });

  it('retries the band on its own', async () => {
    getDeadlineBoard.mockRejectedValueOnce({ isAxiosError: true, response: { status: 500, data: {} } });
    getDeadlineBoard.mockResolvedValue(populated());
    render(<ClosingBand />, { wrapper });

    fireEvent.click(await screen.findByRole('button', { name: 'Retry' }));

    await waitFor(() => expect(screen.getByTestId('closing-value-overdue')).toHaveTextContent('4'));
  });

  // A refusal is neither empty nor broken: the server answered, and its answer is that these
  // numbers are not this reader's. No alert, and no Retry to press at a decision that will not change.
  it('renders a refusal calmly, with no Retry to offer', async () => {
    getDeadlineBoard.mockRejectedValue({
      isAxiosError: true,
      response: { status: 403, data: 'Dashboard figures are not part of your role.' },
    });
    render(<ClosingBand />, { wrapper });

    expect(await screen.findByText(/Your role does not permit this action/)).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Retry' })).not.toBeInTheDocument();
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
    expect(screen.queryByTestId('closing-value-overdue')).not.toBeInTheDocument();
  });
});
