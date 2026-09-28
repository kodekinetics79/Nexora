import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { useState } from 'react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import TeamChart, { type Rep } from './TeamChart';

const mocks = vi.hoisted(() => ({ getColumns: vi.fn(), saveColumns: vi.fn() }));
vi.mock('../../api/services/listViewService', () => ({
  default: { getColumns: mocks.getColumns, saveColumns: mocks.saveColumns },
}));

const rep = (userId: number, name: string, over: Partial<Rep> = {}): Rep => ({
  userId, name, activeLeads: 2, overdueLeads: 0, openRfqs: 1, draftQuotes: 0, followUpsDue: 0, pipelineGroups: [],
  wonQuotes: 2, lostQuotes: 1, decidedQuotes: 3, conversionEligible: false, conversionRate: null,
  activityCount: 9, opportunities: 8, quoteSent: 6, customerResponses: 3, averageResponseHours: 5,
  followUpsCreated: 0, completedFollowUps: 0, followUpsCompletedOnTime: 0, openFollowUps: 0, overdueFollowUps: 0,
  revenueByCurrency: [], ...over,
});

const reps = [
  rep(1, 'Aisha', { quoteSent: 10, wonQuotes: 4, conversionEligible: true, conversionRate: 60 }),
  rep(2, 'Bilal', { quoteSent: 2, wonQuotes: 1 }),
];

function Harness() {
  const [picked, setPicked] = useState<Set<number>>(new Set());
  return (
    <>
      <TeamChart reps={reps} selected={picked} onSelect={setPicked} />
      <output data-testid="picked">{[...picked].sort().join(',')}</output>
    </>
  );
}

const renderChart = () => render(
  <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
    <Harness />
  </QueryClientProvider>,
);

describe('TeamChart', () => {
  beforeEach(() => {
    mocks.getColumns.mockReset().mockResolvedValue({ viewKey: 'sales.performance.chart', columns: [], isCustomised: false, supportsCustomFields: false });
    mocks.saveColumns.mockReset().mockImplementation(async (_k, columns) => ({
      viewKey: 'sales.performance.chart', isCustomised: true, supportsCustomFields: false,
      columns: columns.map((c: { key: string; visible: boolean }) => ({ ...c, label: c.key, locked: false, source: 'catalog' })),
    }));
  });

  it('draws one dot per rep on the default measures', () => {
    renderChart();
    expect(screen.getByRole('button', { name: /Aisha: Quotes sent 10, Won 4/ })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: /Bilal: Quotes sent 2, Won 1/ })).toBeInTheDocument();
  });

  it('opens the saved pair from the reader profile', async () => {
    mocks.getColumns.mockResolvedValue({
      viewKey: 'sales.performance.chart', isCustomised: true, supportsCustomFields: false,
      columns: [
        { key: 'opportunities', label: 'Opportunities', visible: true, locked: false, source: 'catalog' },
        { key: 'lostQuotes', label: 'Lost', visible: true, locked: false, source: 'catalog' },
      ],
    });
    renderChart();
    expect(await screen.findByRole('button', { name: /Aisha: Opportunities 8, Lost 1/ })).toBeInTheDocument();
  });

  it('swaps a measure from the axis name and saves it to the profile', async () => {
    renderChart();
    fireEvent.click(screen.getByRole('button', { name: /Up axis: Won/ }));
    fireEvent.click(screen.getByRole('menuitem', { name: 'Lost' }));
    expect(screen.getByRole('button', { name: /Aisha: Quotes sent 10, Lost 1/ })).toBeInTheDocument();
    await waitFor(() => expect(mocks.saveColumns).toHaveBeenCalled());
    const [, saved] = mocks.saveColumns.mock.calls[0];
    expect(saved.filter((c: { visible: boolean }) => c.visible).map((c: { key: string }) => c.key)).toEqual(['quoteSent', 'lostQuotes']);
  });

  it('names reps with no figure instead of drawing them at zero', () => {
    renderChart();
    fireEvent.click(screen.getByRole('button', { name: /Up axis: Won/ }));
    fireEvent.click(screen.getByRole('menuitem', { name: 'Win rate' }));
    expect(screen.queryByRole('button', { name: /^Bilal:/ })).not.toBeInTheDocument();
    expect(screen.getByText(/Not on the chart \(not enough decided quotes\): Bilal/)).toBeInTheDocument();
  });

  it('picks the reps inside a dragged box and lets go on a plain click', () => {
    renderChart();
    const plot = screen.getByRole('group', { name: /Reps by/ });
    fireEvent.pointerDown(plot, { clientX: 0, clientY: 0, pointerId: 1 });
    fireEvent.pointerMove(plot, { clientX: 700, clientY: 400, pointerId: 1 });
    fireEvent.pointerUp(plot, { clientX: 700, clientY: 400, pointerId: 1 });
    expect(screen.getByTestId('picked')).toHaveTextContent('1,2');
    expect(screen.getByRole('button', { name: /2 picked/ })).toBeInTheDocument();

    fireEvent.pointerDown(plot, { clientX: 5, clientY: 5, pointerId: 1 });
    fireEvent.pointerUp(plot, { clientX: 5, clientY: 5, pointerId: 1 });
    expect(screen.getByTestId('picked')).toHaveTextContent('');
  });

  it('opens a rep breakdown from their dot', () => {
    renderChart();
    expect(screen.getByText('Click a dot to see that rep')).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: /^Aisha:/ }));
    const panel = screen.getByRole('button', { name: 'Close breakdown' }).closest('div')!.parentElement!;
    expect(within(panel).getByText('Customer replied')).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: 'Close breakdown' }));
    expect(screen.getByText('Click a dot to see that rep')).toBeInTheDocument();
  });
});
