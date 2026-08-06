import React from 'react';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import {
  Box, Typography, Paper, Button, Grid, TextField, CircularProgress, Stack, Chip,
  Dialog, DialogTitle, DialogContent, DialogActions, MenuItem, Switch, FormControlLabel,
  Alert, AlertTitle, Divider, IconButton, Tooltip, LinearProgress, InputAdornment,
  ToggleButton, ToggleButtonGroup, Collapse,
} from '@mui/material';
import {
  MarkEmailRead as InboxIcon, Send as SmtpIcon, Add as AddIcon, Edit as EditIcon,
  Delete as DeleteIcon, NetworkCheck as TestIcon, CheckCircle as PassIcon,
  Cancel as FailIcon, RemoveCircleOutlined as SkipIcon, WarningAmber as WarnIcon,
  Visibility, VisibilityOff, ShieldOutlined as ShieldIcon, PauseCircleOutlined as PauseIcon,
} from '@mui/icons-material';
import { toast } from 'react-hot-toast';
import { useAuth } from '../../../context/AuthContext';
import mailboxService, {
  type Mailbox, type MailboxWriteRequest, type MailboxProbeResult, type MailboxProtocol,
  type ProbeStatus,
} from '../../../api/services/mailboxService';

const EMPTY: MailboxWriteRequest = {
  configurationName: '', emailAddress: '', protocol: 'IMAP', host: '', port: 993,
  username: '', password: '', useSsl: true, pollingInterval: 5, isActive: true,
  verifyBeforeSave: true,
};

const STAGE_LABEL: Record<string, string> = {
  Policy: 'Address allowed',
  Dns: 'Hostname found',
  Tcp: 'Port reachable',
  Tls: 'Encryption',
  Authentication: 'Sign in',
  Mailbox: 'Mailbox access',
};

const STATUS_STYLE: Record<ProbeStatus, { icon: React.ReactNode; colour: string }> = {
  Passed: { icon: <PassIcon fontSize="small" />, colour: 'success.main' },
  Failed: { icon: <FailIcon fontSize="small" />, colour: 'error.main' },
  Warning: { icon: <WarnIcon fontSize="small" />, colour: 'warning.main' },
  Skipped: { icon: <SkipIcon fontSize="small" />, colour: 'text.disabled' },
};

const HEALTH_COLOUR: Record<string, 'success' | 'error' | 'warning' | 'default'> = {
  Healthy: 'success', Ready: 'success', Failing: 'error',
  'Never polled': 'warning', Disabled: 'default',
};

/** Renders the six-stage connection report. The value is entirely in showing WHICH stage
 *  failed and what to do — a bare "connection failed" is what this replaces. */
const ProbeReport: React.FC<{ result: MailboxProbeResult }> = ({ result }) => (
  <Box sx={{ mt: 2 }}>
    <Alert severity={result.succeeded ? 'success' : 'error'} sx={{ mb: 1.5, borderRadius: 2 }}>
      <AlertTitle sx={{ fontWeight: 800 }}>
        {result.succeeded ? 'Connection successful' : 'Connection failed'}
      </AlertTitle>
      {result.summary}
      {result.negotiatedSecurity && result.negotiatedSecurity !== 'None' && (
        <Typography variant="caption" sx={{ display: 'block', mt: 0.5 }}>
          Encrypted with {result.negotiatedSecurity}.
        </Typography>
      )}
    </Alert>

    {result.credentialsSentInClear && (
      <Alert severity="warning" icon={<ShieldIcon />} sx={{ mb: 1.5, borderRadius: 2 }}>
        <AlertTitle sx={{ fontWeight: 800 }}>The password is sent unencrypted</AlertTitle>
        With these settings the mailbox password crosses the network in clear text, readable by
        any network in between. Turn on “Use a secure connection”.
      </Alert>
    )}

    <Paper variant="outlined" sx={{ borderRadius: 2, overflow: 'hidden' }}>
      {result.steps.map((step, index) => (
        <Box
          key={step.stage}
          sx={{
            display: 'flex', gap: 1.5, p: 1.5, alignItems: 'flex-start',
            borderTop: index === 0 ? 'none' : '1px solid', borderColor: 'divider',
            opacity: step.status === 'Skipped' ? 0.55 : 1,
          }}
        >
          <Box sx={{ color: STATUS_STYLE[step.status].colour, display: 'flex', pt: '2px' }}>
            {STATUS_STYLE[step.status].icon}
          </Box>
          <Box sx={{ flex: 1, minWidth: 0 }}>
            <Stack direction="row" spacing={1} sx={{ alignItems: 'center' }}>
              <Typography sx={{ fontWeight: 700, fontSize: '0.85rem' }}>
                {STAGE_LABEL[step.stage] ?? step.stage}
              </Typography>
              {step.elapsedMs > 0 && (
                <Typography variant="caption" color="text.secondary">{step.elapsedMs} ms</Typography>
              )}
            </Stack>
            <Typography variant="body2" color="text.secondary" sx={{ mt: 0.25 }}>
              {step.detail}
            </Typography>
            {step.remedy && (
              <Alert severity="info" sx={{ mt: 1, py: 0.25, borderRadius: 1.5 }}>
                <Typography variant="body2">{step.remedy}</Typography>
              </Alert>
            )}
          </Box>
        </Box>
      ))}
    </Paper>
  </Box>
);

