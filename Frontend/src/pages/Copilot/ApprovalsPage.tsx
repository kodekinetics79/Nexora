import React from 'react';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { useSnackbar } from 'notistack';
import dayjs from 'dayjs';
import {
  Box,
  Paper,
  Typography,
  Button,
  Chip,
  Avatar,
  CircularProgress,
  Stack,
  Divider,
  Alert,
} from '@mui/material';
import {
  Inbox as InboxIcon,
  Check as ApproveIcon,
  Close as RejectIcon,
  Bolt as ActionIcon,
  AutoAwesome as SparkleIcon,
  TaskAlt as DoneIcon,
} from '@mui/icons-material';
import copilotService, { type AgentApproval } from '../../api/services/copilotService';

const statusChip = (status: AgentApproval['status']) => {
  const map: Record<string, { label: string; color: 'warning' | 'success' | 'error' | 'default' }> = {
    pending: { label: 'Pending', color: 'warning' },
    approved: { label: 'Approved', color: 'success' },
    rejected: { label: 'Rejected', color: 'error' },
  };
  const cfg = map[status] ?? { label: status, color: 'default' as const };
  return <Chip size="small" label={cfg.label} color={cfg.color} variant="outlined" sx={{ fontWeight: 700, fontSize: '0.7rem' }} />;
};

const ApprovalCard: React.FC<{
  approval: AgentApproval;
  onApprove?: () => void;
  onReject?: () => void;
  busy?: boolean;
  decided?: boolean;
}> = ({ approval, onApprove, onReject, busy, decided }) => (
  <Paper sx={{ p: 2, borderRadius: 3, display: 'flex', gap: 1.75, alignItems: 'flex-start', opacity: decided ? 0.85 : 1 }}>
    <Avatar sx={{ width: 40, height: 40, bgcolor: decided ? 'action.hover' : (t) => t.palette.warning.light, color: decided ? 'text.secondary' : 'warning.dark' }}>
      <ActionIcon />
    </Avatar>
    <Box sx={{ flex: 1, minWidth: 0 }}>
      <Stack direction="row" spacing={1} sx={{ mb: 0.5, flexWrap: 'wrap', alignItems: 'center' }}>
        <Typography variant="subtitle2" sx={{ fontWeight: 800, fontFamily: 'monospace' }}>
          {approval.toolName}
        </Typography>
        {statusChip(approval.status)}
      </Stack>
      <Typography variant="body2" color="text.secondary" sx={{ mb: 0.75 }}>
        {approval.summary || 'The agent is requesting approval to run this action.'}
      </Typography>
      <Typography variant="caption" color="text.disabled">
        Requested {dayjs(approval.requestedOn).format('MMM D, YYYY h:mm A')}
      </Typography>
    </Box>
    {!decided && (
      <Stack spacing={1} sx={{ flexShrink: 0 }}>
        <Button size="small" variant="contained" color="success" startIcon={<ApproveIcon />} onClick={onApprove} disabled={busy} sx={{ fontWeight: 700 }}>
          Approve
        </Button>
        <Button size="small" variant="outlined" color="error" startIcon={<RejectIcon />} onClick={onReject} disabled={busy} sx={{ fontWeight: 700 }}>
          Reject
        </Button>
      </Stack>
    )}
  </Paper>
);

