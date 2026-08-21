import { render, screen, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { describe, expect, it, vi, beforeEach } from 'vitest';

/**
 * Setup must never report a SERVER OUTAGE as a configuration fact.
 *
 * GET /api/Mailbox 500s. `useQuery` was destructured without `isError`, so `mailboxes` fell back
 * to its `= []` default and this screen told the Tech Connect administrator, in confident product
 * copy: "No inbox is connected yet, so no leads can arrive by email. Add an IMAP mailbox to
 * start." Every clause of that is a claim about configuration, and none of it had been read.
 *
 * The cost is not a confusing screen. The admin follows the instruction and adds a second mailbox
 * for an inbox that already exists and is already polling live customer correspondence — two
 * pollers on one mailbox, and duplicate leads from every message in it.
 */

const { getAll, getOutboundStatus, getProviders } = vi.hoisted(() => ({
  getAll: vi.fn(),
  getOutboundStatus: vi.fn(),
  getProviders: vi.fn(),
}));

vi.mock('../../../api/services/mailboxService', () => ({
  default: {
    getAll, getOutboundStatus, getProviders,
    create: vi.fn(), update: vi.fn(), remove: vi.fn(), test: vi.fn(), pauseOutbound: vi.fn(),
  },
}));

vi.mock('../../../context/AuthContext', () => ({
  useAuth: () => ({ userData: { businessUnitId: 1 }, hasPermission: () => true }),
}));

vi.mock('react-hot-toast', () => ({
  toast: Object.assign(vi.fn(), { success: vi.fn(), error: vi.fn() }),
  default: Object.assign(vi.fn(), { success: vi.fn(), error: vi.fn() }),
}));

import MailboxPage from './MailboxPage';

/**
 * The shape axios actually rejects with. `src/api/axiosInstance.ts` re-rejects the AxiosError
 * untouched, so this is what `error` holds on a real 500 — a plain `new Error()` would take a
 * different branch of `toPresentableError` and prove less than it looks.
 */
const serverOutage = () => Object.assign(new Error('Request failed with status code 500'), {
  isAxiosError: true,
  code: 'ERR_BAD_RESPONSE',
  config: { method: 'get', url: '/api/Mailbox' },
  request: {},
  response: { status: 500, data: '', headers: {} },
});

/** A real, healthy IMAP row — `Mailbox` in api/services/mailboxService.ts. */
const inbox = {
  id: 4, configurationName: 'Sales enquiries', emailAddress: 'sales@techconnect.sa',
  protocol: 'IMAP' as const, host: 'imap.secureserver.net', port: 993, username: 'sales@techconnect.sa',
  useSsl: true, pollingInterval: 5, isActive: true, createdOn: '2026-07-01T09:00:00Z',
  lastSuccessfulPollOn: '2026-08-20T06:00:00Z', lastPollAttemptOn: '2026-08-20T06:00:00Z',
  lastPollError: null, consecutivePollFailures: 0, healthState: 'Healthy' as const,
  healthDetail: 'Polled 3 minutes ago.', credentialsSentInClear: false,
};

function renderPage() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={client}>
      <MailboxPage />
    </QueryClientProvider>,
  );
}

beforeEach(() => {
  vi.clearAllMocks();
  getProviders.mockResolvedValue([]);
  getOutboundStatus.mockResolvedValue({
    canSendToCustomers: false, activeSmtpCount: 0, activeSmtpHosts: [],
    summary: 'No active SMTP account, so nothing can be sent to a customer.',
    hasAmbiguousOutbound: false, activeImapCount: 1,
  });
});

describe('Setup › Mailboxes, when the read fails', () => {
  it('does not claim no inbox is connected', async () => {
    getAll.mockRejectedValue(serverOutage());
    renderPage();

    // The containment banner above is also an Alert, so anchor on this notice's own sentence.
    await screen.findByText(/could not read your mailbox settings/i);
    expect(screen.queryByText(/No inbox is connected yet/i)).not.toBeInTheDocument();
  });

  it('does not claim no sending account is configured', async () => {
    getAll.mockRejectedValue(serverOutage());
    renderPage();

    await screen.findByText(/could not read your mailbox settings/i);
    expect(screen.queryByText(/No sending account is configured/i)).not.toBeInTheDocument();
  });

  it('says the settings could not be read, and says not to add a mailbox yet', async () => {
    getAll.mockRejectedValue(serverOutage());
    renderPage();

    // The instruction is the dangerous half of the old copy, so the replacement has to carry the
    // opposite instruction — not merely withhold the false one.
    expect(await screen.findByText(/could not read your mailbox settings/i)).toBeInTheDocument();
    expect(screen.getByText(/do not add a mailbox until this list loads/i)).toBeInTheDocument();
  });

  it('offers a retry rather than leaving the admin on a dead screen', async () => {
    getAll.mockRejectedValue(serverOutage());
    renderPage();

    expect(await screen.findByRole('button', { name: /Reload mailboxes/i })).toBeInTheDocument();
  });
});

describe('Setup › Mailboxes, when the read succeeds', () => {
  it('still says the tenant has no inbox when the tenant genuinely has none', async () => {
    // CONTROL for the empty case: the copy must survive the fix, or the fix has only replaced a
    // false statement with no statement, and a real first-run admin is left with nothing to do.
    getAll.mockResolvedValue([]);
    renderPage();

    expect(await screen.findByText(/No inbox is connected yet/i)).toBeInTheDocument();
    expect(screen.getByText(/No sending account is configured/i)).toBeInTheDocument();
  });

  it('lists the mailboxes that exist, with no error surface', async () => {
    getAll.mockResolvedValue([inbox]);
    renderPage();

    expect(await screen.findByText('Sales enquiries')).toBeInTheDocument();
    await waitFor(() => expect(screen.queryByText(/No inbox is connected yet/i)).not.toBeInTheDocument());
    expect(screen.queryByText(/could not read your mailbox settings/i)).not.toBeInTheDocument();
  });
});
