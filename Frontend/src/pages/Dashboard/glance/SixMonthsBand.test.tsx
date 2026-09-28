import { act, fireEvent, render, screen, within } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import SixMonthsBand from './SixMonthsBand';
import type { SixMonthPoint } from './SixMonthsBand';

const populated: SixMonthPoint[] = [
  { month: 'Apr', count: 4, value: 12000, valueCurrency: 'SAR', valueUnavailableReason: null },
  { month: 'May', count: 7, value: 18400, valueCurrency: 'SAR', valueUnavailableReason: null },
  { month: 'Jun', count: 5, value: 9100, valueCurrency: 'SAR', valueUnavailableReason: null },
  { month: 'Jul', count: 9, value: 24050, valueCurrency: 'SAR', valueUnavailableReason: null },
  { month: 'Aug', count: 6, value: 15500, valueCurrency: 'SAR', valueUnavailableReason: null },
  { month: 'Sep', count: 11, value: 31200, valueCurrency: 'SAR', valueUnavailableReason: null },
];

/** The default shape of a brand-new tenant: the months exist, every figure in them is nothing. */
const nothingYet: SixMonthPoint[] = ['Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep'].map((month) => ({
  month, count: 0, value: null, valueCurrency: null,
  valueUnavailableReason: 'This business unit has no single base currency, so order value cannot be totalled.',
}));

describe('SixMonthsBand populated', () => {
  it('draws a column per month and a brass line with an emphasised, directly labelled endpoint', () => {
    render(<SixMonthsBand points={populated} />);

    const requests = screen.getByTestId('six-months-requests');
    expect(within(requests).queryAllByTestId('six-months-empty-column')).toHaveLength(0);
    expect(requests).toHaveAttribute('aria-label', expect.stringContaining('Sep 11'));
    // Named for what the server counts — RFQs by month — not the lead count the bands above
    // call "requests received"; "Sep: 1 requests received" beside six leads was a contradiction.
    expect(requests).toHaveAttribute('aria-label', expect.stringMatching(/^RFQs created, /));
    expect(screen.queryByText(/Requests received/)).not.toBeInTheDocument();

    // The current figure is read off the mark, not off the axis.
    const endpoint = within(screen.getByTestId('six-months-value')).getByTestId('six-months-endpoint');
    expect(endpoint).toHaveTextContent(/31,200/);
  });

  it('carries the month axis and both units, so no mark needs a legend to decode', () => {
    render(<SixMonthsBand points={populated} />);

    expect(screen.getByText(/RFQs created · count/)).toBeInTheDocument();
    expect(screen.getByText(/Order value · SAR/)).toBeInTheDocument();
    const months = within(screen.getByTestId('six-months-value')).getAllByText('Sep');
    expect(months.length).toBeGreaterThan(0);
  });

  // The whole point of the band: it is company-wide for every reader, and its window is the
  // server's, not the one the reader picked above.
  it('seals itself Company-wide and outlined even though other bands may be personal', () => {
    render(<SixMonthsBand points={populated} />);

    const seal = screen.getByTestId('band-seal');
    expect(seal).toHaveTextContent(/^Company-wide · Last 6 months ·/);
    expect(seal).toHaveAttribute('data-governed', 'false');
    expect(screen.getByText('Background context')).toBeInTheDocument();
  });

  it('says freshness is not stated rather than inventing one for an endpoint that sends none', () => {
    render(<SixMonthsBand points={populated} />);

    expect(screen.getByTestId('band-seal')).toHaveTextContent('freshness not stated');
  });

  /**
   * The bug this band exists to fix. The executive panel it replaces tested `value === 0`, so a
   * partly-null series went down the "we have data" path and joined the points either side of the
   * gap — a slope nobody measured. Here the line breaks and the skipped month is named in words.
   */
  it('breaks the line across a month the server would not state, and names it', () => {
    const gapped = populated.map((p, i) => (
      i === 2 ? { ...p, value: null, valueUnavailableReason: 'June contains an order in a currency with no approved rate.' } : p
    ));
    render(<SixMonthsBand points={gapped} />);

    const value = screen.getByTestId('six-months-value');
    expect(within(value).getAllByTestId('six-months-value-gap')).toHaveLength(1);
    expect(screen.getByText(/The line skips Jun/)).toBeInTheDocument();
    expect(screen.getByText(/no approved rate/)).toBeInTheDocument();
  });

  /**
   * Requests genuinely arrived, so this is not "nothing happened" — it is a figure the server will
   * not state, and it must arrive as the server's own sentence over an intact frame.
   */
  it('shows the server reason, not a zero line, when no month has a stated value', () => {
    const noMoney = populated.map((p) => ({
      ...p, value: null, valueCurrency: null,
      valueUnavailableReason: 'This business unit has no single base currency, so order value cannot be totalled.',
    }));
    render(<SixMonthsBand points={noMoney} />);

    expect(screen.getByText('Not available')).toBeInTheDocument();
    expect(screen.getByText(/no single base currency/)).toBeInTheDocument();
    // The frame survives underneath — that is what stops the band reflowing when FX is configured.
    expect(screen.getByTestId('six-months-value')).toBeInTheDocument();
    expect(screen.queryByTestId('six-months-endpoint')).not.toBeInTheDocument();
  });
});

