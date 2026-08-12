import { useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import {
  Alert,
  AlertTitle,
  Box,
  Button,
  Grid,
  LinearProgress,
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableRow,
  TextField,
  Typography,
} from '@mui/material';
import {
  DeleteForeverOutlined as PurgeIcon,
  DownloadOutlined as ExportIcon,
  EventBusyOutlined as ScheduleIcon,
  Inventory2Outlined as ArchiveIcon,
  PauseCircleOutlined as SuspendIcon,
  PersonOffOutlined as EraseIcon,
  PlayCircleOutlined as ResumeIcon,
  RestorePageOutlined as RestoreIcon,
  UndoOutlined as CancelDeletionIcon,
  GavelOutlined as HoldIcon,
} from '@mui/icons-material';
import { useSnackbar } from 'notistack';
import Stack from '../../components/Flex';
import PageSection from '../../components/PageSection';
import ReasonDialog from '../../components/ReasonDialog';
import DestructiveConfirmDialog from '../../components/DestructiveConfirmDialog';
import RoleGate from '../../components/RoleGate';
import { EmptyState, ErrorState, LoadingState } from '../../components/States';
import { SoftChip } from '../../components/StatusChip';
import { fmtDateTime, fmtNumber } from '../../components/format';
import { platformApi } from '../../api/client';
import { platformErrorMessage } from '../../api/apiError';
import { platformKeys } from '../../api/queryKeys';
import { usePlatformPermissions } from '../../auth/usePlatformPermissions';
import { useStepUpReauthentication, isStepUpCancelled } from '../../auth/useStepUpReauthentication';
import { REQUIRED_ROLE_COPY } from '../../auth/permissions';
import type { Tenant, TenantOffboardingStatus, TenantPurgeResult } from '../../types';

/**
 * The platform's own floor, mirrored so the operator is told before the request rather
 * than after. A deployment may configure a LONGER minimum, which is why a value the
 * server refuses is reported verbatim instead of being clamped here.
 */
const MIN_RETENTION_DAYS = 7;
const MAX_RETENTION_DAYS = 3650;

type Reversible = 'suspend' | 'resume' | 'archive' | 'restore';

const REVERSIBLE_COPY: Record<Reversible, { title: string; verb: string; body: string }> = {
  suspend: {
    title: 'Suspend tenant',
    verb: 'Suspend',
    body: 'Its users lose access immediately and keep it until the tenant is resumed. Nothing is deleted.',
  },
  resume: {
    title: 'Resume tenant',
    verb: 'Resume',
    body: 'Returns the tenant to Active and restores access for all of its users. Any unclaimed scheduled deletion is canceled atomically; resume is refused while a purge is active.',
  },
  archive: {
    title: 'Archive tenant',
    verb: 'Archive',
    body: 'The "this customer has left" decision. Access stays denied, nothing is destroyed, and it is the only status from which a deletion can be scheduled.',
  },
  restore: {
    title: 'Restore tenant',
    verb: 'Restore',
    body: 'Brings the tenant back from Archived to Suspended and atomically cancels any unclaimed scheduled deletion. Restore is refused while a purge is active. Resume separately to give access back.',
  },
};

const STAGE_TONE = {
  NotScheduled: 'neutral',
  PendingDeletion: 'warning',
  Purged: 'error',
} as const;

export default function LifecycleTab({ tenant }: { tenant: Tenant }) {
  const queryClient = useQueryClient();
  const { enqueueSnackbar } = useSnackbar();
  const permissions = usePlatformPermissions();
  // Export, purge, erasure and legal-hold release all carry [PlatformHighRiskOperation] on the
  // server. With MFA enforcement relaxed each answers 403 "re-enter your password first" — an
  // instruction that named an endpoint no screen called, so all four were dead. `guard` proves
  // the password and re-runs the same request; on an MFA-bound session it does nothing at all.
  const stepUp = useStepUpReauthentication();

  const [reversible, setReversible] = useState<Reversible | null>(null);
  const [exportOpen, setExportOpen] = useState(false);
  const [scheduleOpen, setScheduleOpen] = useState(false);
  const [retentionDays, setRetentionDays] = useState('30');
  const [cancelOpen, setCancelOpen] = useState(false);
  const [purgeOpen, setPurgeOpen] = useState(false);
  const [eraseOpen, setEraseOpen] = useState(false);
  const [exportNotice, setExportNotice] = useState<string | null>(null);
  const [purgeReceipt, setPurgeReceipt] = useState<TenantPurgeResult | null>(null);
  const [holdOpen, setHoldOpen] = useState(false);
  const [releaseHoldId, setReleaseHoldId] = useState<string | null>(null);
  const [holdScope, setHoldScope] = useState('AllTenantData');
  const [holdAuthority, setHoldAuthority] = useState('Legal counsel instruction');
  const [holdEvidence, setHoldEvidence] = useState('');

  const offboarding = useQuery({
    queryKey: platformKeys.offboarding(tenant.id),
    queryFn: () => platformApi.getOffboarding(tenant.id),
  });
  const status: TenantOffboardingStatus | undefined = offboarding.data;
  const legalHolds = useQuery({
    queryKey: platformKeys.legalHolds(tenant.id),
    queryFn: () => platformApi.listTenantLegalHolds(tenant.id),
    enabled: permissions.isOwner,
  });
  const activeHolds = (legalHolds.data ?? []).filter((hold) => hold.isActive);
  const destructiveHoldGuard = legalHolds.isLoading || legalHolds.isError || activeHolds.length > 0;

  // The preview is Owner-gated server-side, so it is not even requested for anyone else —
  // a query that is certain to 403 is a red line in the network tab and nothing more.
  const preview = useQuery({
    queryKey: platformKeys.purgePreview(tenant.id),
    queryFn: () => platformApi.getPurgePreview(tenant.id),
    enabled: permissions.isOwner && Boolean(status?.canPurge),
  });

  const invalidate = () => {
    queryClient.invalidateQueries({ queryKey: platformKeys.offboarding(tenant.id) });
    queryClient.invalidateQueries({ queryKey: platformKeys.tenant(tenant.id) });
    queryClient.invalidateQueries({ queryKey: platformKeys.tenantOperations(tenant.id) });
    queryClient.invalidateQueries({ queryKey: platformKeys.tenants() });
    queryClient.invalidateQueries({ queryKey: platformKeys.legalHolds(tenant.id) });
  };

  const fail = (fallback: string) => (error: unknown) => {
    // Dismissing the step-up dialog is a decision not to proceed, not a failure. Reporting it
    // as "The purge was refused" would say the server rejected something it never received.
    if (isStepUpCancelled(error)) return;
    enqueueSnackbar(platformErrorMessage(error, fallback), { variant: 'error' });
  };

  const reversibleMutation = useMutation({
    mutationFn: ({ kind, reason }: { kind: Reversible; reason: string }) => {
      if (kind === 'suspend') return platformApi.suspendTenant(tenant.id, reason);
      if (kind === 'resume') return platformApi.resumeTenant(tenant.id, reason);
      if (kind === 'archive') return platformApi.archiveTenant(tenant.id, reason);
      return platformApi.restoreTenant(tenant.id, reason);
    },
    onSuccess: (_updated, { kind }) => {
      const outcome = kind === 'suspend' ? 'suspended'
        : kind === 'resume' ? 'resumed'
          : kind === 'archive' ? 'archived'
            : 'restored';
      enqueueSnackbar(`${tenant.name} ${outcome}`, { variant: 'success' });
      setReversible(null);
      invalidate();
    },
    onError: fail('The lifecycle change was refused'),
  });

  const exportMutation = useMutation({
    mutationFn: (body: { reason: string; confirmation: string }) =>
      stepUp.guard(() => platformApi.exportTenantData(tenant.id, body)),
    onSuccess: (download) => {
      // Saved through a synthetic anchor: the response is a one-shot stream the server
      // deliberately does not store, so there is no URL to send the operator to instead.
      const url = URL.createObjectURL(download.blob);
      const anchor = document.createElement('a');
      anchor.href = url;
      anchor.download = download.filename;
      anchor.click();
      URL.revokeObjectURL(url);
      setExportOpen(false);
      setExportNotice(
        download.sha256
          ? `Export downloaded — ${download.totalRows == null ? 'row count unavailable' : `${fmtNumber(download.totalRows)} rows`}, SHA-256 ${download.sha256}`
          : 'Export downloaded. The receipt hash was not returned by this deployment; it is on the receipt list below.',
      );
      invalidate();
    },
    onError: fail('The export was refused'),
  });

  const scheduleMutation = useMutation({
    mutationFn: (reason: string) =>
      platformApi.scheduleTenantDeletion(tenant.id, {
        reason,
        retentionDays: retentionDays.trim() === '' ? null : Number(retentionDays),
      }),
    onSuccess: () => {
      enqueueSnackbar('Deletion scheduled — the retention clock is running', { variant: 'warning' });
      setScheduleOpen(false);
      invalidate();
    },
    onError: fail('Scheduling the deletion was refused'),
  });

  const cancelMutation = useMutation({
    mutationFn: (reason: string) => platformApi.cancelTenantDeletion(tenant.id, reason),
    onSuccess: () => {
      enqueueSnackbar('Scheduled deletion cancelled', { variant: 'success' });
      setCancelOpen(false);
      invalidate();
    },
    onError: fail('Cancelling the deletion was refused'),
  });

  const purgeMutation = useMutation({
    mutationFn: (body: { reason: string; confirmation: string }) =>
      stepUp.guard(() => platformApi.purgeTenant(tenant.id, body)),
    onSuccess: (result) => {
      enqueueSnackbar(`${fmtNumber(result.rowsDeleted)} rows destroyed across ${result.tablesTouched} tables`, {
        variant: 'warning',
      });
      // The server computes what SURVIVED — lifecycle events, platform audit records, the support
      // threads it redacted rather than deleted — and the console used to throw all of it away and
      // show only the body count. A toast that says what was destroyed and nothing about what was
      // kept is the half of the answer an operator cannot give a customer.
      setPurgeReceipt(result);
      setPurgeOpen(false);
      invalidate();
    },
    onError: fail('The purge was refused'),
  });

  const eraseMutation = useMutation({
    mutationFn: (body: { reason: string; confirmation: string }) =>
      stepUp.guard(() => platformApi.eraseTenantPersonalData(tenant.id, body)),
    onSuccess: (result) => {
      enqueueSnackbar(`${fmtNumber(result.identitiesErased)} identities erased`, { variant: 'warning' });
      setEraseOpen(false);
      invalidate();
    },
    onError: fail('The erasure was refused'),
  });

  const placeHoldMutation = useMutation({
    mutationFn: (reason: string) => platformApi.placeTenantLegalHold(tenant.id, {
      scope: holdScope,
      authority: holdAuthority,
      evidenceReference: holdEvidence,
      reason,
    }),
    onSuccess: () => {
      enqueueSnackbar('Legal hold placed — purge and erasure are blocked', { variant: 'warning' });
      setHoldOpen(false);
      setHoldEvidence('');
      invalidate();
    },
    onError: fail('The legal hold was refused'),
  });

  const releaseHoldMutation = useMutation({
    mutationFn: (reason: string) => stepUp.guard(() => platformApi.releaseTenantLegalHold(
      tenant.id, releaseHoldId as string, reason,
    )),
    onSuccess: () => {
      enqueueSnackbar('Legal hold released', { variant: 'success' });
      setReleaseHoldId(null);
      invalidate();
    },
    onError: fail('The legal-hold release was refused'),
  });

  if (offboarding.isLoading) return <LoadingState label="Reading the offboarding record…" />;
  if (offboarding.isError || !status) {
    return (
      <ErrorState
        message={platformErrorMessage(offboarding.error, 'The offboarding record could not be read.')}
        onRetry={() => offboarding.refetch()}
      />
    );
  }

  const retentionProblem =
    retentionDays.trim() === ''
      ? null
      : !/^\d+$/.test(retentionDays.trim()) ||
          Number(retentionDays) < MIN_RETENTION_DAYS ||
          Number(retentionDays) > MAX_RETENTION_DAYS
        ? `The retention window must be a whole number of days between ${MIN_RETENTION_DAYS} and ${MAX_RETENTION_DAYS}. Seven days is the floor the platform enforces so a deletion scheduled by mistake on a Friday is still noticeable on Monday.`
        : null;

  const clockElapsed =
    status.retentionDays && status.daysUntilPurgeEligible != null
      ? Math.min(
          100,
          Math.max(0, ((status.retentionDays - status.daysUntilPurgeEligible) / status.retentionDays) * 100),
        )
      : status.isPurgeEligible
        ? 100
        : 0;

  return (
    <Stack spacing={2.5}>
      {/*
        Activation used to render here, on the grounds that it is the first lifecycle transition a
        tenant makes. That reasoning was right and the placement was still wrong: this is the tab
        an operator opens to DELETE a customer, and it is ninth of eleven. Somebody with a tenant
        stuck in Provisioning does not go looking for the Activate button underneath Purge and
        Erase. It now has its own tab, second in the row, immediately after Overview — and it is
        NOT rendered here as well, because two Activate buttons on two tabs is two ways to fire the
        same one-way transition and the second one is always the one nobody tested.
      */}

      {status.stage === 'Purged' && (
        <Alert severity="error" sx={{ borderRadius: 2 }}>
          <AlertTitle sx={{ fontWeight: 800 }}>This tenant has been purged</AlertTitle>
          Destroyed {status.purgedOn ? fmtDateTime(status.purgedOn) : 'at an unrecorded time'} by{' '}
          {status.purgedBy ?? 'an unrecorded actor'}
          {status.purgedRowCount == null ? '' : `, ${fmtNumber(status.purgedRowCount)} rows`}. What remains is a
          tombstone: the lifecycle history, the audit trail, the export receipts and the billing statements.
        </Alert>
      )}

      {status.stage === 'PendingDeletion' && (
        <Alert severity="warning" sx={{ borderRadius: 2 }}>
          <AlertTitle sx={{ fontWeight: 800 }}>
            Deletion scheduled{status.purgeEligibleOn ? ` — purge allowed from ${fmtDateTime(status.purgeEligibleOn)}` : ''}
          </AlertTitle>
          <Typography variant="body2" sx={{ mb: 1 }}>
            {status.deletionReason ?? 'No reason recorded.'} — scheduled by {status.deletionScheduledBy ?? 'unknown'}
            {status.deletionScheduledOn ? ` on ${fmtDateTime(status.deletionScheduledOn)}` : ''}.
          </Typography>
          <LinearProgress
            variant="determinate"
            value={clockElapsed}
            color={status.isPurgeEligible ? 'error' : 'warning'}
            aria-label="Retention window elapsed"
            sx={{ height: 8, borderRadius: 4 }}
          />
          <Typography variant="caption" sx={{ fontWeight: 700, mt: 0.5, display: 'block' }}>
            {status.isPurgeEligible
              ? 'The retention window has elapsed. A purge is now permitted.'
              : `${status.daysUntilPurgeEligible ?? '—'} day(s) of the ${status.retentionDays ?? '—'}-day window remaining. The purge is refused until it elapses.`}
          </Typography>
        </Alert>
      )}

      {status.personalDataErasedOn && (
        <Alert severity="info" sx={{ borderRadius: 2 }}>
          Personal data was erased {fmtDateTime(status.personalDataErasedOn)} by{' '}
          {status.personalDataErasedBy ?? 'an unrecorded actor'}
          {status.erasedIdentityCount == null ? '' : ` — ${fmtNumber(status.erasedIdentityCount)} identities`}. The
          commercial records were deliberately kept.
        </Alert>
      )}

      {activeHolds.length > 0 && (
        <Alert severity="error" icon={<HoldIcon />} sx={{ borderRadius: 2 }}>
          <AlertTitle sx={{ fontWeight: 800 }}>
            Legal hold active — purge and personal-data erasure are blocked
          </AlertTitle>
          {activeHolds.map((hold) => (
            <Box key={hold.id} sx={{ mt: 1 }}>
              <Typography variant="body2" sx={{ fontWeight: 700 }}>
                {hold.scope}: {hold.authority}
              </Typography>
              <Typography variant="caption">
                {hold.reason} Evidence: {hold.evidenceReference}. Placed {fmtDateTime(hold.placedOn)} by {hold.placedBy}.
              </Typography>
              <Button size="small" color="inherit" onClick={() => setReleaseHoldId(hold.id)} sx={{ ml: 1 }}>
                Release hold
              </Button>
            </Box>
          ))}
        </Alert>
      )}

      {permissions.isOwner && legalHolds.isError && (
        <Alert severity="error" sx={{ borderRadius: 2 }}>
          <AlertTitle sx={{ fontWeight: 800 }}>Legal-hold status could not be verified</AlertTitle>
          Purge and personal-data erasure remain unavailable until the legal-hold record can be read.
          <Button size="small" color="inherit" onClick={() => legalHolds.refetch()} sx={{ ml: 1 }}>
            Retry
          </Button>
        </Alert>
      )}

      {permissions.isOwner && (
        <Box>
          <Button variant="outlined" color="warning" startIcon={<HoldIcon />} onClick={() => setHoldOpen(true)}>
            Place legal hold
          </Button>
        </Box>
      )}

      <Grid container spacing={2.5}>
        <Grid size={{ xs: 12, md: 5 }}>
          <PageSection title="Where this tenant stands">
            <Stack spacing={1.5}>
              <Stack direction="row" alignItems="center" justifyContent="space-between">
                <Typography variant="body2" color="text.secondary">
                  Service status
                </Typography>
                <SoftChip label={status.tenantStatus} tone={status.tenantStatus === 'Active' ? 'success' : 'warning'} />
              </Stack>
              <Stack direction="row" alignItems="center" justifyContent="space-between">
                <Typography variant="body2" color="text.secondary">
                  Destruction stage
                </Typography>
                <SoftChip label={status.stage} tone={STAGE_TONE[status.stage]} dot={false} />
              </Stack>
              <Typography variant="caption" color="text.secondary">
                {/* The two axes are independent by design; showing only one is how a
                    tenant looks "just archived" while its retention clock is running. */}
                These are two separate axes. A tenant stays Archived for the whole of offboarding while the stage
                records how far the destruction has got.
              </Typography>
            </Stack>
          </PageSection>

          <PageSection title="Reversible lifecycle" sx={{ mt: 2.5 }}>
            <Stack spacing={1.5}>
              {tenant.status === 'active' && (
                <RoleGate allowed={permissions.canAdministerTenants} requirement={REQUIRED_ROLE_COPY.tenantAdmin}>
                  {(disabled) => (
                    <Button
                      variant="outlined"
                      color="warning"
                      startIcon={<SuspendIcon />}
                      disabled={disabled}
                      onClick={() => setReversible('suspend')}
                    >
                      Suspend tenant
                    </Button>
                  )}
                </RoleGate>
              )}
              {tenant.status === 'suspended' && (
                <>
                  <RoleGate allowed={permissions.canAdministerTenants} requirement={REQUIRED_ROLE_COPY.tenantAdmin}>
                    {(disabled) => (
                      <Button
                        variant="outlined"
                        color="success"
                        startIcon={<ResumeIcon />}
                        disabled={disabled}
                        onClick={() => setReversible('resume')}
                      >
                        Resume tenant
                      </Button>
                    )}
                  </RoleGate>
                  <RoleGate allowed={permissions.canAdministerTenants} requirement={REQUIRED_ROLE_COPY.tenantAdmin}>
                    {(disabled) => (
                      <Button
                        variant="outlined"
                        color="error"
                        startIcon={<ArchiveIcon />}
                        disabled={disabled}
                        onClick={() => setReversible('archive')}
                      >
                        Archive tenant
                      </Button>
                    )}
                  </RoleGate>
                </>
              )}
              {tenant.status === 'archived' && (
                <RoleGate allowed={permissions.canAdministerTenants} requirement={REQUIRED_ROLE_COPY.tenantAdmin}>
                  {(disabled) => (
                    <Button
                      variant="outlined"
                      color="success"
                      startIcon={<RestoreIcon />}
                      disabled={disabled}
                      onClick={() => setReversible('restore')}
                    >
                      Restore to suspended
                    </Button>
                  )}
                </RoleGate>
              )}
            </Stack>
          </PageSection>
        </Grid>

        <Grid size={{ xs: 12, md: 7 }}>
          <PageSection
            title="Offboarding"
            subtitle="Hand the data back, start the clock, then destroy. Each button is enabled by the server, not by this screen."
          >
            <Stack spacing={1.5}>
              <RoleGate allowed={permissions.isOwner} requirement={REQUIRED_ROLE_COPY.owner}>
                {(disabled) => (
                  <Button
                    variant="outlined"
                    startIcon={<ExportIcon />}
                    disabled={disabled}
                    onClick={() => setExportOpen(true)}
                  >
                    Export this tenant's data
                  </Button>
                )}
              </RoleGate>

              <RoleGate allowed={permissions.isOwner} requirement={REQUIRED_ROLE_COPY.owner}>
                {(disabled) => (
                  <Button
                    variant="outlined"
                    color="warning"
                    startIcon={<ScheduleIcon />}
                    // canScheduleDeletion is resolved server-side from BOTH axes — the
                    // tenant must be Archived and the stage must be NotScheduled.
                    disabled={disabled || !status.canScheduleDeletion}
                    onClick={() => setScheduleOpen(true)}
                  >
                    Schedule deletion
                  </Button>
                )}
              </RoleGate>

              <RoleGate allowed={permissions.isOwner} requirement={REQUIRED_ROLE_COPY.owner}>
                {(disabled) => (
                  <Button
                    variant="outlined"
                    color="success"
                    startIcon={<CancelDeletionIcon />}
                    disabled={disabled || !status.canCancelDeletion}
                    onClick={() => setCancelOpen(true)}
                  >
                    Cancel the scheduled deletion
                  </Button>
                )}
              </RoleGate>

              <RoleGate allowed={permissions.isOwner} requirement={REQUIRED_ROLE_COPY.owner}>
                {(disabled) => (
                  <Button
                    variant="contained"
                    color="error"
                    startIcon={<PurgeIcon />}
                    disabled={
                      disabled || destructiveHoldGuard || !status.canPurge
                      || status.purgeRequiresDifferentApprover
                    }
                    onClick={() => setPurgeOpen(true)}
                  >
                    Purge tenant records
                  </Button>
                )}
              </RoleGate>

              <RoleGate allowed={permissions.isOwner} requirement={REQUIRED_ROLE_COPY.owner}>
                {(disabled) => (
                  <Button
                    variant="outlined"
                    color="error"
                    startIcon={<EraseIcon />}
                    disabled={disabled || destructiveHoldGuard || !status.canErasePersonalData}
                    onClick={() => setEraseOpen(true)}
                  >
                    Erase personal data
                  </Button>
                )}
              </RoleGate>

              {permissions.isOwner && status.purgeRequiresDifferentApprover && (
                <Alert severity="warning" sx={{ borderRadius: 2 }}>
                  <AlertTitle sx={{ fontWeight: 800 }}>A second Owner has to run this purge</AlertTitle>
                  You scheduled this deletion
                  {status.deletionApprovedBy ? ` (${status.deletionApprovedBy})` : ''}, so the server
                  will refuse a purge from you. Destroying a customer&apos;s records is the one act
                  here that no later reviewer can correct, and every other control on the path —
                  the role, the typed name, the reason, the clock — is one person satisfying a rule.
                  The platform already requires an independent checker to finalize an invoice, to
                  approve an exchange rate and to release a legal hold; this is the same rule.
                  Ask another Owner to carry it out.
                </Alert>
              )}

              {permissions.isOwner && status.canPurge && !status.purgeRequiresDifferentApprover
                && status.deletionApprovedBy && (
                <Alert severity="info" sx={{ borderRadius: 2 }}>
                  Scheduled by <strong>{status.deletionApprovedBy}</strong>. You are the second
                  approver: carrying this out is your independent decision, and it is recorded as
                  one.
                </Alert>
              )}

              {!permissions.isOwner && (
                <Alert severity="info" sx={{ borderRadius: 2 }}>
                  The destructive verbs are Owner-only by design: a support engineer must be able to take a
                  customer off the product and must not be able to destroy them.
                </Alert>
              )}
            </Stack>

            {/* The retained half of the answer. The server counts what survived; before this it
                was returned on every purge response and rendered nowhere. */}
            {purgeReceipt && (
              <Alert
                severity="warning"
                sx={{ borderRadius: 2, mt: 2 }}
                onClose={() => setPurgeReceipt(null)}
              >
                <AlertTitle sx={{ fontWeight: 800 }}>Purge complete — what was destroyed and what was kept</AlertTitle>
                <Typography variant="body2" sx={{ mb: 1 }}>{purgeReceipt.summary}</Typography>
                <Stack spacing={0.5}>
                  <Typography variant="body2">
                    <strong>Destroyed:</strong> {fmtNumber(purgeReceipt.rowsDeleted)} rows across{' '}
                    {purgeReceipt.tablesTouched} tables.
                  </Typography>
                  <Typography variant="body2">
                    <strong>Kept:</strong> {fmtNumber(purgeReceipt.lifecycleEventsRetained)} lifecycle
                    events and {fmtNumber(purgeReceipt.platformAuditRecordsRetained)} platform audit
                    records — the account of how this customer was treated, which is the operator&apos;s
                    record and not theirs.
                  </Typography>
                  <Typography variant="body2">
                    <strong>Redacted rather than deleted:</strong>{' '}
                    {fmtNumber(purgeReceipt.supportTicketsRedacted)} support tickets, with{' '}
                    {fmtNumber(purgeReceipt.supportNotesErased)} notes erased — the tickets survive as
                    our record of what was promised; the customer&apos;s words inside them do not.
                  </Typography>
                </Stack>
              </Alert>
            )}

            {status.disclosures.length > 0 && (
              <Box sx={{ mt: 2, p: 1.5, borderRadius: 2, bgcolor: 'action.hover' }}>
                <Stack spacing={1}>
                  {status.disclosures.map((line) => (
                    <Typography key={line} variant="caption" color="text.secondary">
                      {line}
                    </Typography>
                  ))}
                </Stack>
              </Box>
            )}

            {/* The download result is announced as well as shown: the hash is the only
                proof of what the customer received, and it must not scroll past. */}
            <Box role="status" aria-live="polite" sx={{ mt: 1.5 }}>
              {exportNotice && (
                <Alert severity="success" sx={{ borderRadius: 2 }} onClose={() => setExportNotice(null)}>
                  <Typography variant="caption" sx={{ fontWeight: 700, wordBreak: 'break-all' }}>
                    {exportNotice}
                  </Typography>
                </Alert>
              )}
            </Box>
          </PageSection>
        </Grid>
      </Grid>

      <PageSection
        title="Export receipts"
        subtitle="The export itself is never stored. These receipts and their content hashes are what prove what was handed back."
      >
        {status.exports.length === 0 ? (
          <EmptyState title="No exports yet" message="Nothing has been handed back to this customer." minHeight={140} />
        ) : (
          <Box sx={{ overflowX: 'auto' }}>
            <Table size="small">
              <TableHead>
                <TableRow>
                  <TableCell sx={{ fontWeight: 700 }}>Completed</TableCell>
                  <TableCell sx={{ fontWeight: 700 }}>Requested by</TableCell>
                  <TableCell sx={{ fontWeight: 700 }} align="right">Rows</TableCell>
                  <TableCell sx={{ fontWeight: 700 }} align="right">Size</TableCell>
                  <TableCell sx={{ fontWeight: 700 }}>SHA-256</TableCell>
                </TableRow>
              </TableHead>
              <TableBody>
                {status.exports.map((receipt) => (
                  <TableRow key={receipt.id} hover>
                    <TableCell>{fmtDateTime(receipt.completedOn)}</TableCell>
                    <TableCell>{receipt.requestedBy}</TableCell>
                    <TableCell align="right">{fmtNumber(receipt.totalRows)}</TableCell>
                    <TableCell align="right">{fmtNumber(Math.round(receipt.sizeBytes / 1024))} KB</TableCell>
                    <TableCell>
                      <Typography variant="caption" sx={{ fontFamily: 'monospace', wordBreak: 'break-all' }}>
                        {receipt.contentSha256}
                      </Typography>
                    </TableCell>
                  </TableRow>
                ))}
              </TableBody>
            </Table>
          </Box>
        )}
      </PageSection>

      <PageSection title="Lifecycle history" subtitle="Every transition, who decided it, and why.">
        {status.history.length === 0 ? (
          <EmptyState title="No transitions recorded" minHeight={140} />
        ) : (
          <Box sx={{ overflowX: 'auto' }}>
            <Table size="small">
              <TableHead>
                <TableRow>
                  <TableCell sx={{ fontWeight: 700 }}>When</TableCell>
                  <TableCell sx={{ fontWeight: 700 }}>Action</TableCell>
                  <TableCell sx={{ fontWeight: 700 }}>Stage</TableCell>
                  <TableCell sx={{ fontWeight: 700 }}>Actor</TableCell>
                  <TableCell sx={{ fontWeight: 700 }}>Reason</TableCell>
                </TableRow>
              </TableHead>
              <TableBody>
                {status.history.map((event) => (
                  <TableRow key={event.id} hover>
                    <TableCell>{fmtDateTime(event.occurredOn)}</TableCell>
                    <TableCell>
                      <Box component="code" sx={{ fontSize: 12.5, fontWeight: 700 }}>
                        {event.action}
                      </Box>
                    </TableCell>
                    <TableCell>
                      <Typography variant="caption" color="text.secondary">
                        {event.fromStage ?? '—'} → {event.toStage ?? '—'}
                      </Typography>
                    </TableCell>
                    <TableCell>{event.actorEmail}</TableCell>
                    <TableCell sx={{ maxWidth: 320 }}>
                      <Typography variant="caption">{event.reason}</Typography>
                    </TableCell>
                  </TableRow>
                ))}
              </TableBody>
            </Table>
          </Box>
        )}
      </PageSection>

      {/* --- dialogs --- */}

      <ReasonDialog
        open={holdOpen}
        title="Place legal hold"
        confirmLabel="Place hold"
        confirmColor="warning"
        minReasonLength={15}
        reasonLabel="Preservation reason"
        description="Creates an immutable preservation order. Purge and personal-data erasure will fail closed until a separately attributable release."
        extra={
          <Stack spacing={2}>
            <TextField label="Scope" value={holdScope} onChange={(event) => setHoldScope(event.target.value)} />
            <TextField label="Authority" value={holdAuthority} onChange={(event) => setHoldAuthority(event.target.value)} />
            <TextField
              label="Evidence reference"
              value={holdEvidence}
              onChange={(event) => setHoldEvidence(event.target.value)}
              helperText="Case, order, contract, or evidence-system reference; do not paste secret contents."
            />
          </Stack>
        }
        extraProblem={
          holdScope.trim().length < 3 || holdAuthority.trim().length < 3 || holdEvidence.trim().length < 3
            ? 'Scope, authority, and evidence reference must each contain at least 3 characters.'
            : null
        }
        busy={placeHoldMutation.isPending}
        onClose={() => setHoldOpen(false)}
        onConfirm={(reason) => placeHoldMutation.mutate(reason)}
      />

      <ReasonDialog
        open={releaseHoldId !== null}
        title="Release legal hold"
        confirmLabel="Release hold"
        confirmColor="warning"
        minReasonLength={15}
        reasonLabel="Why has the preservation duty ended?"
        description="The placement evidence remains immutable. This adds an attributable release and allows governed erasure or purge to proceed again."
        busy={releaseHoldMutation.isPending}
        onClose={() => setReleaseHoldId(null)}
        onConfirm={(reason) => releaseHoldMutation.mutate(reason)}
      />

      <ReasonDialog
        open={reversible !== null}
        title={reversible ? REVERSIBLE_COPY[reversible].title : ''}
        confirmLabel={reversible ? REVERSIBLE_COPY[reversible].verb : ''}
        confirmColor={reversible === 'archive' ? 'error' : reversible === 'suspend' ? 'warning' : 'success'}
        description={
          <>
            <strong>{tenant.name}</strong> — {reversible ? REVERSIBLE_COPY[reversible].body : ''}
          </>
        }
        busy={reversibleMutation.isPending}
        onClose={() => setReversible(null)}
        onConfirm={(reason) => reversible && reversibleMutation.mutate({ kind: reversible, reason })}
      />

      <DestructiveConfirmDialog
        open={exportOpen}
        title="Export this tenant's data"
        confirmationRequired={status.confirmationRequired}
        confirmLabel="Generate export"
        description={
          <>
            Produces a JSON file of {tenant.name}'s records and downloads it here. The file is not stored on the
            platform — only a receipt with its SHA-256 hash, so what was handed back can be proved later.
          </>
        }
        disclosures={[
          'This download contains the tenant’s full commercial history. Handle it as restricted customer data.',
          'The platform retains only a receipt and SHA-256 fingerprint, not a copy of the export.',
        ]}
        busy={exportMutation.isPending}
        onClose={() => setExportOpen(false)}
        onConfirm={(body) => exportMutation.mutate(body)}
      />

      <ReasonDialog
        open={scheduleOpen}
        title="Schedule deletion"
        confirmLabel="Start the retention clock"
        confirmColor="warning"
        minReasonLength={15}
        reasonLabel="Why is this customer being deleted?"
        reasonHelper="At least 15 characters. Read by whoever reviews the purge."
        description={
          <>
            Starts the retention window for <strong>{tenant.name}</strong>. Nothing is destroyed today — this is
            reversible right up until the window elapses and somebody purges, which is the entire point of it.
          </>
        }
        extra={
          <TextField
            fullWidth
            label="Retention window (days)"
            value={retentionDays}
            onChange={(event) => setRetentionDays(event.target.value)}
            error={Boolean(retentionProblem)}
            helperText={
              retentionProblem ??
              `Leave blank for the platform default. Minimum ${MIN_RETENTION_DAYS} days; this deployment may require longer.`
            }
            slotProps={{ htmlInput: { inputMode: 'numeric' } }}
          />
        }
        extraProblem={retentionProblem}
        busy={scheduleMutation.isPending}
        onClose={() => setScheduleOpen(false)}
        onConfirm={(reason) => scheduleMutation.mutate(reason)}
      />

      <ReasonDialog
        open={cancelOpen}
        title="Cancel the scheduled deletion"
        confirmLabel="Cancel deletion"
        confirmColor="success"
        description={
          <>
            Stops the retention clock for <strong>{tenant.name}</strong> and returns it to a plainly archived
            tenant. Nothing has been destroyed, so nothing needs restoring.
          </>
        }
        minReasonLength={1}
        busy={cancelMutation.isPending}
        onClose={() => setCancelOpen(false)}
        onConfirm={(reason) => cancelMutation.mutate(reason)}
      />

      <DestructiveConfirmDialog
        open={purgeOpen}
        title={`Purge ${tenant.name}`}
        confirmationRequired={status.confirmationRequired}
        confirmLabel="Purge everything"
        disclosures={status.disclosures}
        description={
          <>
            This destroys {tenant.name}'s business records permanently. There is no undo, no recycle bin and no
            backup this platform can restore from.
          </>
        }
        blastRadius={
          preview.isLoading ? (
            <LoadingState label="Counting what would be destroyed…" minHeight={120} />
          ) : preview.data ? (
            <Box>
              <Typography variant="subtitle2" sx={{ fontWeight: 800, mb: 0.5 }}>
                {fmtNumber(preview.data.totalRows)} rows across {preview.data.tables.length} tables
              </Typography>
              <Box sx={{ maxHeight: 200, overflowY: 'auto', borderRadius: 2, bgcolor: 'action.hover', p: 1 }}>
                {preview.data.tables.map((table) => (
                  <Stack key={`${table.table}.${table.tenantColumn}`} direction="row" justifyContent="space-between">
                    <Typography variant="caption" sx={{ fontFamily: 'monospace' }}>
                      {table.table}
                    </Typography>
                    <Typography variant="caption" sx={{ fontWeight: 700 }}>
                      {fmtNumber(table.rows)}
                    </Typography>
                  </Stack>
                ))}
              </Box>
              {/* What SURVIVES, with the reason, at the same weight as what dies.
                  This used to be a comma-joined list of table names in caption text, which told an
                  operator that something was kept and nothing about why — and "why" is the half
                  they have to be able to repeat to a customer asking what we still hold on them. */}
              {preview.data.preservedDetail.length > 0 && (
                <Box sx={{ mt: 2 }}>
                  <Typography variant="subtitle2" sx={{ fontWeight: 800, mb: 0.5 }}>
                    Kept, and why — {preview.data.preservedDetail.length} tables
                  </Typography>
                  <Box sx={{ maxHeight: 200, overflowY: 'auto', borderRadius: 2, bgcolor: 'action.hover', p: 1 }}>
                    {preview.data.preservedDetail.map((entry) => (
                      <Box key={entry.table} sx={{ mb: 1 }}>
                        <Typography variant="caption" sx={{ fontFamily: 'monospace', fontWeight: 700, display: 'block' }}>
                          {entry.table}
                        </Typography>
                        <Typography variant="caption" color="text.secondary">
                          {entry.reason}
                        </Typography>
                      </Box>
                    ))}
                  </Box>
                </Box>
              )}
            </Box>
          ) : (
            <Alert severity="warning" sx={{ borderRadius: 2 }}>
              The purge preview could not be read, so the blast radius is unknown. Do not proceed on a guess —
              retry the preview first.
            </Alert>
          )
        }
        busy={purgeMutation.isPending}
        onClose={() => setPurgeOpen(false)}
        onConfirm={(body) => purgeMutation.mutate(body)}
      />

      <DestructiveConfirmDialog
        open={eraseOpen}
        title={`Erase personal data for ${tenant.name}`}
        confirmationRequired={status.confirmationRequired}
        confirmLabel="Erase identities"
        disclosures={status.disclosures}
        description={
          <>
            Replaces the identities of the natural persons held for {tenant.name} — its users, its invitation
            recipients and its named contacts. Every user is deactivated and their sign-in address replaced, so
            the next login fails for anyone nobody warned. This is <strong>not</strong> a deletion: the commercial
            records stay, because invoices and orders carry statutory retention a customer cannot waive.
          </>
        }
        busy={eraseMutation.isPending}
        onClose={() => setEraseOpen(false)}
        onConfirm={(body) => eraseMutation.mutate(body)}
      />

      {/* Inert until the server actually demands a step-up. */}
      {stepUp.dialog}
    </Stack>
  );
}
