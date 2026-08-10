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
    listEmailProviders: vi.fn(() =>
      Promise.resolve([
        {
          key: 'godaddy',
          displayName: 'GoDaddy',
          supportsInbound: true,
          supportsOutbound: true,
          supportsTenantMailbox: true,
          inbound: {
            direction: 'Inbound', transport: 'Imap', host: 'imap.secureserver.net',
            port: 993, tls: 'Implicit', useSsl: true,
          },
          outboundSmtp: {
            direction: 'Outbound', transport: 'Smtp', host: 'smtpout.secureserver.net',
            port: 465, tls: 'Implicit', useSsl: true,
          },
          outboundApi: null,
          authModes: ['Password'],
          requiredFields: ['host', 'port', 'username', 'password'],
          requiresAppPassword: false,
          smtpAuthDisabledByDefault: false,
          requiresSenderMatchesMailbox: true,
          sendingLimit: '250 per day',
          inboundEnablementNote: null,
          guidance: 'Port 465, SSL/TLS.',
          documentationUrl: null,
        },
      ]),
    ),
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

  it('warns when the From address is not the mailbox being authenticated as', async () => {
    renderPage();
    const from = await screen.findByLabelText(/from address/i);

    // The fixture starts aligned (info@ signing in as info@), so nothing is said.
    await waitFor(() =>
      expect(screen.queryByText(/not the mailbox you are signing in as/i)).not.toBeInTheDocument(),
    );

    // The configuration everyone reaches for: send as the address customers should see, using
    // the credentials you happen to hold. GoDaddy rejects it — at SEND time, long after the
    // connection test has passed, because a connection test never states a From address.
    fireEvent.change(from, { target: { value: 'zack@kodekinetics.com' } });
    expect(await screen.findByText(/not the mailbox you are signing in as/i)).toBeInTheDocument();
    expect(screen.getByText(/GoDaddy hosts mailboxes rather than relaying/i)).toBeInTheDocument();
  });

  it('says nothing about an unrecognised host, rather than guessing', async () => {
    renderPage();
    await screen.findByLabelText(/^host$/i);

    fireEvent.change(screen.getByLabelText(/^host$/i), { target: { value: 'mail.internal.example' } });
    fireEvent.change(screen.getByLabelText(/from address/i), { target: { value: 'zack@kodekinetics.com' } });

    // It might be a corporate relay that is supposed to send for the whole domain. A warning
    // fired on a guess is one operators learn to dismiss.
    await waitFor(() =>
      expect(screen.queryByText(/not the mailbox you are signing in as/i)).not.toBeInTheDocument(),
    );
  });

  it('says why Save is unavailable instead of just disabling it', async () => {
    renderPage();
    await screen.findByLabelText(/^host$/i);

    // THE reported defect. The audit reason has always been mandatory, but the only evidence
    // was a button that did not respond — no asterisk, no error, no tooltip. An operator who
    // filled in the whole form, clicked, and saw nothing happen could only conclude that the
    // screen does not save. The precondition has to be readable without hovering anything.
    const save = screen.getByRole('button', { name: /save email settings/i });
    expect(save).toBeDisabled();
    expect(screen.getByText(/enter why you are changing this before saving/i)).toBeInTheDocument();

    fireEvent.change(screen.getByLabelText(/why are you changing this/i), {
      target: { value: 'Rotating the mailbox password' },
    });

    await waitFor(() => expect(save).toBeEnabled());
    expect(screen.queryByText(/enter why you are changing this before saving/i)).not.toBeInTheDocument();
  });

  it('blocks a port outside the range the server accepts, and names it', async () => {
    renderPage();
    const port = await screen.findByLabelText(/^port$/i);

    fireEvent.change(screen.getByLabelText(/why are you changing this/i), {
      target: { value: 'Correcting the port' },
    });

    // `Number('')` is 0, which the server refuses with a raw [Range(1,65535)] message. Clearing
    // the box to retype it is an ordinary act and must not produce an opaque "Save failed".
    fireEvent.change(port, { target: { value: '' } });

    await waitFor(() =>
      expect(screen.getByRole('button', { name: /save email settings/i })).toBeDisabled(),
    );
    expect(screen.getByText(/must be between 1 and 65535/i)).toBeInTheDocument();
    expect(saveEmailSettings).not.toHaveBeenCalled();
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
