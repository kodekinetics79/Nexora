import { useMemo, useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import {
  Alert,
  AlertTitle,
  Box,
  Button,
  Chip,
  Divider,
  Grid,
  Paper,
  Stack,
  TextField,
  Tooltip,
  Typography,
} from '@mui/material';
import {
  LockOutlined as PolicyIcon,
  ReportProblemOutlined as ProblemIcon,
  VerifiedUserOutlined as EnforcedIcon,
} from '@mui/icons-material';
import { useSnackbar } from 'notistack';
import { platformApi } from '../api/client';
import { platformErrorMessage, platformErrorStatus } from '../api/apiError';
import { platformKeys } from '../api/queryKeys';
import PageHeader from '../components/PageHeader';
import RoleGate from '../components/RoleGate';
import { usePlatformPermissions } from '../auth/usePlatformPermissions';
import { REQUIRED_ROLE_COPY } from '../auth/permissions';
import { ErrorState, LoadingState } from '../components/States';
import { fmtDateTime } from '../components/format';
import type { PlatformMfaMode } from '../types';

/** What each mode means, in the words an operator needs before choosing it. Kept here rather
 *  than on the server because it is copy, not policy — the server decides which of these are
 *  even offered. */
const MODE_COPY: Record<PlatformMfaMode, string> = {
  REQUIRED:
    'Every operator presents a second factor. The only mode production accepts, and the mode every other one returns to when its window closes.',
  OPTIONAL:
    'Operators who have enrolled are still challenged; operators who have not are not forced to. For a staging cutover window only.',
  DISABLED_TEST_ONLY:
    'No second factor is enforced anywhere, including at sign-in for operators who have enrolled. Enrolled authenticators are kept — nobody has to re-enrol when this ends.',
};

/** Local-datetime-input value → the ISO instant the API wants, or null. */
const toIsoOrNull = (localValue: string): string | null => {
  if (!localValue.trim()) return null;
  const parsed = new Date(localValue);
  return Number.isNaN(parsed.getTime()) ? null : parsed.toISOString();
};

/**
 * Platform Admin → Security → Platform Authentication.
 *
 * <p>Owner-only, and server-authoritative in both directions: which modes this screen can even
 * offer comes from `availableModes` on the API response, decided by the backend from its own
 * environment classification. In production that list contains REQUIRED alone, so the disable
 * option does not render — and if it somehow did, the backend refuses it anyway. Nothing here
 * is a source of truth; it is a view of one.</p>
 */
export default function PlatformAuthenticationPage() {
  const queryClient = useQueryClient();
  const permissions = usePlatformPermissions();
  const { enqueueSnackbar } = useSnackbar();

  const policyQuery = useQuery({
    queryKey: platformKeys.platformAuthPolicy(),
    queryFn: () => platformApi.getMfaPolicy(),
  });
  const policy = policyQuery.data;

  const [mode, setMode] = useState<PlatformMfaMode | ''>('');
  const [reason, setReason] = useState('');
  const [expiresAtLocal, setExpiresAtLocal] = useState('');
  const [confirmation, setConfirmation] = useState('');
  const [currentPassword, setCurrentPassword] = useState('');

  const selectedMode: PlatformMfaMode = (mode || policy?.mode || 'REQUIRED') as PlatformMfaMode;
  const requiresExpiry = selectedMode !== 'REQUIRED';
  const expectedPhrase = policy?.confirmationPhrases?.[selectedMode] ?? '';

  const changeMutation = useMutation({
    mutationFn: () =>
      platformApi.changeMfaPolicy({
        mode: selectedMode,
        currentPassword,
        reason: reason.trim(),
        expiresAtUtc: requiresExpiry ? toIsoOrNull(expiresAtLocal) : null,
        confirmation: confirmation.trim(),
        expectedVersion: policy?.version ?? null,
      }),
    onSuccess: (saved) => {
      enqueueSnackbar(`MFA enforcement is now ${saved.mode}.`, { variant: 'success' });
      // The password never survives a save, successful or not — it is a step-up credential and
      // has no business sitting in component state waiting for the next render.
      setCurrentPassword('');
      setConfirmation('');
      setReason('');
      setExpiresAtLocal('');
      setMode('');
      queryClient.setQueryData(platformKeys.platformAuthPolicy(), saved);
      void queryClient.invalidateQueries({ queryKey: platformKeys.platformAuthPolicyEffective() });
    },
    onError: (error) => {
      setCurrentPassword('');
      if (platformErrorStatus(error) === 409) {
        void queryClient.invalidateQueries({ queryKey: platformKeys.platformAuthPolicy() });
        enqueueSnackbar('This policy was changed by someone else. Reloaded — reapply your change.', {
          variant: 'info',
        });
        return;
      }
      enqueueSnackbar(platformErrorMessage(error, 'Could not change MFA enforcement'), {
        variant: 'error',
      });
    },
  });

  /**
   * Why Apply is unavailable, in the operator's words, or null when it is available.
   *
   * This exists because a disabled button is indistinguishable from a broken one — the exact
   * defect that was just fixed on the outbound email screen. Every precondition below is a
   * control the SERVER enforces; naming it here means the operator learns what is missing
   * before they click, instead of from a 400.
   */
  const applyBlockedBecause = useMemo(() => {
    if (!policy) return 'Loading the current policy.';
    if (!policy.availableModes.includes(selectedMode))
      return `${selectedMode} is not available in ${policy.environmentName} (classified ${policy.environmentClass}).`;
    if (selectedMode === policy.mode && selectedMode === policy.declaredMode)
      return `MFA enforcement is already ${selectedMode}.`;
    if (reason.trim().length < policy.minimumReasonLength)
      return `Give a reason of at least ${policy.minimumReasonLength} characters — it is recorded in the audit log and is what a reviewer reads months from now.`;
    if (requiresExpiry && !toIsoOrNull(expiresAtLocal))
      return 'An expiry is required. MFA enforcement may be relaxed for a window, never indefinitely.';
    if (requiresExpiry) {
      const expiry = new Date(expiresAtLocal).getTime();
      const now = Date.now();
      if (expiry <= now) return 'The expiry must be in the future.';
      if (expiry > now + policy.maxBypassHours * 3_600_000)
        return `The maximum bypass duration is ${policy.maxBypassHours} hours.`;
    }
    if (confirmation.trim() !== expectedPhrase)
      return `Type ${expectedPhrase} exactly to confirm.`;
    if (!currentPassword)
      return 'Re-enter your current platform password. Changing enforcement requires proving you are still at the keyboard.';
    return null;
  }, [
    policy, selectedMode, reason, requiresExpiry, expiresAtLocal, confirmation, expectedPhrase, currentPassword,
  ]);

  if (policyQuery.isLoading) return <LoadingState label="Loading platform authentication policy…" />;
  if (policyQuery.isError || !policy)
    return (
      <ErrorState
        message={platformErrorMessage(policyQuery.error, 'Could not load the platform authentication policy')}
        onRetry={() => void policyQuery.refetch()}
      />
    );

  const relaxable = policy.availableModes.some((available) => available !== 'REQUIRED');

  return (
    <Box>
      <PageHeader
        title="Platform Authentication"
        subtitle="Server-authoritative multi-factor enforcement for platform operators."
      />

      {/* The state of the world, before anything editable. Severity tracks how far from
          enforced this deployment currently is, because that is the fact an operator has to
          absorb in one glance. */}
      <Alert
        severity={policy.enforcementDisabled ? 'error' : policy.mode === 'REQUIRED' ? 'success' : 'warning'}
        icon={policy.mode === 'REQUIRED' ? <EnforcedIcon /> : <ProblemIcon />}
        sx={{ mb: 2.5, borderRadius: 3 }}
        data-testid="platform-mfa-policy-status"
      >
        <AlertTitle sx={{ fontWeight: 800 }}>
          {policy.enforcementDisabled
            ? 'MFA enforcement is disabled in this test environment.'
            : policy.mode === 'REQUIRED'
              ? 'MFA is required for every platform operator.'
              : 'MFA enforcement is relaxed.'}
        </AlertTitle>
        <Typography variant="body2">
          {MODE_COPY[policy.mode]}
        </Typography>
        {policy.bypassExpired && (
          <Typography variant="body2" sx={{ mt: 1, fontWeight: 700 }}>
            The {policy.declaredMode} window set by {policy.changedBy ?? 'an operator'} expired at{' '}
            {policy.expiresAtUtc ? fmtDateTime(policy.expiresAtUtc) : 'its stated time'} and enforcement
            returned to REQUIRED on its own.
          </Typography>
        )}
        <Stack direction="row" spacing={1} sx={{ mt: 1.5, flexWrap: 'wrap', gap: 1 }}>
          <Chip size="small" label={`Environment: ${policy.environmentName}`} />
          <Chip size="small" label={`Class: ${policy.environmentClass}`} />
          <Chip size="small" label={`Mode: ${policy.mode}`} />
          {policy.expiresAtUtc && (
            <Chip size="small" color="warning" label={`Expires: ${fmtDateTime(policy.expiresAtUtc)}`} />
          )}
          {policy.isolatedTestInfrastructure && (
            <Chip size="small" label="Classified isolated test infrastructure" />
          )}
        </Stack>
      </Alert>

      <Paper sx={{ p: { xs: 2, md: 3 }, borderRadius: 3, mb: 2.5 }}>
        <Typography variant="h6" sx={{ fontWeight: 800, mb: 2 }}>
          Current policy
        </Typography>
        <Grid container spacing={2}>
          <Fact label="Effective mode" value={policy.mode} />
          <Fact label="Declared mode" value={policy.declaredMode} />
          <Fact label="Effective from" value={fmtDateTime(policy.effectiveFromUtc)} />
          <Fact label="Expires" value={policy.expiresAtUtc ? fmtDateTime(policy.expiresAtUtc) : 'No expiry (REQUIRED)'} />
          <Fact label="Changed by" value={policy.changedBy ?? 'Never changed — deployment default'} />
          <Fact label="Last changed" value={policy.updatedAtUtc ? fmtDateTime(policy.updatedAtUtc) : '—'} />
          <Fact label="Reason" value={policy.changeReason ?? '—'} />
          <Fact
            label="Affected operators"
            value={`${policy.enrolledOperatorCount} enrolled of ${policy.activeOperatorCount} active`}
          />
          <Fact label="Active MFA-bound sessions" value={String(policy.activeMfaBoundSessionCount)} />
          <Fact label="Remembered browsers" value={`${policy.activeBrowserTrustCount} (trust window ${policy.browserTrustHours}h)`} />
        </Grid>
      </Paper>

      <Paper sx={{ p: { xs: 2, md: 3 }, borderRadius: 3 }}>
        <Typography variant="h6" sx={{ fontWeight: 800, mb: 0.5 }}>
          Change enforcement
        </Typography>
        <Typography variant="body2" color="text.secondary" sx={{ mb: 2 }}>
          Every change is versioned and written to the immutable platform audit log with your name,
          the environment, the reason and the expiry.
        </Typography>

        {/* The security warning, above the controls rather than beside the button. It is the
            argument against doing this, and it has to be readable before the form is filled in. */}
        <Alert severity="warning" sx={{ mb: 2.5, borderRadius: 2 }}>
          <AlertTitle sx={{ fontWeight: 800 }}>Read this before relaxing enforcement</AlertTitle>
          A platform session reads and administers every tenant on this deployment under BYPASSRLS.
          With MFA relaxed, a stolen or guessed platform password is enough on its own. Purge, export,
          legal-hold release, invoice finalisation and this screen still demand a password
          re-authentication and their existing typed confirmations — that is the compensating control,
          not a reason the relaxation is safe.
        </Alert>

        {!relaxable ? (
          // Production renders no disable option at all. Saying WHY beats an empty dropdown:
          // an operator who cannot find the control needs to know it does not exist here.
          <Alert severity="info" sx={{ borderRadius: 2 }} data-testid="platform-mfa-not-relaxable">
            <AlertTitle sx={{ fontWeight: 800 }}>REQUIRED is the only mode available here</AlertTitle>
            {policy.environmentName} is classified {policy.environmentClass}, and a production-class
            deployment accepts REQUIRED only. There is no configuration on this screen that changes
            that; relaxing enforcement is not reachable in production by any route.
          </Alert>
        ) : (
          <>
            <Grid container spacing={2}>
              <Grid size={{ xs: 12, md: 6 }}>
                {/* A NATIVE select, not MUI's popover one. Two reasons, and both are about the
                    control being real: a native <select> is operable by keyboard and screen reader
                    with no JavaScript of ours in the path, and it is the element an automated
                    certification of this screen can actually drive — a popover listbox is testable
                    only by whoever knows it is a popover. */}
                <TextField
                  select
                  fullWidth
                  label="MFA enforcement mode"
                  value={selectedMode}
                  onChange={(event) => {
                    setMode(event.target.value as PlatformMfaMode);
                    setConfirmation('');
                  }}
                  slotProps={{ select: { native: true }, inputLabel: { shrink: true } }}
                  helperText={MODE_COPY[selectedMode]}
                >
                  {policy.availableModes.map((available) => (
                    <option key={available} value={available}>
                      {available}
                    </option>
                  ))}
                </TextField>
              </Grid>

              <Grid size={{ xs: 12, md: 6 }}>
                <TextField
                  fullWidth
                  type="datetime-local"
                  // Labelled "Expires at" and not "Expires at (UTC)": a datetime-local input is
                  // the browser's WALL CLOCK, and a label that says UTC over a control that means
                  // local time is how an operator sets a four-hour bypass and gets a seven-hour
                  // one. The conversion is stated in the helper text and done on submit.
                  label="Expires at"
                  value={expiresAtLocal}
                  onChange={(event) => setExpiresAtLocal(event.target.value)}
                  disabled={!requiresExpiry}
                  slotProps={{ inputLabel: { shrink: true } }}
                  helperText={
                    requiresExpiry
                      ? `Your local time, stored and audited in UTC. Mandatory, maximum ${policy.maxBypassHours} hours — enforcement returns to REQUIRED on its own when this passes, with no action from anyone.`
                      : 'REQUIRED does not expire.'
                  }
                />
              </Grid>

              <Grid size={12}>
                <TextField
                  fullWidth
                  multiline
                  minRows={2}
                  required
                  label="Reason"
                  value={reason}
                  onChange={(event) => setReason(event.target.value)}
                  helperText={`At least ${policy.minimumReasonLength} characters. Recorded in the audit log. Apply stays unavailable until this is filled in.`}
                />
              </Grid>

              <Grid size={{ xs: 12, md: 6 }}>
                <TextField
                  fullWidth
                  required
                  label="Confirmation phrase"
                  value={confirmation}
                  onChange={(event) => setConfirmation(event.target.value)}
                  helperText={`Type ${expectedPhrase} exactly.`}
                />
              </Grid>

              <Grid size={{ xs: 12, md: 6 }}>
                <TextField
                  fullWidth
                  required
                  type="password"
                  autoComplete="current-password"
                  label="Current password"
                  value={currentPassword}
                  onChange={(event) => setCurrentPassword(event.target.value)}
                  helperText="Re-authentication. Your session is already signed in; this proves you are still the person at the keyboard."
                />
              </Grid>
            </Grid>

            <Divider sx={{ my: 2.5 }} />

            <Box>
              <RoleGate allowed={permissions.isOwner} requirement={REQUIRED_ROLE_COPY.platformAuthentication}>
                {(roleBlocked) => (
                  <Tooltip title={!roleBlocked && applyBlockedBecause ? applyBlockedBecause : ''}>
                    <Box component="span" sx={{ display: 'inline-flex' }}>
                      <Button
                        variant="contained"
                        color={selectedMode === 'REQUIRED' ? 'primary' : 'warning'}
                        startIcon={<PolicyIcon />}
                        disabled={roleBlocked || changeMutation.isPending || applyBlockedBecause !== null}
                        onClick={() => changeMutation.mutate()}
                      >
                        {changeMutation.isPending ? 'Applying…' : 'Apply MFA policy'}
                      </Button>
                    </Box>
                  </Tooltip>
                )}
              </RoleGate>
              {/* On screen, not only on hover. A tooltip an operator never thinks to hover over is
                  the same as no explanation at all, and this is exactly where a silently disabled
                  button reads as a broken console. */}
              {permissions.isOwner && applyBlockedBecause && (
                <Typography
                  variant="caption"
                  color="error"
                  data-testid="platform-mfa-apply-blocked"
                  sx={{ display: 'block', mt: 1 }}
                >
                  {applyBlockedBecause}
                </Typography>
              )}
            </Box>
          </>
        )}
      </Paper>
    </Box>
  );
}

function Fact({ label, value }: { label: string; value: string }) {
  return (
    <Grid size={{ xs: 12, sm: 6, md: 4 }}>
      <Typography variant="caption" color="text.secondary" sx={{ display: 'block' }}>
        {label}
      </Typography>
      <Typography variant="body2" sx={{ fontWeight: 600, wordBreak: 'break-word' }}>
        {value}
      </Typography>
    </Grid>
  );
}
