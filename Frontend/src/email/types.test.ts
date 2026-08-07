import { describe, expect, it } from 'vitest';
import { endpointFor, providerWarnings, type EmailProviderCapability } from './types';

/**
 * The asymmetry these tests pin is the whole reason the module exists.
 *
 * One `useSsl` boolean cannot describe Microsoft 365. Inbound IMAP is implicit TLS on 993,
 * where the runtime reads `useSsl: false` as CLEARTEXT. Outbound SMTP is STARTTLS on 587,
 * where the runtime reads `useSsl: false` as STARTTLS. A screen that picks one endpoint's
 * flag and applies it to the other direction saves a mailbox that cannot connect — and the
 * resulting failure reads to the operator as a wrong password.
 */

const microsoft365: EmailProviderCapability = {
  key: 'microsoft365',
  displayName: 'Microsoft 365',
  supportsInbound: true,
  supportsOutbound: true,
  supportsTenantMailbox: true,
  inbound: {
    direction: 'Inbound', transport: 'Imap', host: 'outlook.office365.com',
    port: 993, tls: 'Implicit', useSsl: true,
  },
  outboundSmtp: {
    direction: 'Outbound', transport: 'Smtp', host: 'smtp.office365.com',
    port: 587, tls: 'StartTls', useSsl: false,
  },
  outboundApi: null,
  authModes: ['Password', 'AppPassword'],
  requiredFields: ['host', 'port', 'username', 'password'],
  requiresAppPassword: true,
  smtpAuthDisabledByDefault: true,
  requiresSenderMatchesMailbox: true,   // a mailbox host: From must be the account signed in as
  sendingLimit: '10,000 recipients per day',
  inboundEnablementNote: 'IMAP is off by default on Microsoft 365 mailboxes.',
  guidance: 'Use 993 to read and 587 to send.',
  documentationUrl: null,
};

const sendgrid: EmailProviderCapability = {
  key: 'sendgrid',
  displayName: 'SendGrid',
  supportsInbound: false,
  supportsOutbound: true,
  supportsTenantMailbox: false,
  inbound: null,
  outboundSmtp: {
    direction: 'Outbound', transport: 'Smtp', host: 'smtp.sendgrid.net',
    port: 587, tls: 'StartTls', useSsl: false,
  },
  outboundApi: {
    direction: 'Outbound', transport: 'HttpApi', host: 'api.sendgrid.com',
    port: 443, tls: 'Implicit', useSsl: true,
  },
  authModes: ['ApiKey'],
  requiredFields: ['host', 'port', 'apiKey'],
  requiresAppPassword: false,
  smtpAuthDisabledByDefault: false,
  requiresSenderMatchesMailbox: false,  // a relay: sending as many addresses is the point
  sendingLimit: '100 per day on the free tier',
  inboundEnablementNote: null,
  guidance: 'The username is the literal string "apikey".',
  documentationUrl: null,
};

describe('endpointFor', () => {
  it('never carries one direction’s TLS flag into the other', () => {
    const inbound = endpointFor(microsoft365, 'Inbound')!;
    const outbound = endpointFor(microsoft365, 'Outbound')!;

    expect(inbound.port).toBe(993);
    expect(inbound.useSsl).toBe(true); // implicit TLS — false here would mean cleartext IMAP

    expect(outbound.port).toBe(587);
    expect(outbound.useSsl).toBe(false); // STARTTLS — true here asks for implicit TLS on a
                                         // STARTTLS-only port, and the connection hangs

    // The two directions genuinely disagree. That is the fact a single shared boolean loses.
    expect(inbound.useSsl).not.toBe(outbound.useSsl);
  });

  it('falls back to the HTTP submission API when a provider offers no SMTP endpoint', () => {
    const apiOnly = { ...sendgrid, outboundSmtp: null };
    expect(endpointFor(apiOnly, 'Outbound')?.transport).toBe('HttpApi');
  });

  it('returns null rather than a wrong endpoint for an unsupported direction', () => {
    expect(endpointFor(sendgrid, 'Inbound')).toBeNull();
  });
});

describe('providerWarnings', () => {
  it('warns about SMTP submission only when sending, not when reading a mailbox', () => {
    const outbound = providerWarnings(microsoft365, 'Outbound');
    const inbound = providerWarnings(microsoft365, 'Inbound');

    expect(outbound.some((w) => /SMTP sending per mailbox by default/i.test(w))).toBe(true);
    expect(inbound.some((w) => /SMTP sending per mailbox by default/i.test(w))).toBe(false);

    // …and the reverse: the IMAP-is-off note belongs to the inbound form.
    expect(inbound.some((w) => /IMAP is off by default/i.test(w))).toBe(true);
    expect(outbound.some((w) => /IMAP is off by default/i.test(w))).toBe(false);
  });

  it('states the app-password requirement in both directions, because it applies to both', () => {
    for (const direction of ['Inbound', 'Outbound'] as const)
      expect(providerWarnings(microsoft365, direction).some((w) => /app password/i.test(w))).toBe(true);
  });

  it('says nothing when there is nothing at the provider to switch on', () => {
    const plain = {
      ...sendgrid, sendingLimit: null, requiresAppPassword: false, smtpAuthDisabledByDefault: false,
    };
    expect(providerWarnings(plain, 'Outbound')).toEqual([]);
  });
});
