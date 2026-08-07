import { useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import {
  Alert,
  AlertTitle,
  Box,
  Button,
  Chip,
  Dialog,
  DialogActions,
  DialogContent,
  DialogTitle,
  Divider,
  FormControlLabel,
  Grid,
  MenuItem,
  Paper,
  Switch,
  Tab,
  Tabs,
  TextField,
  Typography,
} from '@mui/material';
import {
  AssignmentIndOutlined as AssignIcon,
  PriorityHighOutlined as SeverityIcon,
  SendOutlined as SendIcon,
} from '@mui/icons-material';
import { useSnackbar } from 'notistack';
import Stack from './Flex';
import ReasonDialog from './ReasonDialog';
import RoleGate from './RoleGate';
import { ErrorState, LoadingState } from './States';
import { SoftChip } from './StatusChip';
import { fmtDateTime, fmtRelative } from './format';
import { platformApi } from '../api/client';
import { platformErrorMessage } from '../api/apiError';
import { platformKeys } from '../api/queryKeys';
import { usePlatformAuth } from '../auth/usePlatformAuth';
import { usePlatformPermissions } from '../auth/usePlatformPermissions';
import { REQUIRED_ROLE_COPY } from '../auth/permissions';
import { SUPPORT_TICKET_SEVERITIES } from '../types';
import type { SupportTicketSeverity } from '../types';

export const SEVERITY_TONE: Record<string, 'error' | 'warning' | 'info' | 'neutral'> = {
  Critical: 'error',
  High: 'warning',
  Normal: 'info',
  Low: 'neutral',
};

export const STATUS_TONE: Record<string, 'success' | 'warning' | 'info' | 'neutral' | 'error'> = {
  New: 'warning',
  Open: 'info',
  Pending: 'neutral',
  Resolved: 'success',
  Closed: 'neutral',
};

/** Statuses whose transition takes a resolution, mirroring the server's ticket contract. */
const RESOLUTION_STATUSES = ['Resolved', 'Closed'];

interface Props {
  ticketId: string | null;
  onClose: () => void;
}

/**
 * One ticket, everything an operator does to it. The transitions come from the server's
 * `allowedTransitions` rather than a graph hard-coded here, so this screen and the
 * lifecycle can never disagree about whether a Closed ticket can be reopened.
 */
export default function SupportTicketDialog({ ticketId, onClose }: Props) {
  const queryClient = useQueryClient();
  const { enqueueSnackbar } = useSnackbar();
  const { platformUser } = usePlatformAuth();
  const permissions = usePlatformPermissions();

  const [view, setView] = useState<'thread' | 'timeline'>('thread');
  const [note, setNote] = useState('');
  const [internal, setInternal] = useState(true);
  const [transitionTo, setTransitionTo] = useState<string | null>(null);
  const [resolution, setResolution] = useState('');
  const [severityTarget, setSeverityTarget] = useState<SupportTicketSeverity | null>(null);
  const [assignTarget, setAssignTarget] = useState<{ id: string | null; label: string } | null>(null);

  const ticketQuery = useQuery({
    queryKey: platformKeys.supportTicket(ticketId ?? ''),
    queryFn: () => platformApi.getSupportTicket(ticketId as string),
    enabled: Boolean(ticketId),
  });

  const timelineQuery = useQuery({
    queryKey: platformKeys.supportTicketTimeline(ticketId ?? ''),
    queryFn: () => platformApi.getSupportTicketTimeline(ticketId as string),
    enabled: Boolean(ticketId) && view === 'timeline',
  });

  // Only an Owner may enumerate platform operators, so the assignee picker is offered
  // only to them. Everyone with desk authority can still take a ticket or drop it, which
  // is the move a support engineer actually makes.
  const operatorsQuery = useQuery({
    queryKey: platformKeys.platformUsers(),
    queryFn: () => platformApi.listPlatformUsers(),
    enabled: Boolean(ticketId) && permissions.isOwner,
  });

  const ticket = ticketQuery.data;

  const invalidate = () => {
    queryClient.invalidateQueries({ queryKey: platformKeys.supportTicket(ticketId ?? '') });
    queryClient.invalidateQueries({ queryKey: platformKeys.supportTicketTimeline(ticketId ?? '') });
    queryClient.invalidateQueries({ queryKey: [...platformKeys.all, 'support'] });
    if (ticket) queryClient.invalidateQueries({ queryKey: platformKeys.tenantOperations(ticket.tenantId) });
  };

  const fail = (fallback: string) => (error: unknown) =>
    enqueueSnackbar(platformErrorMessage(error, fallback), { variant: 'error' });

  const noteMutation = useMutation({
    mutationFn: () =>
      platformApi.addSupportTicketNote(ticketId as string, {
        body: note.trim(),
        isInternal: internal,
        // The version the console was showing. A stale one is refused with 409 rather
        // than landing on top of somebody else's edit.
        expectedVersion: ticket?.version,
      }),
    onSuccess: () => {
      setNote('');
      invalidate();
    },
    onError: fail('The note was not added'),
  });

  const transitionMutation = useMutation({
    mutationFn: (reason: string) =>
      platformApi.transitionSupportTicket(ticketId as string, {
        status: transitionTo as string,
        reason,
        resolution: RESOLUTION_STATUSES.includes(transitionTo ?? '') ? resolution.trim() || null : null,
        expectedVersion: ticket?.version,
      }),
    onSuccess: () => {
      enqueueSnackbar(`Ticket moved to ${transitionTo}`, { variant: 'success' });
      setTransitionTo(null);
      setResolution('');
      invalidate();
    },
    onError: fail('The transition was refused'),
  });

  const severityMutation = useMutation({
    mutationFn: (reason: string) =>
      platformApi.changeSupportTicketSeverity(ticketId as string, {
        severity: severityTarget as SupportTicketSeverity,
        reason,
        expectedVersion: ticket?.version,
      }),
    onSuccess: () => {
      enqueueSnackbar(`Severity set to ${severityTarget}`, { variant: 'success' });
      setSeverityTarget(null);
      invalidate();
    },
    onError: fail('The severity change was refused'),
  });

  const assignMutation = useMutation({
    mutationFn: (reason: string) =>
      platformApi.assignSupportTicket(ticketId as string, {
        assignToPlatformUserId: assignTarget?.id ?? null,
        reason,
        expectedVersion: ticket?.version,
      }),
    onSuccess: () => {
      enqueueSnackbar(assignTarget?.id ? `Assigned to ${assignTarget.label}` : 'Ticket unassigned', {
        variant: 'success',
      });
      setAssignTarget(null);
      invalidate();
    },
    onError: fail('The assignment was refused'),
  });

  const canWork = permissions.canAdministerTenants;
  const ownId = platformUser?.id ?? null;

  return (
    <>
      <Dialog open={Boolean(ticketId)} onClose={onClose} fullWidth maxWidth="lg">
        <DialogTitle sx={{ fontWeight: 800, pr: 6 }}>
          {ticket ? `#${ticket.id} · ${ticket.subject}` : 'Support ticket'}
          {ticket && (
            <Stack direction="row" spacing={1} sx={{ mt: 1, flexWrap: 'wrap' }}>
              <SoftChip label={ticket.status} tone={STATUS_TONE[ticket.status] ?? 'neutral'} />
              <SoftChip label={ticket.severity} tone={SEVERITY_TONE[ticket.severity] ?? 'neutral'} dot={false} />
              {ticket.tenantName && (
                <Chip size="small" label={`${ticket.tenantName} · ${ticket.tenantStatus ?? '—'}`} sx={{ fontWeight: 700 }} />
              )}
            </Stack>
          )}
        </DialogTitle>

        <DialogContent dividers>
          {ticketQuery.isLoading ? (
            <LoadingState label="Loading ticket…" minHeight={240} />
          ) : ticketQuery.isError || !ticket ? (
            <ErrorState
              message={platformErrorMessage(ticketQuery.error, 'The ticket could not be loaded.')}
              onRetry={() => ticketQuery.refetch()}
              minHeight={240}
            />
          ) : (
            <Grid container spacing={2.5}>
              <Grid size={{ xs: 12, md: 8 }}>
                {ticket.isRedacted && (
                  <Alert severity="warning" sx={{ borderRadius: 2, mb: 2 }}>
                    <AlertTitle sx={{ fontWeight: 800}}>This thread was redacted</AlertTitle>
                    {ticket.redactedReason ?? 'A purge erased the customer content from this ticket.'}
                  </Alert>
                )}

                <Tabs value={view} onChange={(_event, next) => setView(next)} sx={{ mb: 1.5 }}>
                  <Tab value="thread" label={`Thread (${ticket.notes.length})`} />
                  {/* The merged view: notes plus the privileged actions taken against this
                      ticket, read back out of the audit log rather than stored twice. */}
                  <Tab value="timeline" label="Timeline" />
                </Tabs>

                {view === 'thread' ? (
                  <Stack spacing={1.5}>
                    <Paper variant="outlined" sx={{ p: 2, borderRadius: 2 }}>
                      <Typography variant="caption" color="text.secondary">
                        Opened {fmtDateTime(ticket.createdAtUtc)} by {ticket.openedByEmail ?? 'unknown'}
                        {ticket.requesterEmail ? ` for ${ticket.requesterEmail}` : ''}
                      </Typography>
                      <Typography variant="body2" sx={{ mt: 1, whiteSpace: 'pre-wrap' }}>
                        {ticket.body ?? '—'}
                      </Typography>
                    </Paper>

                    {ticket.notes.map((entry) => (
                      <Paper
                        key={entry.id}
                        variant="outlined"
                        sx={{
                          p: 2,
                          borderRadius: 2,
                          // Internal notes are visually distinct because the difference
                          // between "the customer may read this" and "they may not" must
                          // never be a detail somebody has to remember.
                          borderColor: entry.isInternal ? 'divider' : 'primary.main',
                          bgcolor: entry.isInternal ? 'transparent' : 'action.hover',
                        }}
                      >
                        <Stack direction="row" alignItems="center" spacing={1} sx={{ mb: 0.5 }}>
                          <Typography variant="body2" sx={{ fontWeight: 700 }}>
                            {entry.authorLabel}
                          </Typography>
                          <SoftChip
                            label={entry.isInternal ? 'internal' : 'customer-visible'}
                            tone={entry.isInternal ? 'neutral' : 'info'}
                            dot={false}
                          />
                          <Box sx={{ flex: 1 }} />
                          <Typography variant="caption" color="text.secondary">
                            {fmtRelative(entry.createdAtUtc)}
                          </Typography>
                        </Stack>
                        <Typography variant="body2" sx={{ whiteSpace: 'pre-wrap' }}>
                          {entry.body}
                        </Typography>
                      </Paper>
                    ))}

                    <Divider />

                    <TextField
                      fullWidth
                      multiline
                      minRows={3}
                      label="Add a note"
                      value={note}
                      onChange={(event) => setNote(event.target.value)}
                      disabled={!canWork}
                      helperText={canWork ? undefined : REQUIRED_ROLE_COPY.tenantAdmin}
                    />
                    <Stack direction="row" alignItems="center" spacing={2}>
                      <FormControlLabel
                        control={
                          <Switch checked={internal} onChange={(event) => setInternal(event.target.checked)} disabled={!canWork} />
                        }
                        label={internal ? 'Internal note' : 'Customer-visible note'}
                      />
                      <Box sx={{ flex: 1 }} />
                      <RoleGate allowed={canWork} requirement={REQUIRED_ROLE_COPY.tenantAdmin}>
                        {(disabled) => (
                          <Button
                            variant="contained"
                            startIcon={<SendIcon />}
                            disabled={disabled || note.trim().length === 0 || noteMutation.isPending}
                            onClick={() => noteMutation.mutate()}
                            sx={{ fontWeight: 700 }}
                          >
                            {noteMutation.isPending ? 'Adding…' : 'Add note'}
                          </Button>
                        )}
                      </RoleGate>
                    </Stack>
                  </Stack>
                ) : timelineQuery.isLoading ? (
                  <LoadingState label="Merging the thread and the audit trail…" minHeight={200} />
                ) : (
                  <Stack spacing={1}>
                    {(timelineQuery.data?.entries ?? []).map((entry) => (
                      <Stack key={`${entry.kind}-${entry.id}`} direction="row" spacing={1.5} sx={{ py: 0.75 }}>
                        <SoftChip label={entry.kind} tone={entry.kind === 'audit' ? 'info' : 'neutral'} dot={false} />
                        <Box sx={{ flex: 1, minWidth: 0 }}>
                          <Typography variant="body2" sx={{ fontWeight: 700 }}>
                            <Box component="code">{entry.action}</Box>
                            {entry.result && entry.result !== 'success' ? ` — ${entry.result}` : ''}
                          </Typography>
                          {entry.body && (
                            <Typography variant="caption" color="text.secondary" sx={{ whiteSpace: 'pre-wrap' }}>
                              {entry.body}
                            </Typography>
                          )}
                        </Box>
                        <Typography variant="caption" color="text.secondary" sx={{ whiteSpace: 'nowrap' }}>
                          {entry.actor ?? '—'} · {fmtDateTime(entry.occurredAtUtc)}
                        </Typography>
                      </Stack>
                    ))}
                  </Stack>
                )}
              </Grid>

              <Grid size={{ xs: 12, md: 4 }}>
                <Stack spacing={2}>
                  <Paper variant="outlined" sx={{ p: 2, borderRadius: 2 }}>
                    <Typography variant="overline" sx={{ fontWeight: 800 }}>
                      Move this ticket
                    </Typography>
                    <Stack spacing={1} sx={{ mt: 1 }}>
                      {ticket.allowedTransitions.length === 0 ? (
                        <Typography variant="caption" color="text.secondary">
                          No transitions are permitted from {ticket.status}.
                        </Typography>
                      ) : (
                        ticket.allowedTransitions.map((status) => (
                          <RoleGate key={status} allowed={canWork} requirement={REQUIRED_ROLE_COPY.tenantAdmin}>
                            {(disabled) => (
                              <Button
                                variant="outlined"
                                size="small"
                                disabled={disabled}
                                onClick={() => {
                                  setResolution(ticket.resolution ?? '');
                                  setTransitionTo(status);
                                }}
                                sx={{ fontWeight: 700, justifyContent: 'flex-start' }}
                              >
                                {status}
                              </Button>
                            )}
                          </RoleGate>
                        ))
                      )}
                    </Stack>
                  </Paper>

                  <Paper variant="outlined" sx={{ p: 2, borderRadius: 2 }}>
                    <Typography variant="overline" sx={{ fontWeight: 800 }}>
                      Assignment
                    </Typography>
                    <Typography variant="body2" sx={{ fontWeight: 700, mt: 0.5 }}>
                      {ticket.assignedToEmail ?? 'Unassigned'}
                    </Typography>
                    <Stack spacing={1} sx={{ mt: 1.5 }}>
                      {permissions.isOwner ? (
                        <TextField
                          select
                          size="small"
                          label="Assign to"
                          value={ticket.assignedToPlatformUserId ?? ''}
                          onChange={(event) => {
                            const id = event.target.value || null;
                            const match = (operatorsQuery.data ?? []).find((operator) => operator.id === id);
                            setAssignTarget({ id, label: match?.email ?? 'nobody' });
                          }}
                        >
                          <MenuItem value="">Unassigned</MenuItem>
                          {(operatorsQuery.data ?? [])
                            .filter((operator) => operator.isActive)
                            .map((operator) => (
                              <MenuItem key={operator.id} value={operator.id}>
                                {operator.email}
                              </MenuItem>
                            ))}
                        </TextField>
                      ) : (
                        <RoleGate allowed={canWork && Boolean(ownId)} requirement={REQUIRED_ROLE_COPY.tenantAdmin}>
                          {(disabled) => (
                            <Button
                              size="small"
                              variant="outlined"
                              startIcon={<AssignIcon />}
                              disabled={disabled || ticket.assignedToPlatformUserId === ownId}
                              onClick={() =>
                                setAssignTarget({ id: ownId, label: platformUser?.email ?? 'me' })
                              }
                              sx={{ fontWeight: 700 }}
                            >
                              Assign to me
                            </Button>
                          )}
                        </RoleGate>
                      )}
                      {ticket.assignedToPlatformUserId && (
                        <RoleGate allowed={canWork} requirement={REQUIRED_ROLE_COPY.tenantAdmin}>
                          {(disabled) => (
                            <Button
                              size="small"
                              color="inherit"
                              disabled={disabled}
                              onClick={() => setAssignTarget({ id: null, label: 'nobody' })}
                              sx={{ fontWeight: 700 }}
                            >
                              Unassign
                            </Button>
                          )}
                        </RoleGate>
                      )}
                    </Stack>
                  </Paper>

                  <Paper variant="outlined" sx={{ p: 2, borderRadius: 2 }}>
                    <Typography variant="overline" sx={{ fontWeight: 800 }}>
                      Severity
                    </Typography>
                    <Stack direction="row" spacing={0.5} sx={{ mt: 1, flexWrap: 'wrap', gap: 0.5 }}>
                      {SUPPORT_TICKET_SEVERITIES.map((severity) => (
                        <RoleGate key={severity} allowed={canWork} requirement={REQUIRED_ROLE_COPY.tenantAdmin}>
                          {(disabled) => (
                            <Button
                              size="small"
                              variant={ticket.severity === severity ? 'contained' : 'outlined'}
                              startIcon={severity === 'Critical' ? <SeverityIcon fontSize="small" /> : undefined}
                              disabled={disabled || ticket.severity === severity}
                              onClick={() => setSeverityTarget(severity)}
                              sx={{ fontWeight: 700 }}
                            >
                              {severity}
                            </Button>
                          )}
                        </RoleGate>
                      ))}
                    </Stack>
                  </Paper>

                  <Paper variant="outlined" sx={{ p: 2, borderRadius: 2 }}>
                    <Typography variant="overline" sx={{ fontWeight: 800 }}>
                      Linked evidence
                    </Typography>
                    {ticket.links.length === 0 ? (
                      <Typography variant="caption" color="text.secondary" sx={{ display: 'block', mt: 0.5 }}>
                        Nothing linked. An impersonation session with no ticket behind it is an operator who
                        entered a customer's account without recording why.
                      </Typography>
                    ) : (
                      <Stack spacing={1} sx={{ mt: 1 }}>
                        {ticket.links.map((link) => (
                          <Box key={link.id}>
                            <Typography variant="caption" sx={{ fontWeight: 700 }}>
                              {link.kind}
                            </Typography>
                            <Typography variant="caption" color="text.secondary" sx={{ display: 'block' }}>
                              {link.targetSummary ?? `${link.targetKey} — no longer resolves`}
                            </Typography>
                          </Box>
                        ))}
                      </Stack>
                    )}
                  </Paper>

                  <Box>
                    <Typography variant="caption" color="text.secondary" sx={{ display: 'block' }}>
                      Updated {fmtRelative(ticket.updatedAtUtc)} · version {ticket.version}
                    </Typography>
                    {ticket.resolution && (
                      <Typography variant="caption" color="text.secondary" sx={{ display: 'block', mt: 0.5 }}>
                        Resolution: {ticket.resolution}
                      </Typography>
                    )}
                  </Box>
                </Stack>
              </Grid>
            </Grid>
          )}
        </DialogContent>
        <DialogActions sx={{ p: 2 }}>
          <Button onClick={onClose} sx={{ fontWeight: 700 }}>
            Close
          </Button>
        </DialogActions>
      </Dialog>

      <ReasonDialog
        open={Boolean(transitionTo)}
        title={`Move ticket to ${transitionTo}`}
        confirmLabel={`Move to ${transitionTo}`}
        minReasonLength={1}
        description={
          <>
            A status change with no stated reason is an audit record that answers "what" and refuses to answer
            "why", which is the only question anybody asks it later.
          </>
        }
        extra={
          RESOLUTION_STATUSES.includes(transitionTo ?? '') ? (
            <TextField
              fullWidth
              multiline
              minRows={2}
              label="Resolution"
              value={resolution}
              onChange={(event) => setResolution(event.target.value)}
              helperText="Recorded on the ticket and echoed into the thread."
            />
          ) : undefined
        }
        busy={transitionMutation.isPending}
        onClose={() => setTransitionTo(null)}
        onConfirm={(reason) => transitionMutation.mutate(reason)}
      />

      <ReasonDialog
        open={Boolean(severityTarget)}
        title={`Set severity to ${severityTarget}`}
        confirmLabel="Change severity"
        minReasonLength={1}
        description={
          <>
            Severity decides who gets paged and what gets promised, so a silent downgrade is the change most
            worth being able to reconstruct.
          </>
        }
        busy={severityMutation.isPending}
        onClose={() => setSeverityTarget(null)}
        onConfirm={(reason) => severityMutation.mutate(reason)}
      />

      <ReasonDialog
        open={Boolean(assignTarget)}
        title={assignTarget?.id ? `Assign to ${assignTarget.label}` : 'Unassign this ticket'}
        confirmLabel={assignTarget?.id ? 'Assign' : 'Unassign'}
        minReasonLength={1}
        reasonLabel="Reason"
        description={
          assignTarget?.id ? (
            <>Moves ownership of this ticket. The change is audited.</>
          ) : (
            <>Returns the ticket to the unassigned queue. This is a real triage move, not a mistake.</>
          )
        }
        busy={assignMutation.isPending}
        onClose={() => setAssignTarget(null)}
        onConfirm={(reason) => assignMutation.mutate(reason)}
      />
    </>
  );
}
