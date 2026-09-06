import { render, screen, within } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
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

    // The current figure is read off the mark, not off the axis.
    const endpoint = within(screen.getByTestId('six-months-value')).getByTestId('six-months-endpoint');
    expect(endpoint).toHaveTextContent(/31,200/);
  });

  it('carries the month axis and both units, so no mark needs a legend to decode', () => {
    render(<SixMonthsBand points={populated} />);

    expect(screen.getByText(/Requests received · count/)).toBeInTheDocument();
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
   * The bug this band exists to fix. TrendPanel tests `value === 0`, so a partly-null series went
   * down the "we have data" path and joined the points either side of the gap — a slope nobody
   * measured. Here the line breaks and the skipped month is named in words.
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
