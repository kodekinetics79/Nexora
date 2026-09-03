import { beforeEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import LeadDecisionActions, { reopenBlockedReason } from './LeadDecisionActions';

const getState = vi.fn();
const transition = vi.fn();
const reopen = vi.fn();
const getLeadOutcomeReasons = vi.fn();
const requestClarification = vi.fn();

vi.mock('../../api/services/commercialLifecycleService', () => ({
  default: {
    getState: (...args: unknown[]) => getState(...args),
    transition: (...args: unknown[]) => transition(...args),
    reopen: (...args: unknown[]) => reopen(...args),
    getLeadOutcomeReasons: () => getLeadOutcomeReasons(),
  },
}));

/** Reassigned per test: reopen is manager-only on top of Leads:Edit. */
let authUser: { id: number; isManager: boolean; isSuperAdmin: boolean } =
  { id: 7, isManager: true, isSuperAdmin: false };
vi.mock('../../context/AuthContext', () => ({
  useAuth: () => ({ userData: authUser, hasPermission: () => true }),
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
    authUser = { id: 7, isManager: true, isSuperAdmin: false };
    reopen.mockResolvedValue({ newStatusCode: 'UNDER_REVIEW' });
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

/**
 * "We passed on it, the customer came back."
 *
 * `POST /api/commercial-cases/leads/{id}/reopen` shipped with the lifecycle spine and had zero
 * frontend callers, so a passed lead was a dead end: three greyed-out buttons and no way forward.
 */
describe('LeadDecisionActions — reopening a closed inquiry', () => {
  const passed = {
    aggregateId: 42,
    currentStatusCode: 'DISQUALIFIED',
    version: 5,
    isTerminal: true,
    canReopen: true,
    allowedTransitions: [],
  };

  beforeEach(() => {
    vi.clearAllMocks();
    authUser = { id: 7, isManager: true, isSuperAdmin: false };
    reopen.mockResolvedValue({ newStatusCode: 'UNDER_REVIEW' });
    getLeadOutcomeReasons.mockResolvedValue([]);
  });

  it('offers a manager the reopen verb on a lead the server says is reopenable', async () => {
    getState.mockResolvedValue(passed);
    renderActions();

    const button = await screen.findByRole('button', { name: /reopen this inquiry/i });
    await waitFor(() => expect(button).toBeEnabled());
    fireEvent.click(button);
    fireEvent.change(screen.getByLabelText(/why is it coming back/i), {
      target: { value: 'Customer re-issued the tender with new quantities.' },
    });
    fireEvent.click(screen.getByRole('button', { name: 'Reopen' }));

    await waitFor(() => expect(reopen).toHaveBeenCalledWith(
      'leads', 42, passed, 'Customer re-issued the tender with new quantities.',
    ));
  });

  it('will not send a reopen with no reason, and says what is missing', async () => {
    getState.mockResolvedValue(passed);
    renderActions();

    fireEvent.click(await screen.findByRole('button', { name: /reopen this inquiry/i }));
    expect(screen.getByRole('button', { name: 'Reopen' })).toBeDisabled();
    expect(screen.getByText(/type at least 5 characters to enable reopen/i)).toBeInTheDocument();
    expect(reopen).not.toHaveBeenCalled();
  });

  it('tells a rep who cannot reopen why, next to the control', async () => {
    authUser = { id: 7, isManager: false, isSuperAdmin: false };
    getState.mockResolvedValue(passed);
    renderActions();

    const button = await screen.findByRole('button', { name: /reopen this inquiry/i });
    await waitFor(() => expect(button).toBeDisabled());
    // The reason is READ, not hovered for.
    expect(screen.getByText(/only a manager can reopen an inquiry that was closed/i)).toBeInTheDocument();
  });

  /**
   * COMPLETED and DUPLICATED are terminal and NOT reopenable. A control driven off `isTerminal`
   * would offer the verb here and be refused after the click.
   */
  it('does not offer reopen on a finished lead, and says so in words', async () => {
    getState.mockResolvedValue({ ...passed, currentStatusCode: 'COMPLETED', canReopen: false });
    renderActions();

    const button = await screen.findByRole('button', { name: /reopen this inquiry/i });
    await waitFor(() => expect(button).toBeDisabled());
    expect(screen.getByText(/finished as completed and is not reopened/i)).toBeInTheDocument();
    // No raw lifecycle code reaches the screen.
    expect(screen.queryByText(/COMPLETED/)).not.toBeInTheDocument();
  });

  it('shows no reopen control at all while the inquiry is still live', async () => {
    getState.mockResolvedValue(state);
    renderActions();

    await screen.findByRole('button', { name: 'Qualify Lead' });
    expect(screen.queryByRole('button', { name: /reopen this inquiry/i })).not.toBeInTheDocument();
  });
});

describe('reopenBlockedReason', () => {
  it('lets a manager through on a reopenable state', () => {
    expect(reopenBlockedReason(true, true, 'DISQUALIFIED')).toBeNull();
  });

  it('never names a raw lifecycle code in what the rep reads', () => {
    const finished = reopenBlockedReason(false, true, 'DUPLICATED');
    expect(finished).toContain('duplicated');
    expect(finished).not.toContain('DUPLICATED');
  });
});
