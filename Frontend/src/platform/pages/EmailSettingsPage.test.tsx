import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { SnackbarProvider } from 'notistack';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import type { PlatformEmailSettings } from '../types';

// Hoisted alongside the vi.mock factory below, which vitest lifts above the imports.
const { saveEmailSettings, testEmailConnection } = vi.hoisted(() => ({
  saveEmailSettings: vi.fn(),
  testEmailConnection: vi.fn(),
}));

const settings: PlatformEmailSettings = {
  provider: 'smtp',
  fromAddress: 'info@kodekinetics.com',
  fromName: 'Nexora',
  replyToAddress: null,
  appBaseUrl: 'https://app.nexora.example',
  smtpHost: 'smtpout.secureserver.net',
  smtpPort: 465,
  smtpUsername: 'info@kodekinetics.com',
  hasSmtpPassword: true,
  smtpEnableSsl: true,
  smtpTimeoutMs: 30000,
  hasSendGridApiKey: false,
  sendGridApiBaseUrl: null,
  outboundGuardMode: 'Live',
  outboundGuardRedirectTo: null,
  outboundGuardAllowedRecipients: [],
  outboundGuardAllowedDomains: [],
  outboundGuardSubjectTag: null,
  origin: 'Database',
  version: 4,
  updatedAtUtc: null,
  updatedBy: null,
  updateReason: null,
};

vi.mock('../api/client', () => ({
  platformApi: {
    getEmailSettings: vi.fn(() => Promise.resolve(settings)),
    getEmailStatus: vi.fn(() =>
      Promise.resolve({
        summary: 'Sending through smtpout.secureserver.net.',
        provider: 'smtp',
        origin: 'Database',
        isSending: true,
        credentialsSet: true,
        fromAddress: 'info@kodekinetics.com',
        fromName: 'Nexora',
        replyToAddress: null,
        appBaseUrl: 'https://app.nexora.example',
        hasSmtpPassword: true,
        hasSendGridApiKey: false,
        outboundGuardMode: 'Live',
        outboundGuardRedirectTo: null,
        lastSuccessfulSendAtUtc: null,
        lastSuccessfulSendProvider: null,
        lastFailureAtUtc: null,
        lastFailureKind: null,
        lastFailureReason: null,
        consecutiveFailures: 0,
        lastVerifiedAtUtc: null,
        lastVerifiedBy: null,
        lastVerifiedRecipient: null,
        lastVerificationFailureAtUtc: null,
        lastVerificationFailureKind: null,
        lastVerificationFailureReason: null,
        configuredAtUtc: null,
        configuredBy: null,
        warnings: [],
      }),
    ),
    listEmailProviders: vi.fn(() => Promise.resolve([])),
    saveEmailSettings,
    testEmailConnection,
    testSendEmail: vi.fn(),
  },
}));

// Owner, so nothing under test is disabled for want of authority — the role gating itself
// is covered by permissions.test.ts.
vi.mock('../auth/usePlatformPermissions', () => ({
  usePlatformPermissions: () => ({
    role: 'Owner',
    isOwner: true,
    canAdministerTenants: true,
    canAdministerBilling: true,
    canImpersonate: true,
    roleUnknown: false,
  }),
}));

import EmailSettingsPage from './EmailSettingsPage';

const renderPage = () => {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={client}>
      <SnackbarProvider>
        <EmailSettingsPage />
      </SnackbarProvider>
    </QueryClientProvider>,
  );
};

beforeEach(() => {
  saveEmailSettings.mockReset().mockResolvedValue(settings);
  testEmailConnection.mockReset().mockResolvedValue({
    succeeded: true, summary: 'Signed in.', direction: 'Outbound', transport: 'Smtp',
    protocol: 'SMTP', host: 'smtpout.secureserver.net', port: 465, tls: 'Implicit',
    steps: [], negotiatedSecurity: 'TLS 1.3', inboxMessageCount: null,
    credentialsSentInClear: false, providerKey: 'godaddy', providerDisplayName: 'GoDaddy',
    providerNotes: [],
  });
});

