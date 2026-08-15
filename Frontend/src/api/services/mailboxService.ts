import axiosInstance from '../axiosInstance';
import type { EmailProviderCapability } from '../../email/types';

// Mailbox administration: the IMAP inboxes leads are read from, and the SMTP accounts
// quotes are sent through.
//
// There is deliberately NO password on any response type. The server never returns a stored
// mailbox credential — not masked, not blank — so a type here that claimed one would be
// describing a field that does not exist and inviting a "reuse the existing value" bug.
// Passwords only ever travel outbound-to-server, and an update that omits one keeps what
// is stored.

export type MailboxProtocol = 'IMAP' | 'SMTP';

export type ProbeStage = 'Policy' | 'Dns' | 'Tcp' | 'Tls' | 'Authentication' | 'Mailbox';
export type ProbeStatus = 'Passed' | 'Failed' | 'Skipped' | 'Warning';

export interface MailboxProbeStep {
  stage: ProbeStage;
  status: ProbeStatus;
  detail: string;
  elapsedMs: number;
  /** What the operator should actually do. Null when there is nothing to fix. */
  remedy: string | null;
}

export interface MailboxProbeResult {
  succeeded: boolean;
  protocol: string;
  host: string;
  port: number;
  summary: string;
  steps: MailboxProbeStep[];
  negotiatedSecurity: string | null;
  inboxMessageCount: number | null;
  credentialsSentInClear: boolean;
}

export interface Mailbox {
  id: number;
  configurationName: string;
  emailAddress: string;
  protocol: MailboxProtocol;
  host: string;
  port: number;
  username: string;
  useSsl: boolean;
  pollingInterval: number;
  isActive: boolean;
  createdOn: string;
  lastSuccessfulPollOn: string | null;
  lastPollAttemptOn: string | null;
  lastPollError: string | null;
  consecutivePollFailures: number;
  healthState: 'Healthy' | 'Failing' | 'Never polled' | 'Ready' | 'Disabled';
  healthDetail: string;
  credentialsSentInClear: boolean;
}

export interface MailboxWriteRequest {
  configurationName: string;
  emailAddress: string;
  protocol: MailboxProtocol;
  host: string;
  port: number;
  username: string;
  /** On update, omit or leave blank to keep the stored password. */
  password?: string;
  useSsl: boolean;
  pollingInterval: number;
  isActive: boolean;
  verifyBeforeSave: boolean;
}

export interface MailboxTestRequest {
  mailboxId?: number;
  protocol: MailboxProtocol;
  host: string;
  port: number;
  /**
   * The mailbox address. Sent for EVERY test, because it is the identity the inbound poller
   * signs in with — the test used to send `username` alone and therefore proved nothing about
   * the credential IMAP actually uses.
   */
  emailAddress?: string;
  username: string;
  /** Omit to reuse the stored password of `mailboxId`. */
  password?: string;
  useSsl: boolean;
}

export interface OutboundMailStatus {
  canSendToCustomers: boolean;
  activeSmtpCount: number;
  activeSmtpHosts: string[];
  summary: string;
  hasAmbiguousOutbound: boolean;
  activeImapCount: number;
}

export interface MailboxPreset {
  key: string;
  displayName: string;
  imapHost: string;
  imapPort: number;
  smtpHost: string;
  smtpPort: number;
  useSsl: boolean;
  guidance: string;
}

const mailboxService = {
  getAll: async (): Promise<Mailbox[]> => {
    const { data } = await axiosInstance.get('/api/Mailbox');
    return data;
  },

  create: async (request: MailboxWriteRequest): Promise<Mailbox> => {
    const { data } = await axiosInstance.post('/api/Mailbox', request);
    return data;
  },

  update: async (id: number, request: MailboxWriteRequest): Promise<Mailbox> => {
    const { data } = await axiosInstance.put(`/api/Mailbox/${id}`, request);
    return data;
  },

  remove: async (id: number): Promise<{ deactivated: boolean; message: string }> => {
    const { data } = await axiosInstance.delete(`/api/Mailbox/${id}`);
    return data;
  },

  // POST rather than GET on purpose: the settings include a password, and a GET would put a
  // live mailbox credential into the URL — and from there into browser history and proxy logs.
  test: async (request: MailboxTestRequest): Promise<MailboxProbeResult> => {
    const { data } = await axiosInstance.post('/api/Mailbox/test', request);
    return data;
  },

  getOutboundStatus: async (): Promise<OutboundMailStatus> => {
    const { data } = await axiosInstance.get('/api/Mailbox/outbound-status');
    return data;
  },

  pauseOutbound: async (): Promise<OutboundMailStatus> => {
    const { data } = await axiosInstance.post('/api/Mailbox/outbound/pause');
    return data;
  },

  getPresets: async (): Promise<MailboxPreset[]> => {
    const { data } = await axiosInstance.get('/api/Mailbox/presets');
    return data;
  },

  /**
   * The provider catalogue, which supersedes `getPresets` for anything that needs to know
   * HOW a connection is encrypted.
   *
   * `MailboxPreset` carries a single `useSsl` for both directions, and no single boolean can
   * describe Microsoft 365: inbound is implicit TLS on 993 and outbound is STARTTLS on 587.
   * Applying one flag to the other direction produces a mailbox that is saved with settings
   * that cannot connect — which is the exact failure the catalogue exists to remove, so the
   * screen binds to this and the older shape stays only for callers not yet moved.
   */
  getProviders: async (): Promise<EmailProviderCapability[]> => {
    const { data } = await axiosInstance.get('/api/email/providers');
    return data;
  },
};

export default mailboxService;
