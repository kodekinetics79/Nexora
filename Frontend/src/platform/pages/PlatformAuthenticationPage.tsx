import { useMemo, useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import {
  Alert,
  AlertTitle,
  Box,
  Button,
  Chip,
  Divider,
  FormControlLabel,
  Grid,
  Paper,
  Stack,
  Switch,
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableRow,
  TextField,
  Tooltip,
  Typography,
} from '@mui/material';
import {
  DevicesOutlined as DevicesIcon,
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
import { fmtDateTime, fmtTrustWindow } from '../components/format';
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

/**
 * The browser-trust windows worth offering as one click, in hours.
 *
 * Presets rather than a free number box because the meaningful choices here are few and the
 * consequences of each are different in kind: a working day, a working week, a month. A spinner
 * invites 719, which nobody means. The current value is always added to this list, so a window set
 * by an earlier decision — or by configuration — is selectable rather than silently discarded the
 * first time somebody opens this control to change something else.
 */
const TRUST_WINDOW_PRESETS = [12, 24 * 7, 24 * 30];

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

  // The caller's OWN remembered browsers. Not Owner-gated — the endpoint is scoped to the signed-in
  // operator server-side, and revoking your own trust is the one action here that only ever removes
  // authority. It sits on this page because this is where the window that governs it is set.
  const trustsQuery = useQuery({
    queryKey: platformKeys.platformBrowserTrusts(),
    queryFn: () => platformApi.listBrowserTrusts(),
  });

  const [mode, setMode] = useState<PlatformMfaMode | ''>('');
  const [reason, setReason] = useState('');
  const [expiresAtLocal, setExpiresAtLocal] = useState('');
  const [confirmation, setConfirmation] = useState('');
  const [currentPassword, setCurrentPassword] = useState('');
  // Null means "the operator has not touched this", which is what lets the controls track the
  // server's value across a refetch instead of pinning whatever it happened to be on first render.
  const [trustEnabledDraft, setTrustEnabledDraft] = useState<boolean | null>(null);
  const [trustHoursDraft, setTrustHoursDraft] = useState<number | null>(null);

  const selectedMode: PlatformMfaMode = (mode || policy?.mode || 'REQUIRED') as PlatformMfaMode;
  const requiresExpiry = selectedMode !== 'REQUIRED';
  const expectedPhrase = policy?.confirmationPhrases?.[selectedMode] ?? '';

  const trustEnabled = trustEnabledDraft ?? policy?.browserTrustEnabled ?? true;
  const trustHours = trustHoursDraft ?? policy?.browserTrustHours ?? 12;
  const trustChanged = Boolean(policy)
    && (trustEnabled !== policy!.browserTrustEnabled || trustHours !== policy!.browserTrustHours);

  const changeMutation = useMutation({
    mutationFn: () =>
      platformApi.changeMfaPolicy({
        mode: selectedMode,
        currentPassword,
        reason: reason.trim(),
        expiresAtUtc: requiresExpiry ? toIsoOrNull(expiresAtLocal) : null,
        confirmation: confirmation.trim(),
        expectedVersion: policy?.version ?? null,
        browserTrustEnabled: trustEnabled,
        browserTrustHours: trustHours,
      }),
    onSuccess: (saved) => {
      enqueueSnackbar(
        saved.browserTrustEnabled
          ? `MFA enforcement is now ${saved.mode}. Remembered browsers last ${fmtTrustWindow(saved.browserTrustHours)}.`
          : `MFA enforcement is now ${saved.mode}. Remembered browsers are switched off.`,
        { variant: 'success' },
      );
      // The password never survives a save, successful or not — it is a step-up credential and
      // has no business sitting in component state waiting for the next render.
      setCurrentPassword('');
      setConfirmation('');
      setReason('');
      setExpiresAtLocal('');
      setMode('');
      setTrustEnabledDraft(null);
      setTrustHoursDraft(null);
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
    // "Nothing to apply" now means nothing at all has moved. Checking only the mode blocked the
    // one change a production Owner CAN make here — the mode is REQUIRED and stays REQUIRED, and
    // the remembered-browser window is the thing they came to set.
    if (!trustChanged && selectedMode === policy.mode && selectedMode === policy.declaredMode)
      return `MFA enforcement is already ${selectedMode} and the remembered-browser settings are unchanged.`;
    if (trustHours < policy.minBrowserTrustHours || trustHours > policy.maxBrowserTrustHours)
      return `A remembered browser may be trusted for between ${fmtTrustWindow(policy.minBrowserTrustHours)} and ${fmtTrustWindow(policy.maxBrowserTrustHours)}.`;
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
    trustChanged, trustHours,
  ]);

  const revokeMutation = useMutation({
    mutationFn: (id: number) => platformApi.revokeBrowserTrust(id),
    onSuccess: () => {
      // Not "will be revoked shortly". The revocation is a column both the login redemption and
      // PlatformSessionValidator read, so it also ends the live session that trust minted.
      enqueueSnackbar('That browser is no longer remembered, and any session it signed in is over.', {
        variant: 'success',
      });
      void queryClient.invalidateQueries({ queryKey: platformKeys.platformBrowserTrusts() });
      void queryClient.invalidateQueries({ queryKey: platformKeys.platformAuthPolicy() });
    },
    onError: (error) =>
      enqueueSnackbar(platformErrorMessage(error, 'Could not revoke that browser'), { variant: 'error' }),
  });

  const revokeAllMutation = useMutation({
    mutationFn: () => platformApi.revokeAllBrowserTrusts(),
    onSuccess: (revoked) => {
      enqueueSnackbar(
        revoked === 0
          ? 'There were no remembered browsers to revoke.'
          : `Revoked ${revoked} remembered ${revoked === 1 ? 'browser' : 'browsers'}. Every session they signed in is over.`,
        { variant: revoked === 0 ? 'info' : 'success' },
      );
      void queryClient.invalidateQueries({ queryKey: platformKeys.platformBrowserTrusts() });
      void queryClient.invalidateQueries({ queryKey: platformKeys.platformAuthPolicy() });
    },
    onError: (error) =>
      enqueueSnackbar(platformErrorMessage(error, 'Could not revoke your remembered browsers'), {
        variant: 'error',
      }),
  });

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
          <Fact
            label="Remembered browsers (all operators)"
            value={
              policy.browserTrustEnabled
                ? `${policy.activeBrowserTrustCount} live, window ${fmtTrustWindow(policy.browserTrustHours)}`
                : `${policy.activeBrowserTrustCount} stored, but browser trust is OFF — none of them are honoured`
            }
          />
          <Fact
            label="Trust window set by"
            // A number nobody chose looks identical to a number somebody did, and the difference
            // decides whether the current window is a decision or an oversight.
            value={
              policy.browserTrustFromPolicyRow
                ? `This screen${policy.changedBy ? ` (${policy.changedBy})` : ''}`
                : 'Deployment default — never set here'
            }
          />
        </Grid>
      </Paper>

      <Paper sx={{ p: { xs: 2, md: 3 }, borderRadius: 3, mb: 2.5 }}>
        <Stack direction="row" spacing={1} sx={{ alignItems: 'center', mb: 0.5 }}>
          <DevicesIcon fontSize="small" color="action" />
          <Typography variant="h6" sx={{ fontWeight: 800 }}>
            Your remembered browsers
          </Typography>
        </Stack>
        <Typography variant="body2" color="text.secondary" sx={{ mb: 2 }}>
          Browsers you told the platform to remember, so an ordinary day costs one challenge instead
          of one per session. Yours only — there is no route that reads or revokes anybody else&apos;s.
          Revoking takes effect on the next request: it refuses the next sign-in AND ends the session
          that trust already signed in, rather than letting it run out its remaining lifetime.
        </Typography>

        {trustsQuery.isError ? (
          <Alert severity="warning" sx={{ borderRadius: 2 }}>
            {platformErrorMessage(trustsQuery.error, 'Could not load your remembered browsers')}
          </Alert>
        ) : trustsQuery.isLoading ? (
          <LoadingState label="Loading your remembered browsers…" />
        ) : (trustsQuery.data?.length ?? 0) === 0 ? (
          <Alert severity="info" sx={{ borderRadius: 2 }} data-testid="platform-browser-trusts-empty">
            No browser is currently remembered for your account. Every sign-in asks for your second
            factor.
          </Alert>
        ) : (
          <>
            <Box sx={{ overflowX: 'auto' }}>
              <Table size="small" data-testid="platform-browser-trusts">
                <TableHead>
                  <TableRow>
                    <TableCell>Browser</TableCell>
                    <TableCell>Remembered</TableCell>
                    <TableCell>Last used</TableCell>
                    <TableCell>Expires</TableCell>
                    <TableCell align="right">Action</TableCell>
                  </TableRow>
                </TableHead>
                <TableBody>
                  {trustsQuery.data!.map((trust) => (
                    <TableRow key={trust.id}>
                      <TableCell sx={{ fontWeight: 600 }}>{trust.label ?? 'Unidentified browser'}</TableCell>
                      <TableCell>{fmtDateTime(trust.createdAtUtc)}</TableCell>
                      <TableCell>
                        {trust.lastUsedAtUtc ? fmtDateTime(trust.lastUsedAtUtc) : 'Never since it was granted'}
                      </TableCell>
                      <TableCell>{fmtDateTime(trust.expiresAtUtc)}</TableCell>
                      <TableCell align="right">
                        <Button
                          size="small"
                          color="warning"
                          disabled={revokeMutation.isPending || revokeAllMutation.isPending}
                          onClick={() => revokeMutation.mutate(trust.id)}
                        >
                          Revoke
                        </Button>
                      </TableCell>
                    </TableRow>
                  ))}
                </TableBody>
              </Table>
            </Box>
            <Divider sx={{ my: 2 }} />
            {/* The "I have lost a device and I do not know which entry it is" control. It matters
                more the longer the window is: at 12 hours the problem expired before anyone had to
                think about it; at 30 days it does not. */}
            <Button
              variant="outlined"
              color="warning"
              disabled={revokeAllMutation.isPending || revokeMutation.isPending}
              onClick={() => revokeAllMutation.mutate()}
              data-testid="platform-browser-trusts-revoke-all"
            >
              {revokeAllMutation.isPending ? 'Revoking…' : 'Revoke all remembered browsers'}
            </Button>
          </>
        )}
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

        {!relaxable && (
          // Production renders no disable option at all. Saying WHY beats an empty dropdown:
          // an operator who cannot find the control needs to know it does not exist here.
          //
          // The rest of the form still renders. It used to be replaced entirely by this notice,
          // which was correct while the only thing this screen could change was the enforcement
          // mode — and became a defect the moment the remembered-browser window moved here, because
          // production is precisely where an Owner needs to set it and it would have been the one
          // environment with no control at all.
          <Alert severity="info" sx={{ mb: 2.5, borderRadius: 2 }} data-testid="platform-mfa-not-relaxable">
            <AlertTitle sx={{ fontWeight: 800 }}>REQUIRED is the only mode available here</AlertTitle>
            {policy.environmentName} is classified {policy.environmentClass}, and a production-class
            deployment accepts REQUIRED only. There is no configuration on this screen that changes
            that; relaxing enforcement is not reachable in production by any route. The
            remembered-browser settings below can still be changed, under the same ceremony.
          </Alert>
        )}

        <Grid container spacing={2}>
              {relaxable && (
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
              )}

              {relaxable && (
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
              )}

              <Grid size={12}>
                <Divider sx={{ mb: 1.5 }} />
                <Typography variant="subtitle2" sx={{ fontWeight: 800 }}>
                  Remembered browsers
                </Typography>
                <Typography variant="body2" color="text.secondary">
                  Whether an operator may skip the challenge on a browser that has already presented a
                  second factor, and for how long. Switching it off applies to browsers that are
                  already remembered — they stop counting at the next sign-in, not when their window
                  happens to run out. Widening the window is a real trade: fewer challenges, and a
                  longer period in which a stolen laptop signs in without one. Revoking a browser is
                  immediate and also ends the session it signed in.
                </Typography>
              </Grid>

              <Grid size={{ xs: 12, md: 6 }}>
                <FormControlLabel
                  control={
                    <Switch
                      checked={trustEnabled}
                      onChange={(event) => setTrustEnabledDraft(event.target.checked)}
                      // A STABLE accessible name, because the visible label deliberately changes
                      // with the state — "browser trust is off" is the sentence an operator needs to
                      // read, and a control whose accessible name changes underneath it is a control
                      // no screen reader user, and no test, can address twice.
                      slotProps={{ input: { 'aria-label': 'Offer remembered browsers' } }}
                    />
                  }
                  label={
                    trustEnabled
                      ? 'Offer "remember this browser"'
                      : 'Browser trust is off — every sign-in is challenged'
                  }
                />
              </Grid>

              <Grid size={{ xs: 12, md: 6 }}>
                <TextField
                  select
                  fullWidth
                  label="Remembered browser window"
                  value={String(trustHours)}
                  onChange={(event) => setTrustHoursDraft(Number(event.target.value))}
                  disabled={!trustEnabled}
                  slotProps={{ select: { native: true }, inputLabel: { shrink: true } }}
                  helperText={
                    trustEnabled
                      ? `Between ${fmtTrustWindow(policy.minBrowserTrustHours)} and ${fmtTrustWindow(policy.maxBrowserTrustHours)}. Applies to browsers remembered from now on; browsers already remembered keep the window they were granted.`
                      : 'Not applicable while browser trust is switched off.'
                  }
                >
                  {/* The current value is always present, so a window set earlier — or one that
                      came from the deployment's configuration — is not silently replaced by the
                      nearest preset the first time somebody opens this control. */}
                  {Array.from(new Set([...TRUST_WINDOW_PRESETS, trustHours]))
                    .filter((hours) => hours >= policy.minBrowserTrustHours && hours <= policy.maxBrowserTrustHours)
                    .sort((a, b) => a - b)
                    .map((hours) => (
                      <option key={hours} value={hours}>
                        {fmtTrustWindow(hours)}
                      </option>
                    ))}
                </TextField>
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
