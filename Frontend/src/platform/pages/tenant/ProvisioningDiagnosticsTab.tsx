import { useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import {
  Alert, AlertTitle, Box, Button, Dialog, DialogActions, DialogContent, DialogTitle,
  FormControl, InputLabel, LinearProgress, MenuItem, Paper, Select, TextField, Typography,
} from '@mui/material';
import { useSnackbar } from 'notistack';
import Stack from '../../components/Flex';
import { ErrorState, LoadingState } from '../../components/States';
import { SoftChip } from '../../components/StatusChip';
import RoleGate from '../../components/RoleGate';
import { fmtDateTime } from '../../components/format';
import { platformApi } from '../../api/client';
import { platformErrorMessage } from '../../api/apiError';
import { platformKeys } from '../../api/queryKeys';
import { usePlatformPermissions } from '../../auth/usePlatformPermissions';
import { REQUIRED_ROLE_COPY } from '../../auth/permissions';
import { TENANT_DEPLOYMENT_PROFILES } from '../../types';
import type {
  ActivationControlDisposition,
  ProvisioningBlocker,
  ProvisioningIssueClassification,
  ProvisioningRecoveryAction,
  Tenant,
  TenantDeploymentProfile,
} from '../../types';

/**
 * Why this tenant is not finished, and what may safely be done about it.
 *
 * <b>The defect this closes.</b> Every fact on this screen was already persisted — the failed
 * step, its code, the sentence the runner wrote, the correlation id, both blocker lists — and
 * none of it reached a human. The console rendered "Provisioning failed." for four unrelated
 * causes with four different owners, so the operator's only remaining move was to ask an
 * engineer to read the database.
 *
 * <b>The rule this screen follows, from the email-settings work.</b> Nothing is ever disabled
 * without the unmet precondition being named ON SCREEN, not only in a tooltip. A control the
 * operator can see but cannot use, with no stated reason, is indistinguishable from a broken
 * product — and the reason is exactly the thing they came here to find out.
 */

const CLASSIFICATION_COPY: Record<
  ProvisioningIssueClassification,
  { label: string; tone: 'error' | 'warning' | 'info' | 'neutral'; owner: string }
> = {
  CUSTOMER_INPUT: {
    label: 'Customer input',
    tone: 'warning',
    owner: 'Something in the submitted request has to change. Retrying it unchanged cannot succeed.',
  },
  PLATFORM_CONFIGURATION: {
    label: 'Platform configuration',
    tone: 'error',
    owner: 'This deployment is wired wrong. The customer did nothing; a platform engineer has to act.',
  },
  EXTERNAL_DEPENDENCY: {
    label: 'External dependency',
    tone: 'warning',
    owner: 'Something outside this process refused or never answered. Retry once it is back.',
  },
  RETRYABLE_SYSTEM_FAILURE: {
    label: 'Retryable system failure',
    tone: 'info',
    owner: 'Unclassified and not terminal. Retrying is the correct first move.',
  },
};

const DISPOSITION_TONE: Record<ActivationControlDisposition, 'success' | 'error' | 'warning' | 'info'> = {
  SATISFIED: 'success',
  BLOCKING: 'error',
  DEFERRED: 'warning',
  EXTERNALLY_BLOCKED: 'info',
};

const STEP_TONE: Record<string, 'success' | 'error' | 'warning' | 'info' | 'neutral'> = {
  Succeeded: 'success',
  Skipped: 'success',
  Failed: 'error',
  Running: 'info',
  Pending: 'neutral',
  Cancelled: 'neutral',
};

const PROFILE_TONE: Record<TenantDeploymentProfile, 'success' | 'warning' | 'info'> = {
  PRODUCTION: 'success',
  LOCAL_TEST: 'warning',
  DEMO: 'info',
};

/** The floor the server enforces. Stated here so the box explains itself before it refuses. */
const MIN_PROFILE_REASON = 15;

function BlockerList({
  title,
  caption,
  blockers,
  emptyCopy,
}: {
  title: string;
  caption: string;
  blockers: ProvisioningBlocker[];
  emptyCopy: string;
}) {
  return (
    <Paper variant="outlined" sx={{ p: 2, flex: 1, minWidth: 280 }}>
      <Typography sx={{ fontWeight: 800 }}>{title}</Typography>
      <Typography variant="body2" color="text.secondary" sx={{ mb: 1.5 }}>{caption}</Typography>
      {blockers.length === 0 ? (
        <Alert severity="success">{emptyCopy}</Alert>
      ) : (
        <Stack spacing={1}>
          {blockers.map((blocker) => (
            <Box key={`${blocker.scope}:${blocker.code}`}>
              <Stack direction="row" spacing={1} alignItems="center" sx={{ flexWrap: 'wrap' }}>
                <Typography sx={{ fontWeight: 700, overflowWrap: 'anywhere' }}>{blocker.code}</Typography>
                <SoftChip label={blocker.disposition} tone={DISPOSITION_TONE[blocker.disposition]} dot={false} />
              </Stack>
              <Typography variant="body2" color="text.secondary">{blocker.detail}</Typography>
              {blocker.productionRequirement && (
                <Typography variant="caption" color="text.secondary" sx={{ display: 'block' }}>
                  Production needs: {blocker.productionRequirement}
                </Typography>
              )}
            </Box>
          ))}
        </Stack>
      )}
    </Paper>
  );
}

function RecoveryControl({
  action,
  canAdminister,
  onRun,
  pending,
}: {
  action: ProvisioningRecoveryAction;
  canAdminister: boolean;
  onRun: (action: ProvisioningRecoveryAction) => void;
  pending: boolean;
}) {
  const label = action.action === 'resume' ? 'Resume provisioning' : `Re-run ${action.step}`;
  const disabled = !action.available || !canAdminister || pending;
  return (
    <Paper variant="outlined" sx={{ p: 2 }}>
      <Stack direction={{ xs: 'column', md: 'row' }} justifyContent="space-between" spacing={1.5}>
        <Box>
          <Typography sx={{ fontWeight: 700 }}>{label}</Typography>
          {/* The reason is rendered on the surface, not hidden in a tooltip: the operator opened
              this screen precisely to find out why nothing can be done. */}
          <Typography variant="body2" color="text.secondary">{action.detail}</Typography>
          {!canAdminister && (
            <Typography variant="body2" color="warning.main">
              Unavailable to your role. {REQUIRED_ROLE_COPY.tenantAdmin}
            </Typography>
          )}
          {action.available && !action.safe && (
            <Typography variant="body2" color="warning.main">
              The server would accept this, but it is not expected to help. Change what the failure
              names first.
            </Typography>
          )}
        </Box>
        <RoleGate allowed={canAdminister} requirement={REQUIRED_ROLE_COPY.tenantAdmin}>
          {(gated) => (
            <Button
              variant={action.safe ? 'contained' : 'outlined'}
              color={action.safe ? 'primary' : 'warning'}
              disabled={disabled || gated}
              onClick={() => onRun(action)}
            >
              {label}
            </Button>
          )}
        </RoleGate>
      </Stack>
    </Paper>
  );
}

export default function ProvisioningDiagnosticsTab({ tenant }: { tenant: Tenant }) {
  const queryClient = useQueryClient();
  const { enqueueSnackbar } = useSnackbar();
  const permissions = usePlatformPermissions();
  const [recovery, setRecovery] = useState<{ action: ProvisioningRecoveryAction; reason: string } | null>(null);
  const [profile, setProfile] = useState<{ value: TenantDeploymentProfile; reason: string } | null>(null);

  const diagnosticsQuery = useQuery({
    queryKey: platformKeys.provisioningDiagnostics(tenant.id),
    queryFn: () => platformApi.getTenantProvisioningDiagnostics(tenant.id),
  });

  const invalidate = () => {
    queryClient.invalidateQueries({ queryKey: platformKeys.provisioningDiagnostics(tenant.id) });
    queryClient.invalidateQueries({ queryKey: platformKeys.tenant(tenant.id) });
    queryClient.invalidateQueries({ queryKey: platformKeys.tenantActivationDecision(tenant.id) });
  };

  const retryMutation = useMutation({
    mutationFn: ({ executionId, action, reason }: {
      executionId: string; action: ProvisioningRecoveryAction; reason: string;
    }) => platformApi.retryProvisioning(executionId, {
      step: action.action === 'retry-step' ? action.step : null,
      reason,
    }),
    onSuccess: () => {
      setRecovery(null);
      invalidate();
      enqueueSnackbar('Provisioning was re-queued', { variant: 'success' });
    },
    onError: (error) => enqueueSnackbar(platformErrorMessage(error, 'The retry was refused'), { variant: 'error' }),
  });

  const profileMutation = useMutation({
    mutationFn: (input: { value: TenantDeploymentProfile; reason: string }) =>
      platformApi.setTenantDeploymentProfile(tenant.id, {
        profile: input.value,
        reason: input.value === 'PRODUCTION' ? null : input.reason.trim(),
      }),
    onSuccess: () => {
      setProfile(null);
      invalidate();
      enqueueSnackbar('Deployment profile updated', { variant: 'success' });
    },
    onError: (error) =>
      enqueueSnackbar(platformErrorMessage(error, 'The deployment profile change was refused'), { variant: 'error' }),
  });

  if (diagnosticsQuery.isLoading) return <LoadingState label="Reading the provisioning journal…" />;
  if (diagnosticsQuery.isError || !diagnosticsQuery.data) {
    return (
      <ErrorState
        message={platformErrorMessage(
          diagnosticsQuery.error,
          'The provisioning journal could not be read, so nothing can be said about why this tenant is unfinished. No recovery action is offered.',
        )}
        onRetry={() => diagnosticsQuery.refetch()}
      />
    );
  }

  const diagnostics = diagnosticsQuery.data;
  const classification = CLASSIFICATION_COPY[diagnostics.classification]
    ?? CLASSIFICATION_COPY.RETRYABLE_SYSTEM_FAILURE;
  const progress = diagnostics.totalStepCount > 0
    ? Math.round((diagnostics.completedStepCount / diagnostics.totalStepCount) * 100)
    : 0;
  const profileReasonValid =
    profile?.value === 'PRODUCTION' || (profile?.reason.trim().length ?? 0) >= MIN_PROFILE_REASON;

  return <>
    <Stack spacing={2.5}>
      <Paper sx={{ p: 3, borderRadius: 3 }}>
        <Stack direction={{ xs: 'column', md: 'row' }} justifyContent="space-between" spacing={2}>
          <Box>
            <Typography variant="h6" sx={{ fontWeight: 800 }}>Provisioning</Typography>
            <Typography variant="body2" color="text.secondary">
              {diagnostics.status === 'NotStarted'
                ? 'This tenant has no durable provisioning execution.'
                : `Execution ${diagnostics.executionId} · ${diagnostics.completedStepCount} of ${diagnostics.totalStepCount} steps complete · attempt ${diagnostics.attemptCount}.`}
            </Typography>
          </Box>
          <Stack direction="row" spacing={1} alignItems="center" sx={{ flexWrap: 'wrap' }}>
            <SoftChip label={diagnostics.status} tone={STEP_TONE[diagnostics.status] ?? 'neutral'} />
            <SoftChip label={classification.label} tone={classification.tone} dot={false} />
            <SoftChip
              label={diagnostics.deploymentProfile}
              tone={PROFILE_TONE[diagnostics.deploymentProfile]}
              dot={false}
            />
          </Stack>
        </Stack>

        <LinearProgress variant="determinate" value={progress} sx={{ mt: 2, height: 8, borderRadius: 4 }} />

        <Alert severity={classification.tone === 'error' ? 'error' : classification.tone === 'warning' ? 'warning' : 'info'} sx={{ mt: 2 }}>
          <AlertTitle sx={{ fontWeight: 800 }}>
            {diagnostics.failedStep
              ? `${diagnostics.failedStep.label} failed`
              : diagnostics.currentStep
                ? `Current step: ${diagnostics.currentStep}`
                : classification.label}
          </AlertTitle>
          {/* Never "Provisioning failed." on its own: the sentence the runner wrote, then what
              kind of problem it is, then who owns it. */}
          <Typography variant="body2">
            {diagnostics.failureReason ?? diagnostics.classificationDetail}
          </Typography>
          <Typography variant="body2" sx={{ mt: 1 }}>{classification.owner}</Typography>
          <Typography variant="body2" sx={{ mt: 1 }}>{diagnostics.classificationDetail}</Typography>
        </Alert>

        {diagnostics.missingPrerequisite && (
          <Alert severity="warning" sx={{ mt: 1.5 }}>
            <AlertTitle sx={{ fontWeight: 800 }}>Missing prerequisite</AlertTitle>
            {diagnostics.missingPrerequisite}
          </Alert>
        )}

        <Stack direction={{ xs: 'column', sm: 'row' }} spacing={1} sx={{ mt: 2, flexWrap: 'wrap' }}>
          {diagnostics.failureCode && (
            <SoftChip label={`Failure code ${diagnostics.failureCode}`} tone="error" dot={false} />
          )}
          {diagnostics.correlationId && (
            <SoftChip label={`Correlation ${diagnostics.correlationId}`} tone="neutral" dot={false} />
          )}
        </Stack>
        <Typography variant="caption" color="text.secondary" sx={{ display: 'block', mt: 1.5 }}>
          Evaluated {fmtDateTime(diagnostics.evaluatedAtUtc)}. This screen writes nothing; every action
          below is a separate, audited request.
        </Typography>
      </Paper>

      <Paper sx={{ p: 3, borderRadius: 3 }}>
        <Typography variant="h6" sx={{ fontWeight: 800 }}>Steps</Typography>
        <Typography variant="body2" color="text.secondary" sx={{ mb: 1.5 }}>
          Recorded verdicts, in execution order. A skipped step is a decision, not an absence.
        </Typography>
        {diagnostics.steps.length === 0 ? (
          <Alert severity="info">
            No steps are journalled for this tenant, so there is nothing to replay. That is itself the
            finding: this tenant was not created by the durable provisioning engine.
          </Alert>
        ) : (
          <Stack spacing={1}>
            {diagnostics.steps.map((step) => (
              <Paper key={step.step} variant="outlined" sx={{ p: 1.5 }}>
                <Stack direction={{ xs: 'column', md: 'row' }} justifyContent="space-between" spacing={1}>
                  <Box>
                    <Stack direction="row" spacing={1} alignItems="center" sx={{ flexWrap: 'wrap' }}>
                      <Typography sx={{ fontWeight: 700 }}>{step.label}</Typography>
                      <SoftChip label={step.status} tone={STEP_TONE[step.status] ?? 'neutral'} dot={false} />
                    </Stack>
                    {step.failureReason && (
                      <Typography variant="body2" color="error.main">{step.failureReason}</Typography>
                    )}
                  </Box>
                  <Typography variant="caption" color="text.secondary">
                    {step.step} · attempt {step.attemptCount}
                    {step.durationMs != null ? ` · ${step.durationMs}ms` : ''}
                  </Typography>
                </Stack>
              </Paper>
            ))}
          </Stack>
        )}
      </Paper>

      <Paper sx={{ p: 3, borderRadius: 3 }}>
        <Typography variant="h6" sx={{ fontWeight: 800 }}>Recovery</Typography>
        <Typography variant="body2" color="text.secondary" sx={{ mb: 1.5 }}>
          What the server will accept, and whether it is expected to help. A reason is required and audited.
        </Typography>
        <Stack spacing={1.25}>
          {diagnostics.recoveryActions.map((action) => (
            <RecoveryControl
              key={`${action.action}:${action.step ?? ''}`}
              action={action}
              canAdminister={permissions.canAdministerTenants}
              pending={retryMutation.isPending}
              onRun={(chosen) => setRecovery({ action: chosen, reason: '' })}
            />
          ))}
        </Stack>
      </Paper>

      <Paper sx={{ p: 3, borderRadius: 3 }}>
        <Stack direction={{ xs: 'column', md: 'row' }} justifyContent="space-between" spacing={2}>
          <Box>
            <Typography variant="h6" sx={{ fontWeight: 800 }}>Readiness</Typography>
            <Typography variant="body2" color="text.secondary">
              Two bars, computed independently of this tenant's own profile so they stay comparable.
            </Typography>
          </Box>
          <RoleGate allowed={permissions.isOwner} requirement={REQUIRED_ROLE_COPY.owner}>
            {(gated) => (
              <Button
                variant="outlined"
                disabled={gated}
                onClick={() => setProfile({
                  value: tenant.deploymentProfile,
                  reason: tenant.deploymentProfileReason ?? '',
                })}
              >
                Change deployment profile
              </Button>
            )}
          </RoleGate>
        </Stack>

        {!permissions.isOwner && (
          <Typography variant="body2" color="warning.main" sx={{ mt: 1 }}>
            The deployment profile decides which production controls this tenant may defer, so it is
            Owner-only. {REQUIRED_ROLE_COPY.owner}
          </Typography>
        )}

        <Alert severity={tenant.deploymentProfile === 'PRODUCTION' ? 'success' : 'warning'} sx={{ mt: 2 }}>
          <AlertTitle sx={{ fontWeight: 800 }}>Deployment profile: {tenant.deploymentProfile}</AlertTitle>
          {tenant.deploymentProfile === 'PRODUCTION'
            ? 'Every activation control is a hard gate on this tenant and nothing is deferrable.'
            : 'Catalogued external prerequisites may be deferred for activation. They remain production blockers and this tenant cannot be certified production-ready.'}
          {tenant.deploymentProfileReason && (
            <Typography variant="body2" sx={{ mt: 1 }}>Reason: {tenant.deploymentProfileReason}</Typography>
          )}
          {tenant.deploymentProfileApprovedBy && (
            <Typography variant="caption" sx={{ display: 'block', mt: 0.5 }}>
              Approved by {tenant.deploymentProfileApprovedBy}
              {tenant.deploymentProfileApprovedOn ? ` on ${fmtDateTime(tenant.deploymentProfileApprovedOn)}` : ''}.
            </Typography>
          )}
          {tenant.deploymentProfile === 'DEMO' && !tenant.deploymentProfileApprovedBy && (
            <Typography variant="body2" sx={{ mt: 1 }}>
              No Owner approval is recorded, so nothing is deferred and this tenant fails closed exactly
              as PRODUCTION.
            </Typography>
          )}
        </Alert>

        {diagnostics.blockersUnavailableReason && (
          <Alert severity="warning" sx={{ mt: 2 }}>
            <AlertTitle sx={{ fontWeight: 800 }}>Blockers are unknown, not empty</AlertTitle>
            {diagnostics.blockersUnavailableReason}
          </Alert>
        )}

        <Stack direction={{ xs: 'column', lg: 'row' }} spacing={2} sx={{ mt: 2 }}>
          <BlockerList
            title="Production blockers"
            caption="What this tenant owes a real customer. A deferral never removes anything from this list."
            blockers={diagnostics.productionBlockers}
            emptyCopy="No production blocker is outstanding."
          />
          <BlockerList
            title="Local-test blockers"
            caption="What no deployment profile may defer — defects in this tenant's own record, not absent third parties."
            blockers={diagnostics.localTestBlockers}
            emptyCopy="Nothing blocks this tenant on the LOCAL_TEST profile."
          />
        </Stack>
      </Paper>
    </Stack>

    <Dialog open={Boolean(recovery)} onClose={() => setRecovery(null)} fullWidth maxWidth="sm">
      <DialogTitle sx={{ fontWeight: 800 }}>
        {recovery?.action.action === 'resume' ? 'Resume provisioning' : `Re-run ${recovery?.action.step}`}
      </DialogTitle>
      <DialogContent dividers>
        {recovery && <Stack spacing={2}>
          <Alert severity={recovery.action.safe ? 'info' : 'warning'}>{recovery.action.detail}</Alert>
          <TextField
            required
            multiline
            minRows={2}
            label="Reason"
            value={recovery.reason}
            helperText="Audited. A retry re-executes privileged writes against a customer's data."
            onChange={(event) => setRecovery({ ...recovery, reason: event.target.value })}
          />
        </Stack>}
      </DialogContent>
      <DialogActions>
        <Button color="inherit" onClick={() => setRecovery(null)}>Cancel</Button>
        <Button
          variant="contained"
          disabled={
            !recovery
            || recovery.reason.trim().length < 5
            || retryMutation.isPending
            || !diagnostics.executionId
          }
          onClick={() => recovery && diagnostics.executionId && retryMutation.mutate({
            executionId: diagnostics.executionId,
            action: recovery.action,
            reason: recovery.reason.trim(),
          })}
        >
          Run it
        </Button>
      </DialogActions>
    </Dialog>

    <Dialog open={Boolean(profile)} onClose={() => setProfile(null)} fullWidth maxWidth="sm">
      <DialogTitle sx={{ fontWeight: 800 }}>Set the deployment profile</DialogTitle>
      <DialogContent dividers>
        {profile && <Stack spacing={2}>
          <Alert severity="warning">
            This decides which catalogued production prerequisites this tenant's activation may defer.
            It never changes what is checked, and PRODUCTION defers nothing under any circumstances.
          </Alert>
          <FormControl fullWidth>
            <InputLabel id="deployment-profile-select">Profile</InputLabel>
            <Select
              labelId="deployment-profile-select"
              label="Profile"
              value={profile.value}
              onChange={(event) =>
                setProfile({ ...profile, value: event.target.value as TenantDeploymentProfile })}
            >
              {TENANT_DEPLOYMENT_PROFILES.map((option) => (
                <MenuItem key={option} value={option}>{option}</MenuItem>
              ))}
            </Select>
          </FormControl>
          <TextField
            required={profile.value !== 'PRODUCTION'}
            multiline
            minRows={2}
            label="Reason"
            value={profile.reason}
            disabled={profile.value === 'PRODUCTION'}
            error={!profileReasonValid && profile.reason.length > 0}
            helperText={profile.value === 'PRODUCTION'
              ? 'Not required. Tightening a gate never needs a justification, and the recorded approval is cleared.'
              : `At least ${MIN_PROFILE_REASON} characters. Recorded with your address as the approver.`}
            onChange={(event) => setProfile({ ...profile, reason: event.target.value })}
          />
        </Stack>}
      </DialogContent>
      <DialogActions>
        <Button color="inherit" onClick={() => setProfile(null)}>Cancel</Button>
        <Button
          variant="contained"
          disabled={!profile || !profileReasonValid || profileMutation.isPending}
          onClick={() => profile && profileMutation.mutate(profile)}
        >
          Set profile
        </Button>
      </DialogActions>
    </Dialog>
  </>;
}
