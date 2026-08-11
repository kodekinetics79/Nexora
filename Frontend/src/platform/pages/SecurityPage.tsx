import { useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Link as RouterLink } from 'react-router-dom';
import {
  Box,
  Button,
  Dialog,
  DialogActions,
  DialogContent,
  DialogContentText,
  DialogTitle,
  Paper,
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableRow,
  Typography,
} from '@mui/material';
import { BlockOutlined as RevokeIcon } from '@mui/icons-material';
import { useSnackbar } from 'notistack';
import { platformApi } from '../api/client';
import { platformErrorMessage } from '../api/apiError';
import { platformKeys } from '../api/queryKeys';
import type { AuditEntry, ImpersonationSession } from '../types';
import PageHeader from '../components/PageHeader';
import RoleGate from '../components/RoleGate';
import { usePlatformPermissions } from '../auth/usePlatformPermissions';
import { REQUIRED_ROLE_COPY } from '../auth/permissions';
import { SoftChip } from '../components/StatusChip';
import { EmptyState, ErrorState, LoadingState } from '../components/States';
import { fmtDateTime } from '../components/format';
import PlatformMfaPanel from '../components/PlatformMfaPanel';

const SESSION_TONE: Record<ImpersonationSession['status'], 'success' | 'neutral' | 'error'> = {
  active: 'success',
  expired: 'neutral',
  revoked: 'error',
};

const FAILED_LOGIN_ACTION = 'platform.login.failed';

