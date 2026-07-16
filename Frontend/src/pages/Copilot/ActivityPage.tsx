import React, { useEffect, useState } from 'react';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { useSnackbar } from 'notistack';
import dayjs from 'dayjs';
import {
  Box,
  Paper,
  Typography,
  Chip,
  Avatar,
  CircularProgress,
  Stack,
  Divider,
  Alert,
  Button,
  ToggleButton,
  ToggleButtonGroup,
  TextField,
  FormControlLabel,
  Switch,
  Tooltip,
} from '@mui/material';
import {
  History as HistoryIcon,
  Build as ToolIcon,
  CheckCircle as ExecutedIcon,
  PauseCircle as HeldIcon,
  Cancel as DeniedIcon,
  Tune as PolicyIcon,
  Save as SaveIcon,
} from '@mui/icons-material';
import copilotService, {
  type AgentAuditEntry,
  type AgentPolicy,
  type AgentAutonomyLevel,
} from '../../api/services/copilotService';

const decisionChip = (decision: AgentAuditEntry['decision']) => {
  const key = decision?.toLowerCase();
  if (key === 'executed') return { label: 'Executed', color: 'success' as const, icon: <ExecutedIcon sx={{ fontSize: 15 }} /> };
  if (key === 'held') return { label: 'Held', color: 'warning' as const, icon: <HeldIcon sx={{ fontSize: 15 }} /> };
  if (key === 'denied') return { label: 'Denied', color: 'error' as const, icon: <DeniedIcon sx={{ fontSize: 15 }} /> };
  return { label: decision || 'Unknown', color: 'default' as const, icon: <ToolIcon sx={{ fontSize: 15 }} /> };
};

const AUTONOMY_LEVELS: { value: AgentAutonomyLevel; help: string }[] = [
  { value: 'Observe', help: 'Agent only answers and suggests — never acts.' },
  { value: 'Suggest', help: 'Agent proposes actions; everything needs approval.' },
  { value: 'Act', help: 'Agent acts autonomously within the value caps below.' },
];

// ─── Autonomy policy panel ──────────────────────────────────────────────────

const PolicyPanel: React.FC = () => {
  const queryClient = useQueryClient();
  const { enqueueSnackbar } = useSnackbar();
  const { data, isLoading, isError, refetch } = useQuery({
    queryKey: ['agent-policy'],
    queryFn: copilotService.getPolicy,
  });

  const [draft, setDraft] = useState<AgentPolicy | null>(null);
  useEffect(() => {
    if (data) setDraft(data);
  }, [data]);

  const mutation = useMutation({
    mutationFn: (body: AgentPolicy) => copilotService.updatePolicy(body),
    onSuccess: (saved) => {
      enqueueSnackbar('Autonomy policy updated', { variant: 'success' });
      queryClient.setQueryData(['agent-policy'], saved);
      setDraft(saved);
    },
    onError: () => enqueueSnackbar('Failed to update policy', { variant: 'error' }),
  });

  if (isLoading) {
    return (
      <Paper sx={{ p: 3, borderRadius: 3, display: 'flex', justifyContent: 'center' }}>
        <CircularProgress size={24} />
      </Paper>
    );
  }
  if (isError || !draft) {
    return (
      <Alert severity="error" action={<Button size="small" onClick={() => refetch()}>Retry</Button>}>
        Could not load the autonomy policy.
      </Alert>
    );
  }

  const set = <K extends keyof AgentPolicy>(key: K, value: AgentPolicy[K]) => setDraft((p) => (p ? { ...p, [key]: value } : p));
  const dirty = data ? JSON.stringify(draft) !== JSON.stringify(data) : false;

  return (
    <Paper sx={{ p: 2.5, borderRadius: 3 }}>
      <Stack direction="row" spacing={1.25} sx={{ mb: 2, alignItems: 'center' }}>
        <PolicyIcon color="primary" />
        <Box>
          <Typography variant="subtitle1" sx={{ fontWeight: 800 }}>
            Autonomy policy
          </Typography>
          <Typography variant="caption" color="text.secondary">
            Dial how much the Copilot can do on its own.
          </Typography>
        </Box>
      </Stack>

      <Typography variant="caption" sx={{ fontWeight: 800, color: 'text.secondary', textTransform: 'uppercase', letterSpacing: '0.06em' }}>
        Autonomy level
      </Typography>
      <ToggleButtonGroup
        exclusive
        fullWidth
        size="small"
        value={draft.autonomyLevel}
        onChange={(_, v: AgentAutonomyLevel | null) => v && set('autonomyLevel', v)}
        sx={{ mt: 1, mb: 0.5 }}
      >
        {AUTONOMY_LEVELS.map((lvl) => (
          <ToggleButton key={lvl.value} value={lvl.value} sx={{ fontWeight: 700, textTransform: 'none' }}>
            <Tooltip title={lvl.help}>
              <span>{lvl.value}</span>
            </Tooltip>
          </ToggleButton>
        ))}
      </ToggleButtonGroup>
      <Typography variant="caption" color="text.secondary" sx={{ display: 'block', mb: 2 }}>
        {AUTONOMY_LEVELS.find((l) => l.value === draft.autonomyLevel)?.help}
      </Typography>

      <Stack direction={{ xs: 'column', sm: 'row' }} spacing={1.5} sx={{ mb: 2 }}>
        <TextField
          fullWidth
          size="small"
          type="number"
          label="Max auto-award value"
          value={draft.maxAutoAwardValue}
          onChange={(e) => set('maxAutoAwardValue', Number(e.target.value))}
        />
        <TextField
          fullWidth
          size="small"
          type="number"
          label="Max auto-order value"
          value={draft.maxAutoOrderValue}
          onChange={(e) => set('maxAutoOrderValue', Number(e.target.value))}
        />
      </Stack>

      <Stack spacing={0.5} sx={{ mb: 2 }}>
        <FormControlLabel
          control={<Switch checked={draft.requireApprovalForAwards} onChange={(e) => set('requireApprovalForAwards', e.target.checked)} />}
          label={<Typography variant="body2" sx={{ fontWeight: 600 }}>Require approval for awards</Typography>}
        />
        <FormControlLabel
          control={<Switch checked={draft.requireApprovalForOrders} onChange={(e) => set('requireApprovalForOrders', e.target.checked)} />}
          label={<Typography variant="body2" sx={{ fontWeight: 600 }}>Require approval for orders</Typography>}
        />
        <FormControlLabel
          control={<Switch checked={draft.requireApprovalForSupplierEmails} onChange={(e) => set('requireApprovalForSupplierEmails', e.target.checked)} />}
          label={<Typography variant="body2" sx={{ fontWeight: 600 }}>Require approval for supplier emails</Typography>}
        />
      </Stack>

      <Button
        variant="contained"
        fullWidth
        startIcon={mutation.isPending ? <CircularProgress size={16} color="inherit" /> : <SaveIcon />}
        disabled={!dirty || mutation.isPending}
        onClick={() => draft && mutation.mutate(draft)}
        sx={{ fontWeight: 700 }}
      >
        {dirty ? 'Save policy' : 'Saved'}
      </Button>
    </Paper>
  );
};

