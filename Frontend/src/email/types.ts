// ---------------------------------------------------------------------------
// The email connectivity module, client side.
//
// One shape for "which provider, connecting how", shared by the platform console
// (the product's own outbound relay) and the tenant setup screen (the mailboxes
// RFQs are read from and quotes are sent through). Both planes render the same
// provider catalogue and the same six-stage connection report, because the server
// answers both from `IMailConnectionTester` — a second, differently-shaped
// diagnosis would give an operator two qualities of answer to one question.
//
// Mirrors `Backend/ERP_RFQ_Automation/Email/EmailProviderDtos.cs`. Enums travel
// as strings (each backend enum carries JsonStringEnumConverter), so the unions
// below are the wire values verbatim.
// ---------------------------------------------------------------------------

export type MailDirection = 'Inbound' | 'Outbound';
export type MailTransport = 'Imap' | 'Pop3' | 'Smtp' | 'HttpApi';
export type MailAuthMode = 'Password' | 'AppPassword' | 'ApiKey' | 'OAuth2';

/**
 * How TLS is established. The distinction the whole module exists to preserve:
 * `Implicit` wraps the socket in TLS before a byte of protocol is spoken (993, 465),
 * `StartTls` upgrades a connection that starts in the clear (587).
 */
export type MailTlsMode = 'None' | 'StartTls' | 'Implicit';

export interface EmailEndpoint {
  direction: MailDirection;
  transport: MailTransport;
  host: string;
  port: number;
  tls: MailTlsMode;
  /**
   * What to store in this endpoint's `useSsl` column.
   *
   * Carried per endpoint rather than derived on the client, because the derivation is
   * ASYMMETRIC between the two directions and getting it wrong is the defect this
   * module removes: for IMAP the runtime reads `false` as *cleartext*, and for SMTP it
   * reads `false` as *STARTTLS*. Microsoft 365 is inbound-implicit (993) and
   * outbound-STARTTLS (587) at the same time, so a single shared boolean cannot
   * describe it at all.
   */
  useSsl: boolean;
}

export interface EmailProviderCapability {
  key: string;
  displayName: string;

  supportsInbound: boolean;
  supportsOutbound: boolean;
  /** False for send-only API providers, which a tenant mailbox row cannot represent. */
  supportsTenantMailbox: boolean;

  inbound: EmailEndpoint | null;
  outboundSmtp: EmailEndpoint | null;
  outboundApi: EmailEndpoint | null;

  authModes: MailAuthMode[];
  /** Which boxes this provider actually needs, by name — an API key for SendGrid, a
   *  username/password pair for GoDaddy — so neither screen hardcodes either. */
  requiredFields: string[];

  /** The account password will be refused; only a provider-issued app password works. */
  requiresAppPassword: boolean;
  /** SMTP submission is off until somebody turns it on at the provider. */
  smtpAuthDisabledByDefault: boolean;
  /** A ceiling worth stating before it is discovered as a partial outage. */
  sendingLimit: string | null;
  inboundEnablementNote: string | null;

  guidance: string;
  documentationUrl: string | null;
}

// --- the staged connection report ------------------------------------------

export type MailProbeStage = 'Policy' | 'Dns' | 'Tcp' | 'Tls' | 'Authentication' | 'Mailbox';
export type MailProbeStatus = 'Passed' | 'Failed' | 'Skipped' | 'Warning';

export interface MailProbeStep {
  stage: MailProbeStage;
  status: MailProbeStatus;
  detail: string;
  elapsedMs: number;
  /** What to actually DO. Null when there is nothing to fix. This field is what turns
   *  a support ticket into a self-service correction. */
  remedy: string | null;
}

export interface MailConnectionTestResult {
  succeeded: boolean;
  summary: string;
  direction: MailDirection;
  transport: MailTransport;
  protocol: string;
  host: string;
  port: number;
  tls: MailTlsMode;
  steps: MailProbeStep[];
  negotiatedSecurity: string | null;
  inboxMessageCount: number | null;
  /** True when the credential would travel unencrypted. Never a passing result. */
  credentialsSentInClear: boolean;
  providerKey: string | null;
  providerDisplayName: string | null;
  /** Provider-specific notes chosen by WHAT FAILED — an auth failure on Microsoft 365
   *  names SMTP submission rather than guessing at the password. */
  providerNotes: string[];
}

/** Plain-English stage names. The operator does not need to know what a TCP handshake is. */
export const MAIL_STAGE_LABEL: Record<MailProbeStage, string> = {
  Policy: 'Address allowed',
  Dns: 'Hostname found',
  Tcp: 'Port reachable',
  Tls: 'Encryption',
  Authentication: 'Sign in',
  Mailbox: 'Mailbox access',
};

/**
 * The endpoint a screen should fill in from, for the direction it is configuring.
 * SMTP is preferred over the HTTP submission API because a mailbox row can only
 * describe SMTP; a caller that wants the API endpoint asks for it by name.
 */
export const endpointFor = (
  provider: EmailProviderCapability,
  direction: MailDirection,
): EmailEndpoint | null =>
  direction === 'Inbound' ? provider.inbound : provider.outboundSmtp ?? provider.outboundApi;

/**
 * The sentences worth putting in front of an operator BEFORE they type a password —
 * every one of them describes a setting that lives at the provider, not in this
 * product, and every one produces a failure that reads like a wrong password.
 */
export const providerWarnings = (
  provider: EmailProviderCapability,
  direction?: MailDirection,
): string[] => {
  const warnings: string[] = [];

  if (provider.requiresAppPassword)
    warnings.push(
      `${provider.displayName} refuses your normal account password. Generate an app password in your ${provider.displayName} security settings and use that here.`,
    );

  if (provider.smtpAuthDisabledByDefault && direction !== 'Inbound')
    warnings.push(
      `${provider.displayName} disables SMTP sending per mailbox by default. If sign-in fails with a password you know is right, that is why — it has to be switched on at the provider first.`,
    );

  if (provider.inboundEnablementNote && direction !== 'Outbound')
    warnings.push(provider.inboundEnablementNote);

  if (provider.sendingLimit && direction !== 'Inbound')
    warnings.push(`Sending limit: ${provider.sendingLimit}`);

  return warnings;
};
