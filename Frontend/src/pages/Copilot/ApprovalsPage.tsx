import React from 'react';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { useSnackbar } from 'notistack';
import dayjs from 'dayjs';
import {
  Box,
  Paper,
  Typography,
  Button,
  Avatar,
  CircularProgress,
  Stack,
  Divider,
  Alert,
  alpha,
} from '@mui/material';
import {
  Inbox as InboxIcon,
  Check as ApproveIcon,
  Close as RejectIcon,
  TaskAlt as DoneIcon,
} from '@mui/icons-material';
import copilotService, { type AgentApproval } from '../../api/services/copilotService';
import { humanizeTool, approvalQuestion } from './humanize';

// ─── A single "is this OK?" request ──────────────────────────────────────────

const ApprovalCard: React.FC<{
  approval: AgentApproval;
  onApprove?: () => void;
  onReject?: () => void;
  busy?: boolean;
  decided?: boolean;
}> = ({ approval, onApprove, onReject, busy, decided }) => {
  const tool = humanizeTool(approval.toolName);
  const outcome =
    approval.status === 'approved' ? 'You said yes' : approval.status === 'rejected' ? 'You said no' : '';

  return (
    <Paper sx={{ p: 2.5, borderRadius: 3, display: 'flex', gap: 2, alignItems: 'flex-start', opacity: decided ? 0.75 : 1 }}>
      <Avatar
        sx={{
          width: 44,
          height: 44,
          fontSize: '1.4rem',
          bgcolor: decided ? 'action.hover' : (t) => alpha(t.palette.warning.main, 0.14),
          flexShrink: 0,
        }}
      >
        {tool.icon}
      </Avatar>
      <Box sx={{ flex: 1, minWidth: 0 }}>
        <Typography variant="subtitle1" sx={{ fontWeight: 700, lineHeight: 1.35 }}>
          {approvalQuestion(approval.toolName)}
        </Typography>
        {approval.summary && (
          <Typography variant="body2" color="text.secondary" sx={{ mt: 0.5 }}>
            {approval.summary}
          </Typography>
        )}
        <Typography variant="caption" color="text.disabled" sx={{ display: 'block', mt: 0.75 }}>
          {decided && outcome ? `${outcome} · ` : ''}Asked {dayjs(approval.requestedOn).format('MMM D, YYYY [at] h:mm A')}
        </Typography>

        {!decided && (
          <Stack direction={{ xs: 'column', sm: 'row' }} spacing={1} sx={{ mt: 1.75 }}>
            <Button
              variant="contained"
              color="success"
              startIcon={<ApproveIcon />}
              onClick={onApprove}
              disabled={busy}
              sx={{ fontWeight: 700, px: 2.5 }}
            >
              Yes, do it
            </Button>
            <Button
              variant="outlined"
              color="inherit"
              startIcon={<RejectIcon />}
              onClick={onReject}
              disabled={busy}
              sx={{ fontWeight: 700, px: 2.5, color: 'text.secondary', borderColor: 'divider' }}
            >
              No, cancel
            </Button>
          </Stack>
        )}
      </Box>
    </Paper>
  );
};

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
      enqueueSnackbar(res.resultSummary || 'Done — I’ll take it from here', { variant: 'success' });
      invalidateAll();
    },
    onError: () => enqueueSnackbar('Sorry, that didn’t go through. Please try again.', { variant: 'error' }),
  });

  const rejectMutation = useMutation({
    mutationFn: (id: string) => copilotService.reject(id),
    onSuccess: (res) => {
      enqueueSnackbar(res.resultSummary || 'No problem — I won’t do that', { variant: 'info' });
      invalidateAll();
    },
    onError: () => enqueueSnackbar('Sorry, that didn’t go through. Please try again.', { variant: 'error' }),
  });

  const decided = [...approved, ...rejected].sort((a, b) => dayjs(b.requestedOn).valueOf() - dayjs(a.requestedOn).valueOf());
  const busyId =
    (approveMutation.isPending ? approveMutation.variables : undefined) ??
    (rejectMutation.isPending ? rejectMutation.variables : undefined);

  return (
    <Box sx={{ width: '100%', px: 1, py: 1, maxWidth: 820, mx: 'auto' }}>
      <Stack direction="row" spacing={1.5} sx={{ mb: 0.5, alignItems: 'center' }}>
        <Avatar sx={{ width: 40, height: 40, background: 'linear-gradient(135deg, #f59e0b 0%, #f97316 100%)' }}>
          <InboxIcon />
        </Avatar>
        <Box>
          <Typography variant="h5" sx={{ fontWeight: 800, letterSpacing: '-0.02em' }}>
            Your approvals
          </Typography>
          <Typography variant="body2" color="text.secondary">
            A few things need a quick yes or no from you.
          </Typography>
        </Box>
      </Stack>

      <Divider sx={{ my: 2 }} />

      {isLoading ? (
        <Box sx={{ display: 'flex', justifyContent: 'center', py: 6 }}>
          <CircularProgress />
        </Box>
      ) : isError ? (
        <Alert severity="error" action={<Button size="small" onClick={() => refetch()}>Try again</Button>}>
          I couldn't load your approvals just now.
        </Alert>
      ) : (
        <>
          {pending.length === 0 ? (
            <Paper sx={{ p: 6, borderRadius: 3, textAlign: 'center' }}>
              <DoneIcon sx={{ fontSize: 48, color: 'success.main', mb: 1.5, opacity: 0.85 }} />
              <Typography variant="h6" sx={{ fontWeight: 700 }}>
                Nothing needs your approval right now
              </Typography>
              <Typography variant="body2" color="text.secondary" sx={{ mt: 0.5 }}>
                If I need a yes or no from you, it'll show up here.
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
              <Typography variant="overline" sx={{ display: 'block', mt: 4, mb: 1.5, fontWeight: 800, color: 'text.secondary', letterSpacing: '0.08em' }}>
                Already answered
              </Typography>
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