export default function SecurityPage() {
  const queryClient = useQueryClient();
  const permissions = usePlatformPermissions();
  const { enqueueSnackbar } = useSnackbar();
  const [revokeTarget, setRevokeTarget] = useState<ImpersonationSession | null>(null);

  const sessionsQuery = useQuery({
    queryKey: platformKeys.impersonationSessions(),
    queryFn: () => platformApi.listImpersonationSessions(),
  });

  const failedLoginsQuery = useQuery({
    queryKey: platformKeys.audit({ action: FAILED_LOGIN_ACTION }),
    queryFn: () => platformApi.listAudit({ action: FAILED_LOGIN_ACTION }),
  });

  const failuresQuery = useQuery({
    queryKey: platformKeys.audit({ result: 'failure' }),
    queryFn: () => platformApi.listAudit({ result: 'failure' }),
  });

  const revokeMutation = useMutation({
    mutationFn: (session: ImpersonationSession) => platformApi.revokeImpersonation(session.jti),
    onSuccess: (session) => {
      enqueueSnackbar(`Impersonation session ${session.jti.slice(0, 8)}… revoked`, { variant: 'success' });
      setRevokeTarget(null);
      queryClient.invalidateQueries({ queryKey: platformKeys.impersonationSessions() });
    },
    onError: (error) =>
      enqueueSnackbar(platformErrorMessage(error, 'Revocation failed'), { variant: 'error' }),
  });

  const sessions = sessionsQuery.data ?? [];

  return (
    <Box>
      <PageHeader
        title="Security"
        subtitle="Impersonation sessions and platform-plane failure evidence."
      />

      {/* The plane-wide policy lives on its own screen; this panel is the operator's OWN second
          factor. Two different things that both say "MFA", so the link says which is which. */}
      <Paper sx={{ p: 2.5, borderRadius: 3, mb: 2.5 }}>
        <Typography variant="h6" sx={{ fontWeight: 800, mb: 0.5 }}>Platform Authentication</Typography>
        <Typography variant="body2" color="text.secondary" sx={{ mb: 1.5 }}>
          Whether a second factor is enforced for every operator on this deployment, who last changed
          it, and when a relaxation expires. Owner only.
        </Typography>
        <Button component={RouterLink} to="/platform/security/authentication" variant="outlined" size="small">
          Open Platform Authentication
        </Button>
      </Paper>

      <PlatformMfaPanel />

      {/* Impersonation sessions */}
      <Paper sx={{ p: 3, borderRadius: 3, mb: 2.5 }}>
        <Typography variant="h6" sx={{ fontWeight: 800, mb: 0.5 }}>Impersonation Sessions</Typography>
        <Typography variant="caption" color="text.secondary">
          Active sessions plus the last 7 days. Revoking cuts a live token off immediately.
        </Typography>
        {sessionsQuery.isLoading ? (
          <LoadingState label="Loading sessions…" minHeight={160} />
        ) : sessionsQuery.isError ? (
          <ErrorState minHeight={160} message="Impersonation sessions could not be loaded." onRetry={() => sessionsQuery.refetch()} />
        ) : sessions.length === 0 ? (
          <EmptyState title="No impersonation sessions" message="No sessions were issued in the last 7 days." minHeight={160} />
        ) : (
          <Box sx={{ overflowX: 'auto', mt: 1.5 }}>
            <Table size="small">
              <TableHead>
                <TableRow>
                  <TableCell sx={{ fontWeight: 700 }}>Tenant</TableCell>
                  <TableCell sx={{ fontWeight: 700 }}>Operator</TableCell>
                  <TableCell sx={{ fontWeight: 700 }}>Reason</TableCell>
                  <TableCell sx={{ fontWeight: 700 }}>Issued</TableCell>
                  <TableCell sx={{ fontWeight: 700 }}>Expires</TableCell>
                  <TableCell sx={{ fontWeight: 700 }}>Status</TableCell>
                  <TableCell sx={{ fontWeight: 700 }} align="right">Actions</TableCell>
                </TableRow>
              </TableHead>
              <TableBody>
                {sessions.map((session) => (
                  <TableRow key={session.jti} hover>
                    <TableCell sx={{ fontWeight: 600 }}>{session.tenantName ?? `Tenant ${session.tenantId}`}</TableCell>
                    <TableCell>
                      <Typography variant="caption">{session.actorEmail ?? `Operator ${session.actorPlatformUserId}`}</Typography>
                    </TableCell>
                    <TableCell>
                      <Typography variant="caption" color="text.secondary">{session.reason}</Typography>
                    </TableCell>
                    <TableCell>
                      <Typography variant="caption" color="text.secondary">{fmtDateTime(session.issuedAtUtc)}</Typography>
                    </TableCell>
                    <TableCell>
                      <Typography variant="caption" color="text.secondary">
                        {session.revokedAtUtc
                          ? `revoked ${fmtDateTime(session.revokedAtUtc)}${session.revokedBy ? ` · ${session.revokedBy}` : ''}`
                          : fmtDateTime(session.expiresAtUtc)}
                      </Typography>
                    </TableCell>
                    <TableCell>
                      <SoftChip label={session.status} tone={SESSION_TONE[session.status]} />
                    </TableCell>
                    <TableCell align="right">
                      {session.status === 'active' && (
                        <RoleGate allowed={permissions.canImpersonate} requirement={REQUIRED_ROLE_COPY.impersonate}>
                          {(disabled) => (
                            <Button
                              size="small"
                              color="error"
                              startIcon={<RevokeIcon fontSize="small" />}
                              disabled={disabled}
                              onClick={() => setRevokeTarget(session)}
                              sx={{ fontWeight: 700 }}
                            >
                              Revoke
                            </Button>
                          )}
                        </RoleGate>
                      )}
                    </TableCell>
                  </TableRow>
                ))}
              </TableBody>
            </Table>
          </Box>
        )}
      </Paper>

      {/* Failed platform logins */}
      <AuditFailurePanel
        title="Failed Platform Logins"
        caption={`Audit entries with action ${FAILED_LOGIN_ACTION}.`}
        emptyTitle="No failed platform logins"
        emptyMessage="No failed platform sign-in attempts are on record."
        isLoading={failedLoginsQuery.isLoading}
        isError={failedLoginsQuery.isError}
        onRetry={() => failedLoginsQuery.refetch()}
        rows={failedLoginsQuery.data ?? []}
      />

      {/* All recent audit failures */}
      <AuditFailurePanel
        title="Recent Audit Failures"
        caption="Every platform audit entry whose recorded result is failure."
        emptyTitle="No audit failures"
        emptyMessage="No failed privileged actions are on record."
        isLoading={failuresQuery.isLoading}
        isError={failuresQuery.isError}
        onRetry={() => failuresQuery.refetch()}
        rows={failuresQuery.data ?? []}
      />

      {/* Revoke confirm */}
      <Dialog open={!!revokeTarget} onClose={() => setRevokeTarget(null)} maxWidth="xs" fullWidth>
        <DialogTitle sx={{ fontWeight: 800 }}>Revoke impersonation session</DialogTitle>
        <DialogContent>
          <DialogContentText>
            Revoke the active impersonation session for{' '}
            <strong>{revokeTarget?.tenantName ?? `tenant ${revokeTarget?.tenantId}`}</strong>? The token is rejected
            immediately and the revocation is recorded in the audit trail.
          </DialogContentText>
        </DialogContent>
        <DialogActions sx={{ p: 2 }}>
          <Button onClick={() => setRevokeTarget(null)} color="inherit">Cancel</Button>
          <Button
            variant="contained"
            color="error"
            onClick={() => revokeTarget && revokeMutation.mutate(revokeTarget)}
            disabled={revokeMutation.isPending}
            sx={{ fontWeight: 700 }}
          >
            {revokeMutation.isPending ? 'Revoking…' : 'Revoke'}
          </Button>
        </DialogActions>
      </Dialog>
    </Box>
  );
}

