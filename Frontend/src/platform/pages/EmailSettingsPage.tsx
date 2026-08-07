import { useEffect, useMemo, useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import {
  Alert,
  AlertTitle,
  Box,
  Button,
  Chip,
  Collapse,
  Divider,
  Grid,
  IconButton,
  InputAdornment,
  MenuItem,
  Paper,
  Stack,
  Switch,
  FormControlLabel,
  TextField,
  Tooltip,
  Typography,
} from '@mui/material';
import {
  NetworkCheck as TestConnectionIcon,
  Send as TestSendIcon,
  Save as SaveIcon,
  Visibility,
  VisibilityOff,
  MarkEmailReadOutlined as VerifiedIcon,
  ReportProblemOutlined as ProblemIcon,
} from '@mui/icons-material';
import { useSnackbar } from 'notistack';
import { platformApi } from '../api/client';
import { platformErrorMessage } from '../api/apiError';
import { platformKeys } from '../api/queryKeys';
import PageHeader from '../components/PageHeader';
import RoleGate from '../components/RoleGate';
import { usePlatformPermissions } from '../auth/usePlatformPermissions';
import { REQUIRED_ROLE_COPY } from '../auth/permissions';
import { ErrorState, LoadingState } from '../components/States';
import { fmtDateTime } from '../components/format';
import ConnectionReport from '../../email/ConnectionReport';
import ProviderPicker from '../../email/ProviderPicker';
import type { MailConnectionTestResult, MailTlsMode } from '../../email/types';
import type {
  OutboundGuardMode,
  PlatformEmailProvider,
  PlatformEmailSettings,
  TestOutboundEmailResult,
} from '../types';

/**
 * Where the product's own outbound mail is configured.
 *
 * <b>Why this screen is not optional.</b> Until it is filled in the provider is `console`,
 * which logs every message and DISCARDS it. Provisioning a tenant reports success, the
 * activation link is minted, and the founding administrator never receives anything —
 * a silent failure that looks exactly like a working system from the operator's side.
 * The status banner at the top of this page exists to make that state impossible to miss.
 *
 * <b>The password is write-only, everywhere.</b> No read path on the server returns it —
 * not masked, not truncated — so the box below is always rendered empty and an empty box
 * means "keep what is stored". Clearing a credential is a separate, deliberate act.
 */

const PROVIDERS: { value: PlatformEmailProvider; label: string; caption: string }[] = [
  {
    value: 'console',
    label: 'Console (nothing is sent)',
    caption: 'Messages are written to the server log and discarded. Development only.',
  },
  {
    value: 'smtp',
    label: 'SMTP',
    caption: 'Any mail host: GoDaddy, Microsoft 365, Google Workspace, Amazon SES, Postmark, Mailgun.',
  },
  {
    value: 'sendgrid',
    label: 'SendGrid (HTTP API)',
    caption: 'Submits over HTTPS rather than SMTP. Useful where outbound SMTP ports are blocked.',
  },
];

const GUARD_MODES: { value: OutboundGuardMode; label: string; caption: string }[] = [
  { value: 'Live', label: 'Live', caption: 'Mail goes to whoever it is addressed to.' },
  {
    value: 'AllowListOnly',
    label: 'Allow list only',
    caption: 'Only the addresses and domains below receive mail. Everything else is dropped.',
  },
  {
    value: 'Redirect',
    label: 'Redirect',
    caption: 'Every message is delivered to one address instead of its real recipient.',
  },
  { value: 'DraftOnly', label: 'Draft only', caption: 'Nothing is transmitted at all.' },
];

/** The editable form. Secrets are separate from the loaded settings because they are the
 *  only fields that are never populated from the server. */
interface FormState {
  provider: PlatformEmailProvider;
  fromAddress: string;
  fromName: string;
  replyToAddress: string;
  appBaseUrl: string;
  smtpHost: string;
  smtpPort: number;
  smtpUsername: string;
  smtpEnableSsl: boolean;
  smtpTimeoutMs: number;
  sendGridApiBaseUrl: string;
  outboundGuardMode: OutboundGuardMode;
  outboundGuardRedirectTo: string;
  outboundGuardAllowedRecipients: string;
  outboundGuardAllowedDomains: string;
  outboundGuardSubjectTag: string;
}

const toForm = (settings: PlatformEmailSettings): FormState => ({
  provider: settings.provider,
  fromAddress: settings.fromAddress ?? '',
  fromName: settings.fromName ?? '',
  replyToAddress: settings.replyToAddress ?? '',
  appBaseUrl: settings.appBaseUrl ?? '',
  smtpHost: settings.smtpHost ?? '',
  smtpPort: settings.smtpPort || 587,
  smtpUsername: settings.smtpUsername ?? '',
  smtpEnableSsl: settings.smtpEnableSsl,
  smtpTimeoutMs: settings.smtpTimeoutMs || 30000,
  sendGridApiBaseUrl: settings.sendGridApiBaseUrl ?? '',
  outboundGuardMode: settings.outboundGuardMode,
  outboundGuardRedirectTo: settings.outboundGuardRedirectTo ?? '',
  outboundGuardAllowedRecipients: (settings.outboundGuardAllowedRecipients ?? []).join(', '),
  outboundGuardAllowedDomains: (settings.outboundGuardAllowedDomains ?? []).join(', '),
  outboundGuardSubjectTag: settings.outboundGuardSubjectTag ?? '',
});

const splitList = (value: string): string[] =>
  value
    .split(/[,\n;]/)
    .map((entry) => entry.trim())
    .filter(Boolean);

/**
 * The TLS mode the runtime will actually use, derived exactly as `SmtpEmailSender` derives
 * it — implicit on 465, STARTTLS on everything else, none when encryption is off.
 *
 * Shown to the operator rather than left implied, because the whole class of defect this
 * module addresses is a port and a TLS setting that disagree.
 */
const effectiveTls = (port: number, enableSsl: boolean): MailTlsMode =>
  !enableSsl ? 'None' : port === 465 ? 'Implicit' : 'StartTls';

export default function EmailSettingsPage() {
  const queryClient = useQueryClient();
  const permissions = usePlatformPermissions();
  const { enqueueSnackbar } = useSnackbar();

  const [form, setForm] = useState<FormState | null>(null);
  const [providerKey, setProviderKey] = useState('');
  const [smtpPassword, setSmtpPassword] = useState('');
  const [sendGridApiKey, setSendGridApiKey] = useState('');
  const [showSecret, setShowSecret] = useState(false);
  const [reason, setReason] = useState('');
  const [testRecipient, setTestRecipient] = useState('');
  const [connectionResult, setConnectionResult] = useState<MailConnectionTestResult | null>(null);
  const [sendResult, setSendResult] = useState<TestOutboundEmailResult | null>(null);

  const settingsQuery = useQuery({
    queryKey: platformKeys.emailSettings(),
    queryFn: () => platformApi.getEmailSettings(),
  });
  const statusQuery = useQuery({
    queryKey: platformKeys.emailStatus(),
    queryFn: () => platformApi.getEmailStatus(),
  });
  const providersQuery = useQuery({
    queryKey: platformKeys.emailProviders(),
    queryFn: () => platformApi.listEmailProviders(),
    staleTime: 60 * 60 * 1000, // a table of published hostnames; it does not move
  });

  const settings = settingsQuery.data;
  const status = statusQuery.data;

  // Load once, then leave the operator's edits alone — a background refetch must never
  // overwrite a half-typed host.
  useEffect(() => {
    if (settings && form === null) setForm(toForm(settings));
  }, [settings, form]);

  const saveMutation = useMutation({
    mutationFn: () => {
      if (!form) throw new Error('Nothing to save.');
      return platformApi.saveEmailSettings({
        provider: form.provider,
        fromAddress: form.fromAddress.trim(),
        fromName: form.fromName.trim(),
        replyToAddress: form.replyToAddress.trim() || null,
        appBaseUrl: form.appBaseUrl.trim(),
        smtpHost: form.smtpHost.trim() || null,
        smtpPort: form.smtpPort,
        smtpUsername: form.smtpUsername.trim() || null,
        // Undefined — not empty string — so an untouched box KEEPS the stored credential.
        // Empty string is the server's "clear it" signal and must never be sent by accident.
        smtpPassword: smtpPassword ? smtpPassword : undefined,
        smtpEnableSsl: form.smtpEnableSsl,
        smtpTimeoutMs: form.smtpTimeoutMs,
        sendGridApiKey: sendGridApiKey ? sendGridApiKey : undefined,
        sendGridApiBaseUrl: form.sendGridApiBaseUrl.trim() || null,
        outboundGuardMode: form.outboundGuardMode,
        outboundGuardRedirectTo: form.outboundGuardRedirectTo.trim() || null,
        outboundGuardAllowedRecipients: splitList(form.outboundGuardAllowedRecipients),
        outboundGuardAllowedDomains: splitList(form.outboundGuardAllowedDomains),
        outboundGuardSubjectTag: form.outboundGuardSubjectTag.trim() || null,
        expectedVersion: settings?.version ?? null,
        reason: reason.trim(),
      });
    },
    onSuccess: (saved) => {
      enqueueSnackbar('Email settings saved', { variant: 'success' });
      // The secrets are gone from the form the moment they are stored — nothing keeps a
      // credential in browser memory after it has served its purpose.
      setSmtpPassword('');
      setSendGridApiKey('');
      setReason('');
      setForm(toForm(saved));
      queryClient.setQueryData(platformKeys.emailSettings(), saved);
      queryClient.invalidateQueries({ queryKey: platformKeys.emailStatus() });
    },
    onError: (error) => enqueueSnackbar(platformErrorMessage(error, 'Save failed'), { variant: 'error' }),
  });

  const connectionMutation = useMutation({
    mutationFn: () => {
      if (!form) throw new Error('Nothing to test.');
      return platformApi.testEmailConnection({
        providerKey: providerKey || undefined,
        host: form.smtpHost.trim() || undefined,
        port: form.smtpPort,
        tls: effectiveTls(form.smtpPort, form.smtpEnableSsl),
        username: form.smtpUsername.trim() || undefined,
        // Blank means "use the stored password" — the console was never given it.
        password: smtpPassword || undefined,
      });
    },
    onSuccess: setConnectionResult,
    onError: (error) =>
      enqueueSnackbar(platformErrorMessage(error, 'Connection test failed'), { variant: 'error' }),
  });

  const sendMutation = useMutation({
    mutationFn: () => {
      if (!form) throw new Error('Nothing to test.');
      return platformApi.testSendEmail({
        recipient: testRecipient.trim(),
        settings: {
          provider: form.provider,
          fromAddress: form.fromAddress.trim() || undefined,
          fromName: form.fromName.trim() || undefined,
          replyToAddress: form.replyToAddress.trim() || undefined,
          smtpHost: form.smtpHost.trim() || undefined,
          smtpPort: form.smtpPort,
          smtpUsername: form.smtpUsername.trim() || undefined,
          smtpPassword: smtpPassword || undefined,
          smtpEnableSsl: form.smtpEnableSsl,
          smtpTimeoutMs: form.smtpTimeoutMs,
          sendGridApiKey: sendGridApiKey || undefined,
          sendGridApiBaseUrl: form.sendGridApiBaseUrl.trim() || undefined,
        },
      });
    },
    onSuccess: (result) => {
      setSendResult(result);
      queryClient.invalidateQueries({ queryKey: platformKeys.emailStatus() });
    },
    onError: (error) => enqueueSnackbar(platformErrorMessage(error, 'Test send failed'), { variant: 'error' }),
  });

  const patch = (changes: Partial<FormState>) => {
    setForm((current) => (current ? { ...current, ...changes } : current));
    // A settings change invalidates the previous verdict. Leaving a green report on screen
    // next to edited settings is the console asserting something it has not tested.
    setConnectionResult(null);
    setSendResult(null);
  };

  /** Only the SMTP-capable entries: the SMTP form cannot describe a send-only HTTP API. */
  const smtpProviders = useMemo(
    () => (providersQuery.data ?? []).filter((provider) => provider.outboundSmtp !== null),
    [providersQuery.data],
  );

  /**
   * The provider currently configured, recognised by HOST rather than by the picker — the host is
   * what the server will actually dial, and it survives a page reload where the picker selection
   * does not.
   */
  const configuredProvider = useMemo(() => {
    const host = form?.smtpHost.trim().toLowerCase();
    if (!host) return null;
    return (
      (providersQuery.data ?? []).find((provider) =>
        [provider.inbound, provider.outboundSmtp, provider.outboundApi].some(
          (endpoint) => endpoint?.host.toLowerCase() === host,
        ),
      ) ?? null
    );
  }, [providersQuery.data, form?.smtpHost]);

  /**
   * Set when the From address and the authenticated mailbox differ on a provider that hosts
   * mailboxes. Null for a relay, where sending as another address is the entire point, and null
   * for a host the catalogue does not recognise — a warning fired on a guess is a warning
   * operators learn to dismiss.
   */
  const senderMismatch = useMemo(() => {
    if (!form || !configuredProvider?.requiresSenderMatchesMailbox) return null;
    const from = form.fromAddress.trim().toLowerCase();
    const username = form.smtpUsername.trim().toLowerCase();
    if (!from || !username || from === username) return null;
    return configuredProvider;
  }, [form, configuredProvider]);

  if (settingsQuery.isLoading || !form) return <LoadingState label="Loading email settings…" />;
  if (settingsQuery.isError)
    return <ErrorState message={platformErrorMessage(settingsQuery.error, 'Could not load email settings')} />;

  const isSmtp = form.provider === 'smtp';
  const isSendGrid = form.provider === 'sendgrid';
  const tls = effectiveTls(form.smtpPort, form.smtpEnableSsl);

  return (
    <Box>
      <PageHeader
        title="Email"
        subtitle="How activation links, invitations and every other message leave the product."
      />

      {/* ---- is mail actually working? ------------------------------------ */}
      {status && (
        <Alert
          severity={status.isSending ? (status.warnings.length > 0 ? 'warning' : 'success') : 'error'}
          icon={status.isSending ? <VerifiedIcon /> : <ProblemIcon />}
          sx={{ mb: 2.5, borderRadius: 3 }}
        >
          <AlertTitle sx={{ fontWeight: 800 }}>
            {status.isSending ? 'Mail is being sent' : 'No mail is leaving this system'}
          </AlertTitle>
          <Typography variant="body2">{status.summary}</Typography>

          <Stack direction="row" spacing={1} sx={{ mt: 1, flexWrap: 'wrap', gap: 1 }}>
            <Chip size="small" label={`Provider: ${status.provider}`} />
            <Chip size="small" label={`Source: ${status.origin}`} />
            <Chip size="small" label={`Guard: ${status.outboundGuardMode}`} />
            {status.consecutiveFailures > 0 && (
              <Chip
                size="small"
                color="error"
                label={`${status.consecutiveFailures} consecutive failure${status.consecutiveFailures === 1 ? '' : 's'}`}
              />
            )}
          </Stack>

          {status.lastVerifiedAtUtc && (
            <Typography variant="caption" sx={{ display: 'block', mt: 1 }}>
              Last verified {fmtDateTime(status.lastVerifiedAtUtc)}
              {status.lastVerifiedBy ? ` by ${status.lastVerifiedBy}` : ''}
              {status.lastVerifiedRecipient ? ` → ${status.lastVerifiedRecipient}` : ''}
            </Typography>
          )}
          {status.lastFailureAtUtc && (
            <Typography variant="caption" sx={{ display: 'block' }}>
              Last failure {fmtDateTime(status.lastFailureAtUtc)}: {status.lastFailureKind}
              {status.lastFailureReason ? ` — ${status.lastFailureReason}` : ''}
            </Typography>
          )}

          {status.warnings.length > 0 && (
            <Stack component="ul" spacing={0.25} sx={{ mt: 1, mb: 0, pl: 2.5 }}>
              {status.warnings.map((warning) => (
                <Typography key={warning} component="li" variant="body2">
                  {warning}
                </Typography>
              ))}
            </Stack>
          )}
        </Alert>
      )}

      {/* ---- sending identity --------------------------------------------- */}
      <Paper sx={{ p: 3, borderRadius: 3, mb: 2.5 }}>
        <Typography variant="h6" sx={{ fontWeight: 800, mb: 0.5 }}>
          Sending identity
        </Typography>
        <Typography variant="caption" color="text.secondary">
          What recipients see, and the address activation links point back to.
        </Typography>

        <Grid container spacing={2} sx={{ mt: 1 }}>
          <Grid size={{ xs: 12, md: 4 }}>
            <TextField
              select
              fullWidth
              size="small"
              label="Transport"
              value={form.provider}
              onChange={(event) => patch({ provider: event.target.value as PlatformEmailProvider })}
              helperText={PROVIDERS.find((p) => p.value === form.provider)?.caption}
            >
              {PROVIDERS.map((option) => (
                <MenuItem key={option.value} value={option.value}>
                  {option.label}
                </MenuItem>
              ))}
            </TextField>
          </Grid>
          <Grid size={{ xs: 12, md: 4 }}>
            <TextField
              fullWidth
              size="small"
              label="From address"
              value={form.fromAddress}
              onChange={(event) => patch({ fromAddress: event.target.value })}
              helperText="Most hosts require this to be the mailbox you authenticate as."
            />
          </Grid>
          <Grid size={{ xs: 12, md: 4 }}>
            <TextField
              fullWidth
              size="small"
              label="From name"
              value={form.fromName}
              onChange={(event) => patch({ fromName: event.target.value })}
            />
          </Grid>
          <Grid size={{ xs: 12, md: 6 }}>
            <TextField
              fullWidth
              size="small"
              label="Reply-to address (optional)"
              value={form.replyToAddress}
              onChange={(event) => patch({ replyToAddress: event.target.value })}
              helperText="Where replies go when the From address is unattended."
            />
          </Grid>
          <Grid size={{ xs: 12, md: 6 }}>
            <TextField
              fullWidth
              size="small"
              label="Application base URL"
              value={form.appBaseUrl}
              onChange={(event) => patch({ appBaseUrl: event.target.value })}
              helperText="Activation and invitation links are built from this. A wrong value produces links that resolve nowhere."
            />
          </Grid>
        </Grid>
      </Paper>

      {/* ---- SMTP --------------------------------------------------------- */}
      <Collapse in={isSmtp} unmountOnExit>
        <Paper sx={{ p: 3, borderRadius: 3, mb: 2.5 }}>
          <Typography variant="h6" sx={{ fontWeight: 800, mb: 0.5 }}>
            SMTP server
          </Typography>
          <Typography variant="caption" color="text.secondary">
            Pick your provider and the published settings are filled in for you.
          </Typography>

          <Box sx={{ mt: 2 }}>
            <ProviderPicker
              providers={smtpProviders}
              value={providerKey}
              direction="Outbound"
              onApply={(provider, endpoint) => {
                setProviderKey(provider.key);
                if (endpoint) patch({ smtpHost: endpoint.host, smtpPort: endpoint.port, smtpEnableSsl: endpoint.useSsl });
              }}
              helperText="Not listed? Leave this alone and type the server settings your provider gave you."
            />
          </Box>

          <Grid container spacing={2} sx={{ mt: 1 }}>
            <Grid size={{ xs: 12, md: 6 }}>
              <TextField
                fullWidth
                size="small"
                label="Host"
                value={form.smtpHost}
                onChange={(event) => patch({ smtpHost: event.target.value })}
              />
            </Grid>
            <Grid size={{ xs: 6, md: 3 }}>
              <TextField
                fullWidth
                size="small"
                type="number"
                label="Port"
                value={form.smtpPort}
                onChange={(event) => patch({ smtpPort: Number(event.target.value) })}
              />
            </Grid>
            <Grid size={{ xs: 6, md: 3 }}>
              <FormControlLabel
                sx={{ mt: 0.5 }}
                control={
                  <Switch
                    checked={form.smtpEnableSsl}
                    onChange={(event) => patch({ smtpEnableSsl: event.target.checked })}
                  />
                }
                label="Encrypted"
              />
            </Grid>

            <Grid size={{ xs: 12 }}>
              {/*
                The single most useful line on this screen. The runtime derives its TLS mode
                from the port, and an operator who typed 587 while expecting implicit TLS has
                no other way to discover the disagreement before mail silently stops.
              */}
              <Alert severity={tls === 'None' ? 'warning' : 'info'} sx={{ borderRadius: 2 }}>
                {tls === 'Implicit' &&
                  `Port ${form.smtpPort}: the connection is wrapped in TLS before anything is sent (implicit TLS/SSL).`}
                {tls === 'StartTls' &&
                  `Port ${form.smtpPort}: the connection starts in the clear and is upgraded with STARTTLS, which is required — not optional.`}
                {tls === 'None' &&
                  'Encryption is off. The password will cross the network in plain text. Only ever correct for an internal relay that requires no credential.'}
              </Alert>
            </Grid>

            <Grid size={{ xs: 12, md: 6 }}>
              <TextField
                fullWidth
                size="small"
                label="Username"
                value={form.smtpUsername}
                onChange={(event) => patch({ smtpUsername: event.target.value })}
                autoComplete="off"
                helperText="Usually the full mailbox address."
              />
            </Grid>

            {senderMismatch && (
              <Grid size={{ xs: 12 }}>
                {/*
                  Shown while typing, not after a reload. The connection test will pass with these
                  settings — it authenticates and disconnects, and never states a From address — so
                  without this the first evidence of the problem is a tenant whose founding
                  administrator never received their activation link.
                */}
                <Alert severity="warning" sx={{ borderRadius: 2 }}>
                  <AlertTitle sx={{ fontWeight: 800 }}>
                    The From address is not the mailbox you are signing in as
                  </AlertTitle>
                  Sending as <strong>{form.fromAddress.trim()}</strong> while authenticating as{' '}
                  <strong>{form.smtpUsername.trim()}</strong>. {senderMismatch.displayName} hosts
                  mailboxes rather than relaying for a domain, so it will reject the message —
                  the connection test will still pass, because it never states a From address.
                  Either send as the mailbox you authenticate as, or add it as an alias at{' '}
                  {senderMismatch.displayName} first.
                </Alert>
              </Grid>
            )}
            <Grid size={{ xs: 12, md: 6 }}>
              <TextField
                fullWidth
                size="small"
                type={showSecret ? 'text' : 'password'}
                label="Password"
                value={smtpPassword}
                onChange={(event) => {
                  setSmtpPassword(event.target.value);
                  setConnectionResult(null);
                  setSendResult(null);
                }}
                autoComplete="new-password"
                placeholder={settings?.hasSmtpPassword ? 'A password is stored — leave blank to keep it' : ''}
                helperText={
                  settings?.hasSmtpPassword
                    ? 'Stored and encrypted. It is never sent back to this screen; leave blank to keep it.'
                    : 'Encrypted before it is stored, and no read path ever returns it.'
                }
                slotProps={{
                  input: {
                    endAdornment: (
                      <InputAdornment position="end">
                        <Tooltip title={showSecret ? 'Hide' : 'Show'}>
                          <IconButton size="small" onClick={() => setShowSecret((value) => !value)} edge="end">
                            {showSecret ? <VisibilityOff fontSize="small" /> : <Visibility fontSize="small" />}
                          </IconButton>
                        </Tooltip>
                      </InputAdornment>
                    ),
                  },
                }}
              />
            </Grid>
            <Grid size={{ xs: 12, md: 4 }}>
              <TextField
                fullWidth
                size="small"
                type="number"
                label="Timeout (ms)"
                value={form.smtpTimeoutMs}
                onChange={(event) => patch({ smtpTimeoutMs: Number(event.target.value) })}
              />
            </Grid>
          </Grid>

          <Stack direction="row" spacing={1.5} sx={{ mt: 2, flexWrap: 'wrap', gap: 1.5 }}>
            <RoleGate allowed={permissions.isOwner} requirement={REQUIRED_ROLE_COPY.owner}>
              {(disabled) => (
                <Button
                  variant="outlined"
                  startIcon={<TestConnectionIcon />}
                  disabled={disabled || connectionMutation.isPending || !form.smtpHost.trim()}
                  onClick={() => connectionMutation.mutate()}
                >
                  {connectionMutation.isPending ? 'Testing…' : 'Test connection'}
                </Button>
              )}
            </RoleGate>
            <Typography variant="caption" color="text.secondary" sx={{ alignSelf: 'center' }}>
              Connects, signs in and disconnects. Nothing is sent, so it is safe against a live relay.
            </Typography>
          </Stack>

          <Collapse in={connectionResult !== null} unmountOnExit>
            {connectionResult && <ConnectionReport result={connectionResult} />}
          </Collapse>
        </Paper>
      </Collapse>

      {/* ---- SendGrid ------------------------------------------------------ */}
      <Collapse in={isSendGrid} unmountOnExit>
        <Paper sx={{ p: 3, borderRadius: 3, mb: 2.5 }}>
          <Typography variant="h6" sx={{ fontWeight: 800, mb: 1.5 }}>
            SendGrid
          </Typography>
          <Grid container spacing={2}>
            <Grid size={{ xs: 12, md: 6 }}>
              <TextField
                fullWidth
                size="small"
                type={showSecret ? 'text' : 'password'}
                label="API key"
                value={sendGridApiKey}
                onChange={(event) => {
                  setSendGridApiKey(event.target.value);
                  setSendResult(null);
                }}
                autoComplete="new-password"
                placeholder={settings?.hasSendGridApiKey ? 'A key is stored — leave blank to keep it' : ''}
                helperText={
                  settings?.hasSendGridApiKey
                    ? 'Stored and encrypted. Leave blank to keep it.'
                    : 'Encrypted before it is stored, and no read path ever returns it.'
                }
              />
            </Grid>
            <Grid size={{ xs: 12, md: 6 }}>
              <TextField
                fullWidth
                size="small"
                label="API base URL (optional)"
                value={form.sendGridApiBaseUrl}
                onChange={(event) => patch({ sendGridApiBaseUrl: event.target.value })}
                helperText="Leave blank for SendGrid's own endpoint."
              />
            </Grid>
          </Grid>
        </Paper>
      </Collapse>

      {/* ---- outbound guard ------------------------------------------------ */}
      <Paper sx={{ p: 3, borderRadius: 3, mb: 2.5 }}>
        <Typography variant="h6" sx={{ fontWeight: 800, mb: 0.5 }}>
          Outbound guard
        </Typography>
        <Typography variant="caption" color="text.secondary">
          What stops a test database full of plausible addresses from mailing real people.
        </Typography>

        <Grid container spacing={2} sx={{ mt: 1 }}>
          <Grid size={{ xs: 12, md: 4 }}>
            <TextField
              select
              fullWidth
              size="small"
              label="Mode"
              value={form.outboundGuardMode}
              onChange={(event) => patch({ outboundGuardMode: event.target.value as OutboundGuardMode })}
              helperText={GUARD_MODES.find((m) => m.value === form.outboundGuardMode)?.caption}
            >
              {GUARD_MODES.map((option) => (
                <MenuItem key={option.value} value={option.value}>
                  {option.label}
                </MenuItem>
              ))}
            </TextField>
          </Grid>
          <Grid size={{ xs: 12, md: 4 }}>
            <TextField
              fullWidth
              size="small"
              label="Redirect everything to"
              value={form.outboundGuardRedirectTo}
              onChange={(event) => patch({ outboundGuardRedirectTo: event.target.value })}
              disabled={form.outboundGuardMode !== 'Redirect'}
            />
          </Grid>
          <Grid size={{ xs: 12, md: 4 }}>
            <TextField
              fullWidth
              size="small"
              label="Subject tag"
              value={form.outboundGuardSubjectTag}
              onChange={(event) => patch({ outboundGuardSubjectTag: event.target.value })}
              helperText="Prefixed to every subject so a redirected message is obviously not real."
            />
          </Grid>
          <Grid size={{ xs: 12, md: 6 }}>
            <TextField
              fullWidth
              size="small"
              label="Allowed recipients"
              value={form.outboundGuardAllowedRecipients}
              onChange={(event) => patch({ outboundGuardAllowedRecipients: event.target.value })}
              helperText="Comma separated."
            />
          </Grid>
          <Grid size={{ xs: 12, md: 6 }}>
            <TextField
              fullWidth
              size="small"
              label="Allowed domains"
              value={form.outboundGuardAllowedDomains}
              onChange={(event) => patch({ outboundGuardAllowedDomains: event.target.value })}
              helperText="Comma separated, without the @."
            />
          </Grid>
        </Grid>
      </Paper>

      {/* ---- save ---------------------------------------------------------- */}
      <Paper sx={{ p: 3, borderRadius: 3, mb: 2.5 }}>
        <TextField
          fullWidth
          size="small"
          label="Why are you changing this?"
          value={reason}
          onChange={(event) => setReason(event.target.value)}
          helperText="Recorded in the audit log. Changing the product's sending identity is a privileged act."
        />
        {settings?.updatedAtUtc && (
          <Typography variant="caption" color="text.secondary" sx={{ display: 'block', mt: 1 }}>
            Last changed {fmtDateTime(settings.updatedAtUtc)}
            {settings.updatedBy ? ` by ${settings.updatedBy}` : ''}
            {settings.updateReason ? ` — “${settings.updateReason}”` : ''}
          </Typography>
        )}
        <Box sx={{ mt: 2 }}>
          <RoleGate allowed={permissions.isOwner} requirement={REQUIRED_ROLE_COPY.owner}>
            {(disabled) => (
              <Button
                variant="contained"
                startIcon={<SaveIcon />}
                disabled={disabled || saveMutation.isPending || reason.trim().length < 3}
                onClick={() => saveMutation.mutate()}
              >
                {saveMutation.isPending ? 'Saving…' : 'Save email settings'}
              </Button>
            )}
          </RoleGate>
        </Box>
      </Paper>

      <Divider sx={{ mb: 2.5 }} />

      {/* ---- verification send --------------------------------------------- */}
      <Paper sx={{ p: 3, borderRadius: 3 }}>
        <Typography variant="h6" sx={{ fontWeight: 800, mb: 0.5 }}>
          Send a test message
        </Typography>
        <Typography variant="caption" color="text.secondary">
          Sends one real message using what is typed above, saved or not. The only proof that mail
          reaches an inbox rather than merely leaving the building.
        </Typography>

        <Stack direction="row" spacing={1.5} sx={{ mt: 2, flexWrap: 'wrap', gap: 1.5 }}>
          <TextField
            size="small"
            label="Recipient"
            value={testRecipient}
            onChange={(event) => setTestRecipient(event.target.value)}
            sx={{ minWidth: 320 }}
          />
          <RoleGate allowed={permissions.isOwner} requirement={REQUIRED_ROLE_COPY.owner}>
            {(disabled) => (
              <Button
                variant="outlined"
                startIcon={<TestSendIcon />}
                disabled={disabled || sendMutation.isPending || !testRecipient.trim()}
                onClick={() => sendMutation.mutate()}
              >
                {sendMutation.isPending ? 'Sending…' : 'Send test email'}
              </Button>
            )}
          </RoleGate>
        </Stack>

        <Collapse in={sendResult !== null} unmountOnExit>
          {sendResult && (
            <Alert
              severity={sendResult.succeeded ? (sendResult.transmitted ? 'success' : 'warning') : 'error'}
              sx={{ mt: 2, borderRadius: 2 }}
            >
              <AlertTitle sx={{ fontWeight: 800 }}>
                {/*
                  "Succeeded" and "transmitted" are different questions. The console provider
                  succeeds at doing nothing, and an operator must never read that as proof.
                */}
                {sendResult.succeeded
                  ? sendResult.transmitted
                    ? 'Message accepted by the provider'
                    : 'Nothing was transmitted'
                  : 'Send failed'}
              </AlertTitle>
              <Typography variant="body2">{sendResult.message}</Typography>
              <Typography variant="caption" sx={{ display: 'block', mt: 0.5 }}>
                {sendResult.kind !== 'None' ? `${sendResult.kind} · ` : ''}
                {sendResult.providerStatus ? `${sendResult.providerStatus} · ` : ''}
                {sendResult.intendedRecipient !== sendResult.effectiveRecipient
                  ? `redirected to ${sendResult.effectiveRecipient} by the outbound guard · `
                  : ''}
                {sendResult.elapsedMs} ms
              </Typography>
            </Alert>
          )}
        </Collapse>
      </Paper>
    </Box>
  );
}