const ApprovalsPage: React.FC = () => {
  const queryClient = useQueryClient();
  const { enqueueSnackbar } = useSnackbar();

  const { data: pending = [], isLoading, isError, refetch } = useQuery({
    queryKey: ['agent-approvals', 'pending'],
    queryFn: () => copilotService.getApprovals('pending'),
  });

  const { data: approved = [] } = useQuery({
    queryKey: ['agent-approvals', 'approved'],
    queryFn: () => copilotService.getApprovals('approved'),
  });

  const { data: rejected = [] } = useQuery({
    queryKey: ['agent-approvals', 'rejected'],
    queryFn: () => copilotService.getApprovals('rejected'),
  });

  const invalidateAll = () => {
    queryClient.invalidateQueries({ queryKey: ['agent-approvals'] });
    queryClient.invalidateQueries({ queryKey: ['agent-audit'] });
  };

  const approveMutation = useMutation({
    mutationFn: (id: string) => copilotService.approve(id),
    onSuccess: (res) => {
      enqueueSnackbar(res.resultSummary || 'Action approved and executed', { variant: 'success' });
      invalidateAll();
    },
    onError: () => enqueueSnackbar('Failed to approve action', { variant: 'error' }),
  });

  const rejectMutation = useMutation({
    mutationFn: (id: string) => copilotService.reject(id),
    onSuccess: (res) => {
      enqueueSnackbar(res.resultSummary || 'Action rejected', { variant: 'info' });
      invalidateAll();
    },
    onError: () => enqueueSnackbar('Failed to reject action', { variant: 'error' }),
  });

  const decided = [...approved, ...rejected].sort((a, b) => dayjs(b.requestedOn).valueOf() - dayjs(a.requestedOn).valueOf());
  const busyId =
    (approveMutation.isPending ? approveMutation.variables : undefined) ??
    (rejectMutation.isPending ? rejectMutation.variables : undefined);

  return (
    <Box sx={{ width: '100%', px: 1, py: 1, maxWidth: 900, mx: 'auto' }}>
      <Stack direction="row" spacing={1.5} sx={{ mb: 0.5, alignItems: 'center' }}>
        <Avatar sx={{ width: 40, height: 40, background: 'linear-gradient(135deg, #f59e0b 0%, #f97316 100%)' }}>
          <InboxIcon />
        </Avatar>
        <Box>
          <Typography variant="h5" sx={{ fontWeight: 800, letterSpacing: '-0.02em' }}>
            Approvals
          </Typography>
          <Typography variant="body2" color="text.secondary">
            Human-in-the-loop review for actions the Copilot has queued.
          </Typography>
        </Box>
      </Stack>

      <Divider sx={{ my: 2 }} />

      {isLoading ? (
        <Box sx={{ display: 'flex', justifyContent: 'center', py: 6 }}>
          <CircularProgress />
        </Box>
      ) : isError ? (
        <Alert severity="error" action={<Button size="small" onClick={() => refetch()}>Retry</Button>}>
          Could not load pending approvals.
        </Alert>
      ) : (
        <>
          <Stack direction="row" spacing={1} sx={{ mb: 1.5, alignItems: 'center' }}>
            <Typography variant="overline" sx={{ fontWeight: 800, color: 'text.secondary', letterSpacing: '0.08em' }}>
              Pending
            </Typography>
            <Chip size="small" label={pending.length} color={pending.length ? 'warning' : 'default'} sx={{ fontWeight: 800, height: 20 }} />
          </Stack>

          {pending.length === 0 ? (
            <Paper sx={{ p: 5, borderRadius: 3, textAlign: 'center' }}>
              <DoneIcon sx={{ fontSize: 44, color: 'success.main', mb: 1, opacity: 0.8 }} />
              <Typography variant="subtitle1" sx={{ fontWeight: 700 }}>
                You're all caught up
              </Typography>
              <Typography variant="body2" color="text.secondary">
                No actions are waiting on your approval right now.
              </Typography>
            </Paper>
          ) : (
            <Stack spacing={1.5}>
              {pending.map((a) => (
                <ApprovalCard
                  key={a.id}
                  approval={a}
                  busy={busyId === a.id}
                  onApprove={() => approveMutation.mutate(a.id)}
                  onReject={() => rejectMutation.mutate(a.id)}
                />
              ))}
            </Stack>
          )}

          {decided.length > 0 && (
            <>
              <Stack direction="row" spacing={1} sx={{ mb: 1.5, mt: 4, alignItems: 'center' }}>
                <SparkleIcon sx={{ fontSize: 18, color: 'text.secondary' }} />
                <Typography variant="overline" sx={{ fontWeight: 800, color: 'text.secondary', letterSpacing: '0.08em' }}>
                  Recently decided
                </Typography>
              </Stack>
              <Stack spacing={1.5}>
                {decided.slice(0, 20).map((a) => (
                  <ApprovalCard key={a.id} approval={a} decided />
                ))}
              </Stack>
            </>
          )}
        </>
      )}
    </Box>
  );
};

export default ApprovalsPage;
