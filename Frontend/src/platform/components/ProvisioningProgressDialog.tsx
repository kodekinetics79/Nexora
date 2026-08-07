import { useMemo, useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import {
  Alert,
  AlertTitle,
  Box,
  Button,
  Chip,
  CircularProgress,
  Collapse,
  Dialog,
  DialogActions,
  DialogContent,
  DialogTitle,
  Divider,
  LinearProgress,
  Paper,
  Typography,
} from '@mui/material';
import {
  Block as CancelledIcon,
  CheckCircle as SucceededIcon,
  ErrorOutlined as FailedIcon,
  ExpandLess,
  ExpandMore,
  RadioButtonUnchecked as PendingIcon,
  RemoveCircleOutlined as SkippedIcon,
  Replay as RetryIcon,
  StopCircleOutlined as CancelIcon,
} from '@mui/icons-material';
import { useSnackbar } from 'notistack';
import Stack from './Flex';
import ReasonDialog from './ReasonDialog';
import RoleGate from './RoleGate';
import { platformApi } from '../api/client';
import { platformErrorMessage } from '../api/apiError';
import { platformKeys } from '../api/queryKeys';
import { usePlatformPermissions } from '../auth/usePlatformPermissions';
import { REQUIRED_ROLE_COPY } from '../auth/permissions';
import { LoadingState } from './States';
import { SoftChip } from './StatusChip';
import { fmtDateTime } from './format';
import type {
  ProvisioningExecution,
  ProvisioningStep,
  ProvisioningStepStatus,
  SubmitProvisioningResult,
} from '../types';

/**
 * How often the console asks. Two seconds is fast enough that a step which takes one
 * second is still seen happening, and slow enough that a stuck execution left open on a
 * forgotten tab is not a load-generator.
 */
const POLL_MS = 2000;

const RUNNING_STATES: ProvisioningExecution['state'][] = ['Pending', 'Running'];

const STEP_TONE: Record<ProvisioningStepStatus, 'success' | 'error' | 'info' | 'warning' | 'neutral'> = {
  Pending: 'neutral',
  Running: 'info',
  Succeeded: 'success',
  Failed: 'error',
  Skipped: 'neutral',
  Cancelled: 'warning',
};

const fmtDuration = (ms: number | null): string => {
  if (ms == null) return '—';
  if (ms < 1000) return `${ms}ms`;
  return `${(ms / 1000).toFixed(1)}s`;
};

/**
 * The generated initial credential. It exists in the submit response and nowhere else —
 * only a BCrypt hash is stored — so this panel is the single moment it can be handed over,
 * and it says so rather than letting the operator assume they can come back for it.
 */
function OneTimeSecretPanel({ password, adminEmail }: { password: string; adminEmail: string }) {
  const [notice, setNotice] = useState<string | null>(null);
  const [copied, setCopied] = useState(false);

  const copy = () => {
    if (!navigator.clipboard?.writeText) {
      setNotice('Copying is unavailable in this browser — select the password and copy it manually.');
      return;
    }
    navigator.clipboard
      .writeText(password)
      .then(() => {
        setCopied(true);
        setNotice('Password copied to the clipboard.');
      })
      .catch(() => setNotice('Copy failed — select the password and copy it manually.'));
  };

  return (
    <Alert severity="warning" sx={{ borderRadius: 2 }}>
      <AlertTitle sx={{ fontWeight: 800 }}>One-time password for {adminEmail}</AlertTitle>
      <Typography variant="body2" sx={{ mb: 1 }}>
        Shown here and nowhere else, ever. Only a hash is stored — if this is lost before handover
        the credential has to be reset, not looked up.
      </Typography>
      <Stack direction="row" alignItems="center" spacing={1}>
        <Box
          component="code"
          sx={{ px: 1.5, py: 0.75, borderRadius: 1.5, bgcolor: 'action.hover', fontWeight: 700, wordBreak: 'break-all' }}
        >
          {password}
        </Box>
        <Button size="small" onClick={copy} sx={{ fontWeight: 700 }}>
          {copied ? 'Copied' : 'Copy'}
        </Button>
      </Stack>
      {/* Announced as well as shown: a copy button that gives no feedback is a copy button
          an operator presses three times and still does not trust. */}
      <Box role="status" aria-live="polite" sx={{ minHeight: 20, mt: 0.5 }}>
        {notice && (
          <Typography variant="caption" sx={{ fontWeight: 700 }}>
            {notice}
          </Typography>
        )}
      </Box>
    </Alert>
  );
}

function StepIcon({ status }: { status: ProvisioningStepStatus }) {
  if (status === 'Running') return <CircularProgress size={18} thickness={5} />;
  if (status === 'Succeeded') return <SucceededIcon fontSize="small" color="success" />;
  if (status === 'Failed') return <FailedIcon fontSize="small" color="error" />;
  if (status === 'Skipped') return <SkippedIcon fontSize="small" sx={{ color: 'text.disabled' }} />;
  if (status === 'Cancelled') return <CancelledIcon fontSize="small" color="warning" />;
  return <PendingIcon fontSize="small" sx={{ color: 'text.disabled' }} />;
}

interface Props {
  /** Null closes the dialog. Set by the wizard the moment the submit is accepted. */
  executionId: string | null;
  /** The submit response, when this dialog opened from one. Carries the one-time secrets. */
  submission?: SubmitProvisioningResult | null;
  onClose: () => void;
}

/**
 * Watches one durable provisioning attempt and lets an operator repair it.
 *
 * <p>The whole reason this screen exists is that provisioning commits eight separate
 * pieces of a customer's workspace, and a failure at step six leaves five of them
 * standing. A spinner cannot say which; this can.</p>
 */
export default function ProvisioningProgressDialog({ executionId, submission, onClose }: Props) {
  const queryClient = useQueryClient();
  const { enqueueSnackbar } = useSnackbar();
  const permissions = usePlatformPermissions();

  const [expanded, setExpanded] = useState<string | null>(null);
  const [retryTarget, setRetryTarget] = useState<{ step: string | null; label: string } | null>(null);
  const [cancelOpen, setCancelOpen] = useState(false);

  const { data: execution, isLoading } = useQuery({
    queryKey: platformKeys.provisioningExecution(executionId ?? ''),
    queryFn: () => platformApi.getProvisioningExecution(executionId as string),
    enabled: Boolean(executionId),
    // Polling stops the moment the execution reaches a resting state, so a finished
    // attempt left open on screen costs nothing.
    refetchInterval: (query) =>
      RUNNING_STATES.includes(query.state.data?.state ?? 'Pending') ? POLL_MS : false,
    initialData: submission?.execution,
  });

  const settle = () => {
    queryClient.invalidateQueries({ queryKey: platformKeys.provisioningExecution(executionId ?? '') });
    queryClient.invalidateQueries({ queryKey: platformKeys.tenants() });
    queryClient.invalidateQueries({ queryKey: platformKeys.overview() });
  };

  const retryMutation = useMutation({
    mutationFn: ({ step, reason }: { step: string | null; reason: string }) =>
      platformApi.retryProvisioning(executionId as string, { step, reason }),
    onSuccess: () => {
      enqueueSnackbar('Provisioning resumed', { variant: 'success' });
      setRetryTarget(null);
      settle();
    },
    onError: (error) => enqueueSnackbar(platformErrorMessage(error, 'Retry was refused'), { variant: 'error' }),
  });

  const cancelMutation = useMutation({
    mutationFn: (reason: string) => platformApi.cancelProvisioning(executionId as string, reason),
    onSuccess: () => {
      enqueueSnackbar('Provisioning attempt cancelled', { variant: 'warning' });
      setCancelOpen(false);
      settle();
    },
    onError: (error) => enqueueSnackbar(platformErrorMessage(error, 'Cancel was refused'), { variant: 'error' }),
  });

  const progress = useMemo(() => {
    if (!execution || execution.totalStepCount === 0) return 0;
    return Math.round((execution.completedStepCount / execution.totalStepCount) * 100);
  }, [execution]);

  const failedStep: ProvisioningStep | undefined = execution?.steps.find(
    (step) => step.step === execution.failedStep,
  );
  const inFlight = execution ? RUNNING_STATES.includes(execution.state) : false;
  const orphans = execution
    ? [
        execution.tenantId ? `tenant ${execution.tenantId}` : null,
        execution.provisionedBusinessUnitId ? `business unit ${execution.provisionedBusinessUnitId}` : null,
        execution.foundingUserId ? `user ${execution.foundingUserId}` : null,
      ].filter((entry): entry is string => entry !== null)
    : [];

  return (
    <>
      <Dialog
        open={Boolean(executionId)}
        onClose={() => (inFlight ? undefined : onClose())}
        fullWidth
        maxWidth="md"
        aria-labelledby="provisioning-progress-title"
      >
        <DialogTitle id="provisioning-progress-title" sx={{ fontWeight: 800 }}>
          Provisioning {execution?.name ?? 'workspace'}
          <Typography variant="body2" color="text.secondary" sx={{ fontWeight: 500, mt: 0.5 }}>
            Eight steps, committed one at a time. You can close this and come back — the attempt is
            durable and keeps running.
          </Typography>
        </DialogTitle>

        <DialogContent dividers>
          {isLoading || !execution ? (
            <LoadingState label="Reading the provisioning attempt…" minHeight={220} />
          ) : (
            <Stack spacing={2}>
              <Stack direction="row" alignItems="center" spacing={1.5} sx={{ flexWrap: 'wrap' }}>
                <SoftChip
                  label={execution.state}
                  tone={
                    execution.state === 'Succeeded'
                      ? 'success'
                      : execution.state === 'Failed'
                        ? 'error'
                        : execution.state === 'Cancelled'
                          ? 'warning'
                          : 'info'
                  }
                />
                <Typography variant="body2" color="text.secondary">
                  {execution.completedStepCount} of {execution.totalStepCount} steps
                </Typography>
                <Box sx={{ flex: 1 }} />
                {/* The correlation id is the join key between this screen, the audit log and
                    the server logs. It is shown because a support engineer will need it. */}
                <Typography variant="caption" sx={{ fontFamily: 'monospace' }} color="text.secondary">
                  {execution.correlationId}
                </Typography>
              </Stack>

              <LinearProgress
                variant={inFlight && progress === 0 ? 'indeterminate' : 'determinate'}
                value={progress}
                aria-label="Provisioning progress"
                sx={{ height: 8, borderRadius: 4 }}
              />

              {execution.state === 'Failed' && (
                <Alert severity="error" sx={{ borderRadius: 2 }}>
                  <AlertTitle sx={{ fontWeight: 800 }}>
                    Stopped at {failedStep?.label ?? execution.failedStep ?? 'an unknown step'}
                  </AlertTitle>
                  {execution.failureReason ?? 'The server did not report a reason.'}
                  {execution.failureIsTerminal && (
                    <Typography variant="body2" sx={{ mt: 1, fontWeight: 700 }}>
                      Retrying will not help — the address or an input now belongs to something else.
                      Cancel this attempt and resubmit with different details.
                    </Typography>
                  )}
                </Alert>
              )}

              {execution.state === 'Cancelled' && (
                <Alert severity="warning" sx={{ borderRadius: 2 }}>
                  <AlertTitle sx={{ fontWeight: 800 }}>Attempt cancelled</AlertTitle>
                  {execution.cancellationReason ?? 'No reason recorded.'}
                  {orphans.length > 0 && (
                    <Typography variant="body2" sx={{ mt: 1 }}>
                      Cancelling did not undo work already committed. Still standing: {orphans.join(', ')}.
                      Removing them is a separate, destructive operation on the tenant's lifecycle tab.
                    </Typography>
                  )}
                </Alert>
              )}

              {execution.state === 'Succeeded' && (
                <Alert severity="success" sx={{ borderRadius: 2 }}>
                  <AlertTitle sx={{ fontWeight: 800 }}>Workspace ready</AlertTitle>
                  Every step committed. {execution.name} is active and {execution.adminEmail} can sign in.
                </Alert>
              )}

              {submission?.generatedPassword && (
                <OneTimeSecretPanel
                  password={submission.generatedPassword}
                  adminEmail={execution.adminEmail}
                />
              )}
              {submission?.secretNotice && (
                <Alert severity="info" sx={{ borderRadius: 2 }}>
                  {submission.secretNotice}
                </Alert>
              )}
              {submission && !submission.created && (
                <Alert severity="info" sx={{ borderRadius: 2 }}>
                  This request had already been accepted under the same idempotency key, so you are
                  looking at the original attempt rather than a second one.
                </Alert>
              )}

              <Divider />

              <Stack spacing={1}>
                {execution.steps.map((step) => {
                  const open = expanded === step.step;
                  const isFailed = step.status === 'Failed';
                  return (
                    <Paper key={step.step} variant="outlined" sx={{ borderRadius: 2, p: 1.5 }}>
                      <Stack direction="row" alignItems="center" spacing={1.5}>
                        <Box sx={{ display: 'flex', width: 22, justifyContent: 'center' }}>
                          <StepIcon status={step.status} />
                        </Box>
                        <Box sx={{ flex: 1, minWidth: 0 }}>
                          <Typography variant="body2" sx={{ fontWeight: 700 }}>
                            {step.ordinal}. {step.label}
                          </Typography>
                          <Typography variant="caption" color="text.secondary" component="code">
                            {step.step}
                          </Typography>
                        </Box>
                        {step.attemptCount > 1 && (
                          <Chip
                            size="small"
                            label={`${step.attemptCount} attempts`}
                            color="warning"
                            variant="outlined"
                            sx={{ fontWeight: 700 }}
                          />
                        )}
                        <Typography variant="caption" color="text.secondary" sx={{ minWidth: 56, textAlign: 'right' }}>
                          {fmtDuration(step.durationMs)}
                        </Typography>
                        <SoftChip label={step.status} tone={STEP_TONE[step.status]} dot={false} />
                        {(step.detail || step.failureReason) && (
                          <Button
                            size="small"
                            color="inherit"
                            onClick={() => setExpanded(open ? null : step.step)}
                            endIcon={open ? <ExpandLess fontSize="small" /> : <ExpandMore fontSize="small" />}
                            aria-expanded={open}
                            sx={{ fontWeight: 700 }}
                          >
                            Detail
                          </Button>
                        )}
                        {isFailed && (
                          <RoleGate
                            allowed={permissions.canAdministerTenants}
                            requirement={REQUIRED_ROLE_COPY.tenantAdmin}
                          >
                            {(disabled) => (
                              <Button
                                size="small"
                                startIcon={<RetryIcon fontSize="small" />}
                                // A step that would duplicate rather than repair is never
                                // offered: the server says which, and the console obeys it.
                                disabled={disabled || !step.isRetriable || execution.failureIsTerminal}
                                onClick={() => setRetryTarget({ step: step.step, label: step.label })}
                                sx={{ fontWeight: 700 }}
                              >
                                Retry
                              </Button>
                            )}
                          </RoleGate>
                        )}
                      </Stack>

                      <Collapse in={open} unmountOnExit>
                        <Box sx={{ mt: 1.5, pl: 4.5 }}>
                          {step.failureReason && (
                            <Typography variant="body2" color="error.main" sx={{ mb: 1 }}>
                              {step.failureCode ? `${step.failureCode}: ` : ''}
                              {step.failureReason}
                            </Typography>
                          )}
                          {step.startedOn && (
                            <Typography variant="caption" color="text.secondary" sx={{ display: 'block' }}>
                              Started {fmtDateTime(step.startedOn)}
                              {step.completedOn ? ` · finished ${fmtDateTime(step.completedOn)}` : ''}
                            </Typography>
                          )}
                          {step.detail && (
                            <Box
                              component="pre"
                              sx={{
                                mt: 1,
                                p: 1.5,
                                m: 0,
                                borderRadius: 2,
                                bgcolor: 'action.hover',
                                fontSize: 12,
                                overflowX: 'auto',
                              }}
                            >
                              {step.detail}
                            </Box>
                          )}
                        </Box>
                      </Collapse>
                    </Paper>
                  );
                })}
              </Stack>
            </Stack>
          )}
        </DialogContent>

        <DialogActions sx={{ p: 2, gap: 1 }}>
          {execution && execution.state !== 'Succeeded' && execution.state !== 'Cancelled' && (
            <RoleGate allowed={permissions.canAdministerTenants} requirement={REQUIRED_ROLE_COPY.tenantAdmin}>
              {(disabled) => (
                <Button
                  color="error"
                  startIcon={<CancelIcon />}
                  disabled={disabled}
                  onClick={() => setCancelOpen(true)}
                  sx={{ fontWeight: 700 }}
                >
                  Cancel attempt
                </Button>
              )}
            </RoleGate>
          )}
          {execution?.state === 'Failed' && !execution.failureIsTerminal && (
            <RoleGate allowed={permissions.canAdministerTenants} requirement={REQUIRED_ROLE_COPY.tenantAdmin}>
              {(disabled) => (
                <Button
                  startIcon={<RetryIcon />}
                  disabled={disabled}
                  onClick={() => setRetryTarget({ step: null, label: 'the first unfinished step' })}
                  sx={{ fontWeight: 700 }}
                >
                  Resume from failure
                </Button>
              )}
            </RoleGate>
          )}
          <Box sx={{ flex: 1 }} />
          <Button onClick={onClose} variant={execution?.state === 'Succeeded' ? 'contained' : 'text'} sx={{ fontWeight: 700 }}>
            Close
          </Button>
        </DialogActions>
      </Dialog>

      <ReasonDialog
        open={Boolean(retryTarget)}
        title={retryTarget?.step ? `Retry ${retryTarget.label}` : 'Resume provisioning'}
        description={
          retryTarget?.step ? (
            <>
              Re-runs <strong>{retryTarget.label}</strong> and then continues through the remaining
              steps. A retry re-executes privileged writes against this customer's data.
            </>
          ) : (
            <>
              Picks up at the first step that has not succeeded and carries on. Steps that already
              committed are not re-run.
            </>
          )
        }
        confirmLabel={retryTarget?.step ? 'Retry step' : 'Resume'}
        busy={retryMutation.isPending}
        onClose={() => setRetryTarget(null)}
        onConfirm={(reason) => retryMutation.mutate({ step: retryTarget?.step ?? null, reason })}
      />

      <ReasonDialog
        open={cancelOpen}
        title="Cancel this provisioning attempt"
        confirmLabel="Cancel attempt"
        confirmColor="error"
        description={
          <>
            {/* The API is explicit that cancellation is not a rollback, and so is this
                screen — quietly deleting a half-built tenant under a "cancel" button would
                be the most dangerous thing in the console. */}
            <strong>This does not undo work that has already been committed.</strong> Nothing further
            will run, but everything earlier steps created stays exactly where it is.
            {orphans.length > 0 ? (
              <> Left standing: {orphans.join(', ')} — in Provisioning status.</>
            ) : (
              <> Nothing has been committed yet, so nothing will be left behind.</>
            )}{' '}
            Removing a partially built tenant is a separate destructive operation with its own review.
          </>
        }
        busy={cancelMutation.isPending}
        onClose={() => setCancelOpen(false)}
        onConfirm={(reason) => cancelMutation.mutate(reason)}
      />
    </>
  );
}