describe('SixMonthsBand empty', () => {
  it('keeps six labelled month slots and both axes when nothing has happened', () => {
    render(<SixMonthsBand points={nothingYet} />);

    expect(screen.getByText('No requests or orders were recorded in the last six months.')).toBeInTheDocument();
    expect(screen.getAllByTestId('six-months-empty-column')).toHaveLength(6);
    expect(screen.getAllByTestId('six-months-empty-point')).toHaveLength(6);
    const labels = within(screen.getByTestId('six-months-value')).getAllByText(/^(Apr|May|Jun|Jul|Aug|Sep)$/);
    expect(labels).toHaveLength(6);
  });

  it('treats a null value as empty, so a currency-less tenant never gets a flat line of nulls', () => {
    render(<SixMonthsBand points={nothingYet} />);

    // Empty, not unavailable: the calm sentence, and no Alert and no defocused frame.
    expect(screen.queryByText('Not available')).not.toBeInTheDocument();
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
    expect(screen.queryByTestId('six-months-endpoint')).not.toBeInTheDocument();
  });

  it('still draws the axis and calendar when the server sends no rows at all', () => {
    render(<SixMonthsBand points={[]} />);

    expect(screen.getAllByTestId('six-months-empty-column')).toHaveLength(6);
    expect(screen.getByText('No requests or orders were recorded in the last six months.')).toBeInTheDocument();
    expect(screen.getByTestId('band-seal')).toHaveTextContent('Company-wide');
  });
});

describe('SixMonthsBand error', () => {
  it('shows the server reason with a Retry, and looks nothing like the empty state', () => {
    const onRetry = vi.fn();
    render(<SixMonthsBand error="The six-month series could not be read." onRetry={onRetry} />);

    const alert = screen.getByRole('alert');
    expect(alert).toHaveTextContent('We could not load this');
    expect(alert).toHaveTextContent('The six-month series could not be read.');
    expect(within(alert).getByRole('button', { name: 'Retry' })).toBeInTheDocument();
    expect(screen.queryByText('No requests or orders were recorded in the last six months.')).not.toBeInTheDocument();
    expect(screen.queryByTestId('six-months-requests')).not.toBeInTheDocument();
  });

  it('keeps its seal while it is loading, so the reader knows whose numbers are coming', () => {
    render(<SixMonthsBand loading />);

    expect(screen.getByRole('status')).toHaveTextContent(/loading the last six months/i);
    expect(screen.getByTestId('band-seal')).toHaveTextContent('Company-wide');
  });
});