function AuditFailurePanel({
  title,
  caption,
  emptyTitle,
  emptyMessage,
  isLoading,
  isError,
  onRetry,
  rows,
}: {
  title: string;
  caption: string;
  emptyTitle: string;
  emptyMessage: string;
  isLoading: boolean;
  isError: boolean;
  onRetry: () => void;
  rows: AuditEntry[];
}) {
  return (
    <Paper sx={{ p: 3, borderRadius: 3, mb: 2.5 }}>
      <Typography variant="h6" sx={{ fontWeight: 800, mb: 0.5 }}>{title}</Typography>
      <Typography variant="caption" color="text.secondary">{caption}</Typography>
      {isLoading ? (
        <LoadingState label="Loading audit entries…" minHeight={140} />
      ) : isError ? (
        <ErrorState minHeight={140} message="The audit service did not respond." onRetry={onRetry} />
      ) : rows.length === 0 ? (
        <EmptyState title={emptyTitle} message={emptyMessage} minHeight={140} />
      ) : (
        <Box sx={{ overflowX: 'auto', mt: 1.5 }}>
          <Table size="small">
            <TableHead>
              <TableRow>
                <TableCell sx={{ fontWeight: 700 }}>Timestamp</TableCell>
                <TableCell sx={{ fontWeight: 700 }}>Actor</TableCell>
                <TableCell sx={{ fontWeight: 700 }}>Action</TableCell>
                <TableCell sx={{ fontWeight: 700 }}>Target</TableCell>
                <TableCell sx={{ fontWeight: 700 }}>Source IP</TableCell>
                <TableCell sx={{ fontWeight: 700 }}>Result</TableCell>
              </TableRow>
            </TableHead>
            <TableBody>
              {rows.slice(0, 50).map((entry) => (
                <TableRow key={entry.id} hover>
                  <TableCell>
                    <Typography variant="caption" color="text.secondary">{fmtDateTime(entry.timestamp)}</Typography>
                  </TableCell>
                  <TableCell>
                    <Box sx={{ lineHeight: 1.2 }}>
                      <Typography variant="body2" sx={{ fontWeight: 600 }}>{entry.actor}</Typography>
                      <Typography variant="caption" color="text.secondary">{entry.actorEmail}</Typography>
                    </Box>
                  </TableCell>
                  <TableCell><Box component="code" sx={{ fontSize: 12.5, fontWeight: 700 }}>{entry.action}</Box></TableCell>
                  <TableCell>
                    <Typography variant="caption" sx={{ fontFamily: 'monospace' }} color="text.secondary">
                      {entry.targetType}{entry.targetId ? ` ${entry.targetId}` : ''}
                    </Typography>
                  </TableCell>
                  <TableCell>
                    <Typography variant="caption" sx={{ fontFamily: 'monospace' }} color="text.secondary">{entry.ipAddress}</Typography>
                  </TableCell>
                  <TableCell>
                    <SoftChip label={entry.result} tone={entry.result === 'success' ? 'success' : 'error'} dot={false} />
                  </TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        </Box>
      )}
    </Paper>
  );
}