describe('platform email settings', () => {
  it('keeps the stored password when the box is left empty', async () => {
    renderPage();
    await screen.findByLabelText(/^password$/i);

    fireEvent.change(screen.getByLabelText(/why are you changing this/i), {
      target: { value: 'Switching the send port' },
    });
    fireEvent.click(screen.getByRole('button', { name: /save email settings/i }));

    await waitFor(() => expect(saveEmailSettings).toHaveBeenCalled());
    const sent = saveEmailSettings.mock.calls[0][0];

    // UNDEFINED, never ''. The server reads an empty string as "clear the credential", so
    // sending one because the operator did not retype a password they were never shown
    // would wipe outbound mail on every unrelated edit — a port change would stop
    // activation emails and nothing on screen would say why.
    expect(sent.smtpPassword).toBeUndefined();
    expect(sent.smtpPassword).not.toBe('');

    // The version the operator was editing goes back, so a concurrent edit is a 409 rather
    // than a silent overwrite of somebody else's credentials.
    expect(sent.expectedVersion).toBe(4);
  });

  it('sends a typed password and then drops it from the form', async () => {
    renderPage();
    const password = await screen.findByLabelText(/^password$/i);

    fireEvent.change(password, { target: { value: 'the-real-mailbox-password' } });
    fireEvent.change(screen.getByLabelText(/why are you changing this/i), {
      target: { value: 'Initial mailbox credential' },
    });
    fireEvent.click(screen.getByRole('button', { name: /save email settings/i }));

    await waitFor(() => expect(saveEmailSettings).toHaveBeenCalled());
    expect(saveEmailSettings.mock.calls[0][0].smtpPassword).toBe('the-real-mailbox-password');

    // Nothing keeps a credential in browser memory after it has served its purpose.
    await waitFor(() => expect((password as HTMLInputElement).value).toBe(''));
  });

  it('states the TLS mode the runtime will actually use, and follows the port', async () => {
    renderPage();
    const port = await screen.findByLabelText(/^port$/i);

    // 465 is implicit TLS — the setting GoDaddy publishes, and the one a client that only
    // speaks STARTTLS silently hangs on.
    expect(screen.getByText(/wrapped in TLS before anything is sent/i)).toBeInTheDocument();

    fireEvent.change(port, { target: { value: '587' } });
    expect(await screen.findByText(/upgraded with STARTTLS/i)).toBeInTheDocument();

    fireEvent.click(screen.getByLabelText(/encrypted/i));
    expect(await screen.findByText(/plain text/i)).toBeInTheDocument();
  });

  it('clears a stale connection verdict as soon as the settings change', async () => {
    renderPage();
    await screen.findByLabelText(/^host$/i);

    fireEvent.click(screen.getByRole('button', { name: /test connection/i }));
    expect(await screen.findByTestId('mail-connection-report')).toBeInTheDocument();

    // A green report next to edited settings is the console asserting something it has not
    // tested. Editing the host must retract the claim.
    fireEvent.change(screen.getByLabelText(/^host$/i), { target: { value: 'smtp.office365.com' } });
    await waitFor(() => expect(screen.queryByTestId('mail-connection-report')).not.toBeInTheDocument());
  });

  it('tests the connection with the stored credential when no password is typed', async () => {
    renderPage();
    await screen.findByLabelText(/^host$/i);

    fireEvent.click(screen.getByRole('button', { name: /test connection/i }));
    await waitFor(() => expect(testEmailConnection).toHaveBeenCalled());

    const sent = testEmailConnection.mock.calls[0][0];
    expect(sent.password).toBeUndefined();   // blank means "use the one you already hold"
    expect(sent.tls).toBe('Implicit');       // derived from port 465, not from the toggle alone
  });
});
