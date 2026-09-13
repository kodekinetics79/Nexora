import { beforeEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import LeadDecisionActions, {
  clarifyCallout, passCallout, qualifyCallout, reopenBlockedReason,
} from './LeadDecisionActions';

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

/**
 * On a request that had become an RFQ the three call-outs said "Pass is not available for this
 * completed lead." and "Qualification is not an allowed transition from the current lifecycle
 * state." Neither was true: it was not completed, and "transition" is not a word a rep uses.
 */
describe('LeadDecisionActions — call-outs say where the request stands', () => {
  const becameRfq = {
    aggregateId: 42,
    currentStatusCode: 'CONVERTED_TO_RFQ',
    version: 6,
    isTerminal: false,
    canReopen: false,
    allowedTransitions: [
      { statusId: 12, statusCode: 'QUOTED', label: 'Quoted', requiresReason: false },
      { statusId: 13, statusCode: 'CANCELLED', label: 'Cancelled', requiresReason: true },
    ],
  };

  beforeEach(() => {
    vi.clearAllMocks();
    authUser = { id: 7, isManager: true, isSuperAdmin: false };
    getLeadOutcomeReasons.mockResolvedValue([]);
  });

  it('on a request that became an RFQ, says that on each disabled verb', async () => {
    getState.mockResolvedValue(becameRfq);
    renderActions();

    const qualify = await screen.findByRole('button', { name: 'Qualify Lead' });
    await waitFor(() => expect(qualify.parentElement).toHaveAttribute('title', 'This request already became an RFQ.'));
    const pass = screen.getByRole('button', { name: 'Pass' });
    expect(pass).toBeDisabled();
    expect(pass.parentElement).toHaveAttribute('title', "This request already became an RFQ, so it can't be passed on here.");
    expect(screen.getByRole('button', { name: /request clarification/i }).parentElement)
      .toHaveAttribute('title', 'This request already became an RFQ, so there is nothing to clarify here.');

    fireEvent.mouseOver(pass.parentElement!);
    expect(await screen.findByRole('tooltip')).toHaveTextContent("This request already became an RFQ, so it can't be passed on here.");
    expect(document.body.innerHTML).not.toMatch(/completed lead|allowed transition|lifecycle state/i);
  });

  it('says what Qualify and Pass do while they are available', async () => {
    getState.mockResolvedValue(state);
    renderActions();

    const qualify = await screen.findByRole('button', { name: 'Qualify Lead' });
    await waitFor(() => expect(qualify).toBeEnabled());
    expect(qualify.parentElement).toHaveAttribute(
      'title',
      'Marks this request as worth quoting. It does not create an RFQ; that happens on the Decide screen.',
    );
    expect(screen.getByRole('button', { name: 'Pass' }).parentElement)
      .toHaveAttribute('title', 'Closes this request without an RFQ. You choose the reason.');
  });

  it('says in the Qualify dialog that lines are marked, not quoted', async () => {
    getState.mockResolvedValue(state);
    renderActions();

    const qualify = await screen.findByRole('button', { name: 'Qualify Lead' });
    await waitFor(() => expect(qualify).toBeEnabled());
    fireEvent.click(qualify);
    expect(screen.getByText(
      'This marks the request qualified. It does not create an RFQ: lines are marked Quote or Skip, and the RFQ is created, on the Decide screen.',
    )).toBeInTheDocument();
    expect(screen.queryByText(/quoted or skipped|governed lifecycle transition/i)).toBeNull();
  });
});

describe('the three call-outs, status by status', () => {
  const at = (currentStatusCode: string, isTerminal = false) => ({ currentStatusCode, isTerminal });

  it('say the status is still being read before it arrives', () => {
    expect(qualifyCallout(undefined, false)).toBe('Checking where this request stands…');
    expect(passCallout(undefined, false)).toBe('Checking where this request stands…');
    expect(clarifyCallout(undefined, false)).toBe('Record the missing information needed from the customer.');
  });

  it('Qualify', () => {
    expect(qualifyCallout(at('QUALIFIED'), false)).toBe('This request is already qualified.');
    expect(qualifyCallout(at('CONVERTED_TO_RFQ'), false)).toBe('This request already became an RFQ.');
    expect(qualifyCallout(at('QUOTED'), false)).toBe('This request has moved past qualifying (Quote sent).');
    expect(qualifyCallout(at('COMPLETED', true), false)).toBe('This request has moved past qualifying (Completed).');
    expect(qualifyCallout(at('DUPLICATED', true), false)).toBe("This request is closed (Duplicate), so it can't be qualified.");
    expect(qualifyCallout(at('UNASSIGNED'), false))
      .toBe("This request can't be qualified from Waiting for an owner. Ask an administrator to check the lead statuses under Setup.");
  });

  it('Pass', () => {
    expect(passCallout(at('DISQUALIFIED', true), false)).toBe('This request was already declined.');
    expect(passCallout(at('LOST', true), false)).toBe('This request is already closed (Lost).');
    expect(passCallout(at('NEGOTIATION'), false)).toBe('This request is past the point of passing on (In negotiation).');
    expect(passCallout(at('SOMETHING_TENANT_MADE'), false))
      .toBe("Passing on isn't available from its current status. Ask an administrator to check the lead statuses under Setup.");
  });

  it('Request clarification', () => {
    expect(clarifyCallout(at('RECEIVED'), false)).toBe('Record the missing information needed from the customer.');
    expect(clarifyCallout(at('CANCELLED', true), true)).toBe('This request is closed (Cancelled), so there is nothing to clarify here.');
  });

  it('never print a raw status code', () => {
    const codes = ['RECEIVED', 'UNDER_REVIEW', 'QUALIFIED', 'DISQUALIFIED', 'CONVERTED_TO_RFQ', 'QUOTED', 'NEGOTIATION',
      'AWARDED', 'PARTIALLY_AWARDED', 'LOST', 'CANCELLED', 'COMPLETED', 'DUPLICATED', 'PENDING_IDENTIFICATION', 'MYSTERY_CODE'];
    for (const code of codes) {
      for (const terminal of [false, true]) {
        const said = [qualifyCallout(at(code, terminal), false), passCallout(at(code, terminal), false), clarifyCallout(at(code, terminal), true)];
        for (const sentence of said) expect(sentence).not.toMatch(/[A-Z]{2,}_[A-Z]|MYSTERY/);
      }
    }
  });
});
