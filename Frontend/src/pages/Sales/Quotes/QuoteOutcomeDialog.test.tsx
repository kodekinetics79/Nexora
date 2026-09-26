import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { describe, expect, it, vi, beforeEach } from 'vitest';

/**
 * "Why?" is a question about losing, not about winning.
 *
 * With "We won it" selected the dialog still showed "Why? (optional)" listing Price too high,
 * Lost to competitor, Customer cancelled, No response… (D30) — the loss reasons, offered against a
 * win. The picker belongs to Lost and Expired, where a reason is required and the list fits.
 */

const { getOutcomeReasons, setOutcome } = vi.hoisted(() => ({
  getOutcomeReasons: vi.fn(),
  setOutcome: vi.fn(),
}));

vi.mock('../../../api/services/quoteService', () => ({
  default: { getOutcomeReasons, setOutcome },
}));

vi.mock('react-hot-toast', () => ({
  toast: Object.assign(vi.fn(), { success: vi.fn(), error: vi.fn() }),
  default: Object.assign(vi.fn(), { success: vi.fn(), error: vi.fn() }),
}));

import QuoteOutcomeDialog from './QuoteOutcomeDialog';

const onClose = vi.fn();

function renderDialog() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } });
  return render(
    <QueryClientProvider client={client}>
      <QuoteOutcomeDialog open onClose={onClose} quoteId={66} quoteNo="QT-0926-0001" />
    </QueryClientProvider>,
  );
}

beforeEach(() => {
  vi.clearAllMocks();
  getOutcomeReasons.mockResolvedValue([
    { id: 1, code: 'PRICE', label: 'Price too high' },
    { id: 2, code: 'LOST_COMPETITOR', label: 'Lost to competitor' },
    { id: 3, code: 'CANCELLED', label: 'Customer cancelled' },
    { id: 4, code: 'AUTO_EXPIRED', label: 'Expired automatically' },
  ]);
  setOutcome.mockResolvedValue({});
});

describe('QuoteOutcomeDialog — the reason picker', () => {
  it('is not offered when the quote was won', async () => {
    renderDialog();
    await waitFor(() => expect(getOutcomeReasons).toHaveBeenCalled());

    expect(screen.getByRole('radio', { name: /we won it/i })).toBeChecked();
    expect(screen.queryByLabelText(/Why\?/i)).not.toBeInTheDocument();
    expect(screen.queryByText(/Price too high/i)).not.toBeInTheDocument();
    // Saving a win needs no reason.
    expect(screen.getByRole('button', { name: /^save$/i })).toBeEnabled();
  });

  it('appears, required, when the quote was lost — and Save waits for it', async () => {
    renderDialog();
    fireEvent.click(screen.getByRole('radio', { name: /we lost it/i }));

    expect(await screen.findByLabelText(/Why\? \(required\)/i)).toBeInTheDocument();
    expect(screen.getByRole('button', { name: /^save$/i })).toBeDisabled();
  });

  it('appears for an expired quote too, without the system-only reason', async () => {
    renderDialog();
    fireEvent.click(screen.getByRole('radio', { name: /expired without a decision/i }));

    await screen.findByLabelText(/Why\? \(required\)/i);
    await waitFor(() => expect(getOutcomeReasons).toHaveBeenCalled());
    fireEvent.mouseDown(screen.getByRole('combobox', { name: /Why\? \(required\)/i }));
    expect(await screen.findByRole('option', { name: /Customer cancelled/i })).toBeInTheDocument();
    expect(screen.queryByRole('option', { name: /Expired automatically/i })).not.toBeInTheDocument();
  });

  it('saves a win with no reason attached', async () => {
    renderDialog();
    await waitFor(() => expect(getOutcomeReasons).toHaveBeenCalled());
    fireEvent.click(screen.getByRole('button', { name: /^save$/i }));

    await waitFor(() => expect(setOutcome).toHaveBeenCalledWith(66, 'won', undefined, undefined));
  });
});