describe('SixMonthsBand picking a period', () => {
  // The last row is the current month, so "Sep" only means Sep 2026 once the clock says so.
  beforeEach(() => {
    vi.useFakeTimers({ toFake: ['Date'] });
    vi.setSystemTime(new Date('2026-09-17T10:00:00Z'));
  });
  afterEach(() => { vi.useRealTimers(); });

  const month = (name: string) => screen.getAllByTestId('six-months-month')
    .find((b) => b.getAttribute('aria-label')?.startsWith(`${name} ·`)) as HTMLElement;

  it('sends one calendar month as an inclusive YYYY-MM-DD window when a month is clicked', () => {
    const onPickPeriod = vi.fn();
    render(<SixMonthsBand points={populated} onPickPeriod={onPickPeriod} />);

    fireEvent.click(month('Aug'));
    expect(onPickPeriod).toHaveBeenCalledExactlyOnceWith('2026-08-01', '2026-08-31');
    expect(screen.getByText(/Click a month to see the whole dashboard for it · drag across months for a range/)).toBeInTheDocument();
  });

  it('picks a month on a plain pointer press and release, even when the click lands on the overlay', () => {
    // A real browser sends the click to the overlay that captured the pointer, never to the month
    // button, so the pick must happen on release. Clicking the overlay afterwards must not re-pick.
    const onPickPeriod = vi.fn();
    render(<SixMonthsBand points={populated} onPickPeriod={onPickPeriod} />);
    const overlay = screen.getByTestId('six-months-overlay');

    fireEvent.pointerDown(month('Jul'), { button: 0, pointerId: 1 });
    fireEvent.pointerUp(month('Jul'), { button: 0, pointerId: 1 });
    fireEvent.click(overlay);
    expect(onPickPeriod).toHaveBeenCalledExactlyOnceWith('2026-07-01', '2026-07-31');
  });

  it('reads the month off an ISO label rather than counting back', () => {
    const onPickPeriod = vi.fn();
    const iso = populated.map((p, i) => ({ ...p, month: `2025-${String(i + 3).padStart(2, '0')}` }));
    render(<SixMonthsBand points={iso} onPickPeriod={onPickPeriod} />);

    fireEvent.click(month('Mar'));
    expect(onPickPeriod).toHaveBeenCalledWith('2025-03-01', '2025-03-31');
  });

  it('sends the whole run when the reader drags across three months, and draws the brush meanwhile', () => {
    const onPickPeriod = vi.fn();
    render(<SixMonthsBand points={populated} onPickPeriod={onPickPeriod} />);

    fireEvent.pointerDown(month('Jun'), { button: 0, pointerId: 1 });
    fireEvent.pointerMove(month('Aug'), { pointerId: 1 });
    expect(screen.getByTestId('six-months-brush')).toBeInTheDocument();
    fireEvent.pointerUp(month('Aug'), { pointerId: 1 });

    expect(onPickPeriod).toHaveBeenCalledExactlyOnceWith('2026-06-01', '2026-08-31');
    expect(screen.queryByTestId('six-months-brush')).not.toBeInTheDocument();
    // The click a browser may send after the drag does not re-pick a single month.
    fireEvent.click(month('Aug'));
    expect(onPickPeriod).toHaveBeenCalledTimes(1);
  });

  it('marks the months the picked window touches, and only those', () => {
    render(<SixMonthsBand points={populated} onPickPeriod={vi.fn()} picked={{ from: '2026-07-20', to: '2026-08-18' }} />);

    const pressed = screen.getAllByTestId('six-months-month')
      .map((b) => [b.getAttribute('aria-label')?.slice(0, 3), b.getAttribute('aria-pressed')]);
    expect(pressed).toEqual([
      ['Apr', 'false'], ['May', 'false'], ['Jun', 'false'], ['Jul', 'true'], ['Aug', 'true'], ['Sep', 'false'],
    ]);
  });

  it('shows a readout for the hovered month and follows the arrow keys', () => {
    render(<SixMonthsBand points={populated} />);

    fireEvent.pointerMove(month('Jul'));
    expect(screen.getByTestId('six-months-readout')).toHaveTextContent(/^Jul · 9 RFQs · .*24,050/);
    expect(screen.getByTestId('six-months-crosshair')).toBeInTheDocument();
    expect(screen.getByTestId('six-months-active-point')).toBeInTheDocument();
    fireEvent.pointerLeave(screen.getByTestId('six-months-overlay'));
    expect(screen.queryByTestId('six-months-readout')).not.toBeInTheDocument();

    // One tab stop, the current month; arrows walk from there.
    const sep = month('Sep');
    expect(sep).toHaveAttribute('tabindex', '0');
    act(() => sep.focus());
    expect(screen.getByTestId('six-months-readout')).toHaveTextContent(/^Sep · 11 RFQs/);
    fireEvent.keyDown(sep, { key: 'ArrowLeft' });
    expect(month('Aug')).toHaveFocus();
    expect(screen.getByTestId('six-months-readout')).toHaveTextContent(/^Aug · 6 RFQs/);
  });

  it('states the reason in the readout when a month has no value, never a zero', () => {
    const gapped = populated.map((p, i) => (
      i === 2 ? { ...p, value: null, valueUnavailableReason: 'June contains an order in a currency with no approved rate.' } : p
    ));
    render(<SixMonthsBand points={gapped} />);

    fireEvent.pointerMove(month('Jun'));
    expect(screen.getByTestId('six-months-readout')).toHaveTextContent('Jun · 5 RFQs · June contains an order in a currency with no approved rate.');
  });

  it('shows no hint and picks nothing when the page does not ask for picks', () => {
    render(<SixMonthsBand points={populated} />);

    fireEvent.click(month('Aug'));
    expect(screen.queryByText(/Click a month/)).not.toBeInTheDocument();
    expect(month('Aug')).not.toHaveAttribute('aria-pressed');
  });
});