const MailboxPage: React.FC = () => {
  const queryClient = useQueryClient();
  // The API already refuses these (403), but offering a control that always fails is its own
  // defect — the operator cannot tell "not allowed" from "broken". Read-only users get a
  // read-only screen. This mirrors the exact actions the controller gates.
  const { hasPermission } = useAuth();
  const canCreate = hasPermission('Email & SMTP', 'create');
  const canEdit = hasPermission('Email & SMTP', 'edit');
  const canDelete = hasPermission('Email & SMTP', 'delete');
  const [dialogOpen, setDialogOpen] = React.useState(false);
  const [editing, setEditing] = React.useState<Mailbox | null>(null);
  const [form, setForm] = React.useState<MailboxWriteRequest>(EMPTY);
  const [showPassword, setShowPassword] = React.useState(false);
  const [probe, setProbe] = React.useState<MailboxProbeResult | null>(null);

  const { data: mailboxes = [], isLoading } = useQuery({
    queryKey: ['mailboxes'], queryFn: mailboxService.getAll,
  });
  const { data: outbound } = useQuery({
    queryKey: ['mailbox-outbound-status'], queryFn: mailboxService.getOutboundStatus,
  });
  const { data: presets = [] } = useQuery({
    queryKey: ['mailbox-presets'], queryFn: mailboxService.getPresets,
  });

  const refresh = () => {
    queryClient.invalidateQueries({ queryKey: ['mailboxes'] });
    queryClient.invalidateQueries({ queryKey: ['mailbox-outbound-status'] });
  };

  const testMutation = useMutation({
    mutationFn: () => mailboxService.test({
      mailboxId: editing?.id, protocol: form.protocol, host: form.host, port: form.port,
      username: form.username, password: form.password || undefined, useSsl: form.useSsl,
    }),
    onSuccess: setProbe,
    onError: (error: any) =>
      toast.error(error?.response?.data?.message ?? error?.response?.data ?? 'The test could not be run'),
  });

  const saveMutation = useMutation({
    mutationFn: () => (editing
      ? mailboxService.update(editing.id, form)
      : mailboxService.create(form)),
    onSuccess: () => {
      toast.success(editing ? 'Mailbox updated' : 'Mailbox added');
      setDialogOpen(false);
      refresh();
    },
    onError: (error: any) => {
      // A 422 carries the full staged report: show it rather than a one-line failure.
      const body = error?.response?.data;
      if (body?.probe) {
        setProbe(body.probe);
        toast.error(body.message ?? 'The mailbox could not connect');
      } else {
        toast.error(body?.message ?? body ?? 'Could not save the mailbox');
      }
    },
  });

  const deleteMutation = useMutation({
    mutationFn: (id: number) => mailboxService.remove(id),
    onSuccess: (result) => { toast.success(result.message); refresh(); },
    onError: () => toast.error('Could not remove the mailbox'),
  });

  const pauseMutation = useMutation({
    mutationFn: mailboxService.pauseOutbound,
    onSuccess: (status) => { toast.success(status.summary); refresh(); },
    onError: () => toast.error('Could not pause outbound email'),
  });

  const openCreate = () => {
    setEditing(null); setForm(EMPTY); setProbe(null); setShowPassword(false); setDialogOpen(true);
  };

  const openEdit = (mailbox: Mailbox) => {
    setEditing(mailbox);
    setForm({
      configurationName: mailbox.configurationName, emailAddress: mailbox.emailAddress,
      protocol: mailbox.protocol, host: mailbox.host, port: mailbox.port,
      username: mailbox.username, password: '', useSsl: mailbox.useSsl,
      pollingInterval: mailbox.pollingInterval, isActive: mailbox.isActive,
      verifyBeforeSave: true,
    });
    setProbe(null); setShowPassword(false); setDialogOpen(true);
  };

  const applyPreset = (key: string) => {
    const preset = presets.find((p) => p.key === key);
    if (!preset) return;
    const imap = form.protocol === 'IMAP';
    setForm({
      ...form,
      host: imap ? preset.imapHost : preset.smtpHost,
      port: imap ? preset.imapPort : preset.smtpPort,
      useSsl: preset.useSsl,
    });
    if (preset.guidance) toast(preset.guidance, { duration: 9000, icon: 'ℹ️' });
  };

  const setProtocol = (protocol: MailboxProtocol) => {
    // Ports differ per protocol; carrying 993 into an SMTP row is the single most common
    // way a mailbox ends up saved with settings that cannot work.
    setForm({ ...form, protocol, port: protocol === 'IMAP' ? 993 : 587 });
    setProbe(null);
  };

  const inboxes = mailboxes.filter((m) => m.protocol === 'IMAP');
  const senders = mailboxes.filter((m) => m.protocol === 'SMTP');
  const canSave = !!form.configurationName && !!form.host && !!form.username
    && (!!editing || !!form.password);

  const renderCard = (mailbox: Mailbox) => (
    <Grid size={{ xs: 12, md: 6 }} key={mailbox.id}>
      <Paper sx={{ p: 2.5, borderRadius: 3, border: '1px solid', borderColor: 'divider', height: '100%' }}>
        <Stack direction="row" spacing={1} sx={{ alignItems: 'center', mb: 1 }}>
          <Typography sx={{ fontWeight: 800, flex: 1, minWidth: 0 }} noWrap>
            {mailbox.configurationName}
          </Typography>
          <Chip
            size="small"
            label={mailbox.healthState}
            color={HEALTH_COLOUR[mailbox.healthState] ?? 'default'}
            sx={{ fontWeight: 700 }}
          />
          {canEdit && (
            <Tooltip title="Edit">
              <IconButton size="small" onClick={() => openEdit(mailbox)}><EditIcon fontSize="small" /></IconButton>
            </Tooltip>
          )}
          {canDelete && (
            <Tooltip title="Remove">
              <IconButton
                size="small"
                onClick={() => deleteMutation.mutate(mailbox.id)}
                disabled={deleteMutation.isPending}
              >
                <DeleteIcon fontSize="small" />
              </IconButton>
            </Tooltip>
          )}
        </Stack>

        <Typography variant="body2" color="text.secondary">{mailbox.emailAddress}</Typography>
        <Typography variant="caption" color="text.secondary" sx={{ display: 'block', mt: 0.25 }}>
          {mailbox.host}:{mailbox.port} · {mailbox.useSsl ? 'secure connection' : 'no implicit TLS'}
          {mailbox.protocol === 'IMAP' && ` · every ${mailbox.pollingInterval} min`}
        </Typography>

        <Alert
          severity={mailbox.healthState === 'Failing' ? 'error'
            : mailbox.healthState === 'Never polled' ? 'warning' : 'info'}
          sx={{ mt: 1.5, py: 0.25, borderRadius: 2 }}
        >
          <Typography variant="body2">{mailbox.healthDetail}</Typography>
        </Alert>

        {mailbox.credentialsSentInClear && (
          <Alert severity="warning" icon={<ShieldIcon />} sx={{ mt: 1, py: 0.25, borderRadius: 2 }}>
            <Typography variant="body2">
              The password for this mailbox is sent unencrypted.
            </Typography>
          </Alert>
        )}
      </Paper>
    </Grid>
  );

  if (isLoading) {
    return <Box sx={{ display: 'flex', justifyContent: 'center', alignItems: 'center', height: '60vh' }}><CircularProgress /></Box>;
  }

  return (
    <Box sx={{ p: 3, maxWidth: 1200, mx: 'auto' }}>
      <Stack direction="row" spacing={1.5} sx={{ alignItems: 'center', mb: 0.5 }}>
        <InboxIcon color="primary" />
        <Typography variant="h4" sx={{ fontWeight: 900, letterSpacing: '-0.02em', flex: 1 }}>
          Email Inboxes
        </Typography>
        {canCreate && (
          <Button
            variant="contained" startIcon={<AddIcon />} onClick={openCreate}
            sx={{ fontWeight: 800, borderRadius: 2, px: 2.5 }}
          >
            Add mailbox
          </Button>
        )}
      </Stack>
      <Typography variant="body2" color="text.secondary" sx={{ mb: 3 }}>
        Connect the inbox your customers send enquiries to, and the account quotes are sent from.
        Nexora reads new mail on a schedule and turns each enquiry into a lead.
      </Typography>

      {/* Containment status. This is the first thing on the page on purpose: before pointing
          the poller at a mailbox of live customer correspondence, an operator needs outbound
          mail provably off, and an active SMTP row is the only thing that decides it. */}
      {outbound && (
        <Alert
          severity={outbound.canSendToCustomers ? 'warning' : 'success'}
          icon={<ShieldIcon />}
          sx={{ mb: 3, borderRadius: 2 }}
          action={outbound.canSendToCustomers && canEdit ? (
            <Button
              size="small" color="inherit" startIcon={<PauseIcon />}
              disabled={pauseMutation.isPending}
              onClick={() => pauseMutation.mutate()}
              sx={{ fontWeight: 800 }}
            >
              Pause outbound email
            </Button>
          ) : undefined}
        >
          <AlertTitle sx={{ fontWeight: 800 }}>
            {outbound.canSendToCustomers
              ? 'Outbound email is live'
              : 'Outbound email is contained'}
          </AlertTitle>
          {outbound.summary}
          {outbound.hasAmbiguousOutbound && (
            <Typography variant="body2" sx={{ mt: 0.5, fontWeight: 700 }}>
              Only the oldest active SMTP mailbox is actually used. Deactivate the others so the
              screen matches what happens.
            </Typography>
          )}
        </Alert>
      )}

      <Typography sx={{ fontWeight: 900, fontSize: '0.85rem', textTransform: 'uppercase', letterSpacing: '0.03em', color: 'text.secondary', mb: 1.5 }}>
        Incoming — where leads come from
      </Typography>
      {inboxes.length === 0 ? (
        <Paper variant="outlined" sx={{ p: 3, borderRadius: 3, mb: 3, textAlign: 'center' }}>
          <Typography color="text.secondary">
            No inbox is connected yet, so no leads can arrive by email. Add an IMAP mailbox to start.
          </Typography>
        </Paper>
      ) : (
        <Grid container spacing={2} sx={{ mb: 3 }}>{inboxes.map(renderCard)}</Grid>
      )}

      <Typography sx={{ fontWeight: 900, fontSize: '0.85rem', textTransform: 'uppercase', letterSpacing: '0.03em', color: 'text.secondary', mb: 1.5 }}>
        Outgoing — where quotes are sent from
      </Typography>
      {senders.length === 0 ? (
        <Paper variant="outlined" sx={{ p: 3, borderRadius: 3, textAlign: 'center' }}>
          <Typography color="text.secondary">
            No sending account is configured. Quotes and supplier emails cannot leave the system.
          </Typography>
        </Paper>
      ) : (
        <Grid container spacing={2}>{senders.map(renderCard)}</Grid>
      )}

      {/* ---- add / edit ---- */}
      <Dialog open={dialogOpen} onClose={() => setDialogOpen(false)} maxWidth="md" fullWidth>
        <DialogTitle sx={{ fontWeight: 900 }}>
          {editing ? 'Edit mailbox' : 'Add mailbox'}
        </DialogTitle>
        {(saveMutation.isPending || testMutation.isPending) && <LinearProgress />}
        <DialogContent dividers>
          <Stack spacing={2.5} sx={{ pt: 1 }}>
            <ToggleButtonGroup
              exclusive fullWidth size="small" value={form.protocol}
              onChange={(_, value) => value && setProtocol(value)}
              disabled={!!editing}
            >
              <ToggleButton value="IMAP" sx={{ fontWeight: 800, gap: 1 }}>
                <InboxIcon fontSize="small" /> Receive leads (IMAP)
              </ToggleButton>
              <ToggleButton value="SMTP" sx={{ fontWeight: 800, gap: 1 }}>
                <SmtpIcon fontSize="small" /> Send quotes (SMTP)
              </ToggleButton>
            </ToggleButtonGroup>

            <TextField
              select fullWidth size="small" label="Mail provider"
              value="" onChange={(e) => applyPreset(e.target.value)}
              helperText="Choose a provider to fill in its documented server settings."
            >
              {presets.map((preset) => (
                <MenuItem key={preset.key} value={preset.key}>{preset.displayName}</MenuItem>
              ))}
            </TextField>

            <Grid container spacing={2}>
              <Grid size={{ xs: 12, md: 6 }}>
                <TextField
                  fullWidth size="small" label="Name this mailbox" value={form.configurationName}
                  onChange={(e) => setForm({ ...form, configurationName: e.target.value })}
                  helperText="For your team, e.g. “Sales enquiries”."
                />
              </Grid>
              <Grid size={{ xs: 12, md: 6 }}>
                <TextField
                  fullWidth size="small" label="Email address" value={form.emailAddress}
                  onChange={(e) => setForm({ ...form, emailAddress: e.target.value })}
                  helperText={form.protocol === 'IMAP'
                    ? 'The address customers send enquiries to.'
                    : 'The address quotes are sent from.'}
                />
              </Grid>
              <Grid size={{ xs: 12, md: 8 }}>
                <TextField
                  fullWidth size="small" label="Mail server" value={form.host}
                  onChange={(e) => { setForm({ ...form, host: e.target.value }); setProbe(null); }}
                  helperText="e.g. outlook.office365.com"
                />
              </Grid>
              <Grid size={{ xs: 12, md: 4 }}>
                <TextField
                  fullWidth size="small" type="number" label="Port" value={form.port}
                  onChange={(e) => { setForm({ ...form, port: Number(e.target.value) }); setProbe(null); }}
                />
              </Grid>
              <Grid size={{ xs: 12, md: 6 }}>
                <TextField
                  fullWidth size="small" label="Username" value={form.username}
                  onChange={(e) => setForm({ ...form, username: e.target.value })}
                  helperText="Usually the full email address."
                />
              </Grid>
              <Grid size={{ xs: 12, md: 6 }}>
                <TextField
                  fullWidth size="small" label="Password" value={form.password}
                  type={showPassword ? 'text' : 'password'}
                  onChange={(e) => setForm({ ...form, password: e.target.value })}
                  // The stored password is never sent to the browser, so editing genuinely
                  // cannot prefill it. Saying so prevents "the field is empty, is it broken?"
                  helperText={editing
                    ? 'Leave blank to keep the current password.'
                    : 'Most providers require an app password rather than the account password.'}
                  slotProps={{
                    input: {
                      endAdornment: (
                        <InputAdornment position="end">
                          <IconButton size="small" onClick={() => setShowPassword((v) => !v)}>
                            {showPassword ? <VisibilityOff fontSize="small" /> : <Visibility fontSize="small" />}
                          </IconButton>
                        </InputAdornment>
                      ),
                    },
                  }}
                />
              </Grid>
              {form.protocol === 'IMAP' && (
                <Grid size={{ xs: 12, md: 6 }}>
                  <TextField
                    fullWidth size="small" type="number" label="Check for new mail every"
                    value={form.pollingInterval}
                    onChange={(e) => setForm({ ...form, pollingInterval: Number(e.target.value) })}
                    slotProps={{
                      input: { endAdornment: <InputAdornment position="end">minutes</InputAdornment> },
                      htmlInput: { min: 1, max: 1440 },
                    }}
                  />
                </Grid>
              )}
            </Grid>

            <Divider />

            <Stack direction={{ xs: 'column', sm: 'row' }} spacing={2}>
              <FormControlLabel
                control={<Switch checked={form.useSsl} onChange={(e) => { setForm({ ...form, useSsl: e.target.checked }); setProbe(null); }} />}
                label="Use a secure connection"
              />
              <FormControlLabel
                control={<Switch checked={form.isActive} onChange={(e) => setForm({ ...form, isActive: e.target.checked })} />}
                label="Active"
              />
              <FormControlLabel
                control={<Switch checked={form.verifyBeforeSave} onChange={(e) => setForm({ ...form, verifyBeforeSave: e.target.checked })} />}
                label="Test before saving"
              />
            </Stack>

            {!form.verifyBeforeSave && (
              <Alert severity="warning" sx={{ borderRadius: 2 }}>
                A mailbox saved without testing can look configured while silently receiving
                nothing. The problem usually surfaces days later as missing leads.
              </Alert>
            )}

            <Collapse in={!!probe} unmountOnExit>
              {probe && <ProbeReport result={probe} />}
            </Collapse>
          </Stack>
        </DialogContent>
        <DialogActions sx={{ px: 3, py: 2 }}>
          <Button
            startIcon={<TestIcon />}
            onClick={() => testMutation.mutate()}
            disabled={testMutation.isPending || !form.host || !form.username || (!editing && !form.password)}
            sx={{ fontWeight: 800, mr: 'auto' }}
          >
            Test connection
          </Button>
          <Button onClick={() => setDialogOpen(false)}>Cancel</Button>
          <Button
            variant="contained" onClick={() => saveMutation.mutate()}
            disabled={saveMutation.isPending || !canSave}
            sx={{ fontWeight: 800, borderRadius: 2, px: 3 }}
          >
            {editing ? 'Save changes' : 'Add mailbox'}
          </Button>
        </DialogActions>
      </Dialog>
    </Box>
  );
};

export default MailboxPage;
