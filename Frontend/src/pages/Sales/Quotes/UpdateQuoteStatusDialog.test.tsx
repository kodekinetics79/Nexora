import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { beforeEach, describe, expect, it, vi } from 'vitest';

/**
 * The one window for a quote that is with the customer (design review 2026-09-28): the client's
 * own steps, "Customer replied", and how it ended with the client's own endings and reasons.
 */

const { getStatusCatalog, getOutcomeReasons, setStep, setOutcome, markResponded } = vi.hoisted(() => ({
  getStatusCatalog: vi.fn(),
  getOutcomeReasons: vi.fn(),
  setStep: vi.fn(),
  setOutcome: vi.fn(),
  markResponded: vi.fn(),
}));

vi.mock('../../../api/services/quoteService', () => ({
  default: { getStatusCatalog, getOutcomeReasons, setStep, setOutcome, markResponded },
}));
vi.mock('react-hot-toast', () => ({ toast: { success: vi.fn(), error: vi.fn() } }));

import UpdateQuoteStatusDialog from './UpdateQuoteStatusDialog';

const catalog = {
  steps: [
    { id: 11, name: 'Technical evaluation', sortOrder: 1, isActive: true, isSystem: false },
    { id: 12, name: 'Old step', sortOrder: 2, isActive: false, isSystem: false },
  ],
  endings: [{ id: 21, name: 'Partly won', countsAs: 'WON', sortOrder: 1, isActive: true, isSystem: false }],
  reasons: [
    { id: 31, code: 'BEST_PRICE', name: 'Best price', for: 'WON', sortOrder: 1, isActive: true, isSystem: false },
    { id: 32, code: 'PRICE', name: 'Price too high', for: null, sortOrder: 2, isActive: true, isSystem: false },
    { id: 33, code: 'AUTO_EXPIRED', name: 'Expired automatically', for: 'EXPIRED', sortOrder: 3, isActive: true, isSystem: true },
  ],
};

const sentQuote = { id: 7, quoteNo: 'QT-0926-0007', statusCode: 'SENT', respondedOn: null } as never;

function renderDialog(quote = sentQuote) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } });
  const onClose = vi.fn();
  render(
    <QueryClientProvider client={client}>
      <UpdateQuoteStatusDialog open quote={quote} onClose={onClose} />
    </QueryClientProvider>,
  );
  return onClose;
}

beforeEach(() => {
  vi.clearAllMocks();
  getStatusCatalog.mockResolvedValue(catalog);
  setStep.mockResolvedValue({});
  setOutcome.mockResolvedValue({});
  markResponded.mockResolvedValue(undefined);
});

describe('Update status', () => {
  it('opens with nothing chosen and Save off', async () => {
    renderDialog();

    expect(await screen.findByLabelText('Technical evaluation')).not.toBeChecked();
    expect(screen.getByLabelText('Won')).not.toBeChecked();
    expect(screen.getByRole('button', { name: 'Save' })).toBeDisabled();
    expect(screen.queryByLabelText('Old step')).not.toBeInTheDocument();
  });

  it("sets the client's step", async () => {
    const onClose = renderDialog();

    fireEvent.click(await screen.findByLabelText('Technical evaluation'));
    fireEvent.click(screen.getByRole('button', { name: 'Save' }));

    await waitFor(() => expect(setStep).toHaveBeenCalledWith(7, 11));
    await waitFor(() => expect(onClose).toHaveBeenCalled());
  });

  it('records a reply', async () => {
    renderDialog();

    fireEvent.click(await screen.findByLabelText('Customer replied'));
    fireEvent.click(screen.getByRole('button', { name: 'Save' }));

    await waitFor(() => expect(markResponded).toHaveBeenCalledWith(7));
  });

  it("records the client's own ending as a win, with a win reason", async () => {
    renderDialog();

    fireEvent.click(await screen.findByLabelText('Partly won'));
    fireEvent.mouseDown(screen.getByRole('combobox', { name: /Reason/ }));
    expect(screen.queryByRole('option', { name: 'Price too high' })).not.toBeInTheDocument();
    fireEvent.click(await screen.findByRole('option', { name: 'Best price' }));
    fireEvent.click(screen.getByRole('button', { name: 'Save' }));

    await waitFor(() => expect(setOutcome).toHaveBeenCalledWith(7, 'won', 'BEST_PRICE', undefined, 21));
  });

  it('needs a reason for Lost, and never offers the system reason', async () => {
    renderDialog();

    fireEvent.click(await screen.findByLabelText('Lost'));
    expect(screen.getByRole('button', { name: 'Save' })).toBeDisabled();
    fireEvent.mouseDown(screen.getByRole('combobox', { name: /Reason/ }));
    expect(screen.queryByRole('option', { name: 'Expired automatically' })).not.toBeInTheDocument();
    fireEvent.click(await screen.findByRole('option', { name: 'Price too high' }));
    fireEvent.click(screen.getByRole('button', { name: 'Save' }));

    await waitFor(() => expect(setOutcome).toHaveBeenCalledWith(7, 'lost', 'PRICE', undefined, null));
  });

  it('still works with Won / Lost / Expired when the client statuses cannot be read', async () => {
    getStatusCatalog.mockRejectedValue(new Error('down'));
    getOutcomeReasons.mockResolvedValue([{ id: 1, code: 'PRICE', label: 'Price too high' }]);
    renderDialog();

    fireEvent.click(await screen.findByLabelText('Expired'));
    fireEvent.mouseDown(screen.getByRole('combobox', { name: /Reason/ }));
    expect(await screen.findByRole('option', { name: 'Price too high' })).toBeInTheDocument();
  });
});
