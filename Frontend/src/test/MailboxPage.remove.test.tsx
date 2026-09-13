import { describe, expect, it, vi, beforeEach } from 'vitest';
import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import MailboxPage from '../pages/Setup/Mailbox/MailboxPage';

/**
 * Removing a mailbox used to be one click on a trash icon. A mailbox is the thing that reads
 * inquiries in and sends quotes out, and it carries stored credentials, so the wrong click stopped
 * the business silently. These tests hold the line: nothing is sent to the server until the
 * administrator has read which address is going and typed it back.
 */

const getAll = vi.fn();
const remove = vi.fn();

vi.mock('../context/AuthContext', () => ({
  useAuth: () => ({
    hasPermission: () => true,
    userData: { email: 'admin@example.test', isSuperAdmin: true },
  }),
}));

vi.mock('react-hot-toast', () => ({ toast: Object.assign(vi.fn(), { success: vi.fn(), error: vi.fn() }) }));

vi.mock('../email/ProviderPicker', () => ({ default: () => null }));

vi.mock('../api/services/mailboxService', () => ({
  default: {
    getAll: () => getAll(),
    getOutboundStatus: () => Promise.resolve(null),
    getProviders: () => Promise.resolve([]),
    remove: (id: number) => remove(id),
    test: vi.fn(),
    sendTest: vi.fn(),
    create: vi.fn(),
    update: vi.fn(),
    pauseOutbound: vi.fn(),
  },
}));

const INBOX = {
  id: 9, configurationName: 'Sales inbox', emailAddress: 'info@example.test', protocol: 'IMAP',
  host: 'imap.example.test', port: 993, username: 'info@example.test', useSsl: true, pollingInterval: 5,
  isActive: true, createdOn: '2026-09-01T00:00:00Z', lastSuccessfulPollOn: null, lastPollAttemptOn: null,
  lastPollError: null, consecutivePollFailures: 0, healthState: 'Ready', healthDetail: 'Ready to poll.',
  credentialsSentInClear: false,
};

const renderPage = () => {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } });
  return render(<QueryClientProvider client={client}><MailboxPage /></QueryClientProvider>);
};

beforeEach(() => {
  vi.clearAllMocks();
  getAll.mockResolvedValue([INBOX]);
  remove.mockResolvedValue({ message: 'Mailbox removed' });
});

describe('MailboxPage — removing a mailbox', () => {
  it('asks for the address back before anything is sent, and names what stops', async () => {
    renderPage();
    fireEvent.click(await screen.findByRole('button', { name: /remove info@example\.test/i }));

    const dialog = await screen.findByRole('dialog', { name: /remove info@example\.test\?/i });
    expect(within(dialog).getByText(/stop reading new inquiries from this inbox/i)).toBeInTheDocument();
    expect(within(dialog).getByText(/stored sign-in details for this mailbox are removed/i)).toBeInTheDocument();
    const confirm = within(dialog).getByRole('button', { name: /remove mailbox/i });
    expect(confirm).toBeDisabled();
    expect(remove).not.toHaveBeenCalled();

    fireEvent.change(within(dialog).getByLabelText(/type the email address to confirm/i), {
      target: { value: 'info@example.text' },
    });
    expect(confirm).toBeDisabled();

    fireEvent.change(within(dialog).getByLabelText(/type the email address to confirm/i), {
      target: { value: 'info@example.test' },
    });
    expect(confirm).toBeEnabled();
    fireEvent.click(confirm);
    await waitFor(() => expect(remove).toHaveBeenCalledWith(9));
  });

  it('keeps the mailbox when the administrator backs out', async () => {
    renderPage();
    fireEvent.click(await screen.findByRole('button', { name: /remove info@example\.test/i }));
    const dialog = await screen.findByRole('dialog');
    fireEvent.click(within(dialog).getByRole('button', { name: /keep it/i }));
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
    expect(remove).not.toHaveBeenCalled();
  });
});