// ─── Audit feed ─────────────────────────────────────────────────────────────

const ActivityPage: React.FC = () => {
  const { data: audit = [], isLoading, isError, refetch } = useQuery({
    queryKey: ['agent-audit'],
    queryFn: () => copilotService.getAudit(100),
  });

  return (
    <Box sx={{ width: '100%', px: 1, py: 1, maxWidth: 1200, mx: 'auto' }}>
      <Stack direction="row" spacing={1.5} sx={{ mb: 0.5, alignItems: 'center' }}>
        <Avatar sx={{ width: 40, height: 40, background: 'linear-gradient(135deg, #6366f1 0%, #0ea5e9 100%)' }}>
          <HistoryIcon />
        </Avatar>
        <Box>
          <Typography variant="h5" sx={{ fontWeight: 800, letterSpacing: '-0.02em' }}>
            Activity
          </Typography>
          <Typography variant="body2" color="text.secondary">
            A read-only log of every autonomous action the Copilot has taken.
          </Typography>
        </Box>
      </Stack>

      <Divider sx={{ my: 2 }} />

      <Box sx={{ display: 'flex', gap: 2, alignItems: 'flex-start', flexDirection: { xs: 'column', md: 'row' } }}>
        {/* Feed */}
        <Box sx={{ flex: 1, minWidth: 0, width: '100%' }}>
          {isLoading ? (
            <Box sx={{ display: 'flex', justifyContent: 'center', py: 6 }}>
              <CircularProgress />
            </Box>
          ) : isError ? (
            <Alert severity="error" action={<Button size="small" onClick={() => refetch()}>Retry</Button>}>
              Could not load the activity feed.
            </Alert>
          ) : audit.length === 0 ? (
            <Paper sx={{ p: 5, borderRadius: 3, textAlign: 'center' }}>
              <HistoryIcon sx={{ fontSize: 44, color: 'text.disabled', mb: 1 }} />
              <Typography variant="subtitle1" sx={{ fontWeight: 700 }}>
                No activity yet
              </Typography>
              <Typography variant="body2" color="text.secondary">
                Actions taken by the Copilot will appear here.
              </Typography>
            </Paper>
          ) : (
            <Stack spacing={1.25}>
              {audit.map((entry) => {
                const chip = decisionChip(entry.decision);
                return (
                  <Paper key={entry.id} sx={{ p: 1.75, borderRadius: 2.5, display: 'flex', gap: 1.5, alignItems: 'flex-start' }}>
                    <Avatar sx={{ width: 34, height: 34, bgcolor: 'action.hover', color: `${chip.color === 'default' ? 'text.secondary' : `${chip.color}.main`}` }}>
                      {chip.icon}
                    </Avatar>
                    <Box sx={{ flex: 1, minWidth: 0 }}>
                      <Stack direction="row" spacing={1} sx={{ flexWrap: 'wrap', mb: 0.25, alignItems: 'center' }}>
                        <Typography variant="subtitle2" sx={{ fontWeight: 800, fontFamily: 'monospace' }}>
                          {entry.toolName}
                        </Typography>
                        <Chip size="small" icon={chip.icon} label={chip.label} color={chip.color} variant="outlined" sx={{ fontWeight: 700, fontSize: '0.68rem', height: 20 }} />
                      </Stack>
                      <Typography variant="body2" color="text.secondary">
                        {entry.summary}
                      </Typography>
                    </Box>
                    <Box sx={{ textAlign: 'right', flexShrink: 0 }}>
                      <Typography variant="caption" sx={{ fontWeight: 700, display: 'block' }}>
                        {entry.actor}
                      </Typography>
                      <Typography variant="caption" color="text.disabled">
                        {dayjs(entry.createdOn).format('MMM D, h:mm A')}
                      </Typography>
                    </Box>
                  </Paper>
                );
              })}
            </Stack>
          )}
        </Box>

        {/* Policy side panel */}
        <Box sx={{ width: { xs: '100%', md: 340 }, flexShrink: 0 }}>
          <PolicyPanel />
        </Box>
      </Box>
    </Box>
  );
};

export default ActivityPage;
