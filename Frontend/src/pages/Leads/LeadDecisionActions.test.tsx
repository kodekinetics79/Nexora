import { beforeEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import LeadDecisionActions from './LeadDecisionActions';

const getState = vi.fn();
const transition = vi.fn();
const getLeadOutcomeReasons = vi.fn();
const requestClarification = vi.fn();

vi.mock('../../api/services/commercialLifecycleService', () => ({
  default: {
    getState: (...args: unknown[]) => getState(...args),
    transition: (...args: unknown[]) => transition(...args),
    getLeadOutcomeReasons: () => getLeadOutcomeReasons(),
  },
}));

vi.mock('../../api/services/leadService', () => ({
  default: {
    requestClarification: (...args: unknown[]) => requestClarification(...args),
  },
}));

vi.mock('react-hot-toast', () => ({ toast: { success: vi.fn(), error: vi.fn() } }));

const state = {
  aggregateId: 42,
  currentStatusCode: 'RECEIVED',
  version: 3,
  isTerminal: false,
  allowedTransitions: [
    { statusId: 8, statusCode: 'QUALIFIED', label: 'Qualified', requiresReason: false },
    { statusId: 9, statusCode: 'DISQUALIFIED', label: 'Passed', requiresReason: true },
  ],
};

function renderActions() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } });
  return render(
    <QueryClientProvider client={client}>
      <LeadDecisionActions leadId={42} reviewVersion={7} canEdit />
    </QueryClientProvider>,
  );
}

describe('LeadDecisionActions', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    getState.mockResolvedValue(state);
    getLeadOutcomeReasons.mockResolvedValue([{ id: 1, code: 'NO_STOCK', label: 'Item unavailable' }]);
    requestClarification.mockResolvedValue({ id: 42 });
    transition.mockResolvedValue({ newStatusCode: 'DISQUALIFIED' });
  });

  it('records clarification with the optimistic review version and required note', async () => {
    renderActions();
    const clarify = await screen.findByRole('button', { name: /request clarification/i });
    await waitFor(() => expect(clarify).toBeEnabled());
    fireEvent.click(clarify);
    const submit = screen.getByRole('button', { name: 'Record request' });
    expect(submit).toBeDisabled();
    fireEvent.change(screen.getByRole('textbox', { name: /Information needed/ }), {
      target: { value: 'Please confirm the requested quantity.' },
    });
    fireEvent.click(submit);

    await waitFor(() => expect(requestClarification).toHaveBeenCalledWith(42, {
      expectedReviewVersion: 7,
      note: 'Please confirm the requested quantity.',
    }));
  });

  it('presents Pass as a dedicated governed outcome action', async () => {
    renderActions();
    const pass = await screen.findByRole('button', { name: 'Pass' });
    await waitFor(() => expect(pass).toBeEnabled());
    fireEvent.click(pass);

    expect(await screen.findByText('Why is this inquiry ending?')).toBeInTheDocument();
    expect(screen.getByText(/Moving to Passed closes the case/)).toBeInTheDocument();
    expect(getLeadOutcomeReasons).toHaveBeenCalledOnce();
  });

  it('qualifies through the server-provided governed lifecycle transition', async () => {
    renderActions();
    const qualify = await screen.findByRole('button', { name: 'Qualify Lead' });
    await waitFor(() => expect(qualify).toBeEnabled());
    fireEvent.click(qualify);

    expect(screen.getByText(/does not create an RFQ/i)).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: 'Confirm qualification' }));

    await waitFor(() => expect(transition).toHaveBeenCalledWith(
      'leads',
      42,
      state,
      state.allowedTransitions[0],
    ));
  });
});
