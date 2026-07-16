import React, { useEffect, useState } from 'react';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { useSnackbar } from 'notistack';
import dayjs from 'dayjs';
import {
  Box,
  Paper,
  Typography,
  Avatar,
  CircularProgress,
  Stack,
  Divider,
  Alert,
  Button,
  ButtonBase,
  Collapse,
  TextField,
  InputAdornment,
  FormControlLabel,
  Switch,
  Radio,
  RadioGroup,
  FormControl,
  Chip,
} from '@mui/material';
import {
  History as HistoryIcon,
  Save as SaveIcon,
  Settings as SettingsIcon,
  ExpandMore as ExpandIcon,
} from '@mui/icons-material';
import copilotService, {
  type AgentAuditEntry,
  type AgentPolicy,
  type AgentAutonomyLevel,
} from '../../api/services/copilotService';
import { humanizeTool } from './humanize';

// ─── Plain-English decision labels + calm status dots ────────────────────────

interface DecisionInfo {
  label: string;
  /** Theme color path used for the timeline dot. */
  color: string;
}

const decisionInfo = (decision: AgentAuditEntry['decision']): DecisionInfo => {
  switch ((decision ?? '').toLowerCase()) {
    case 'executed':
      return { label: 'Done', color: 'success.main' };
    case 'held':
      return { label: 'Waiting for approval', color: 'warning.main' };
    case 'denied':
      return { label: 'Blocked by your settings', color: 'text.disabled' };
    case 'failed':
      return { label: "Couldn't complete", color: 'error.main' };
    default:
      return { label: decision ? decision.replace(/[_-]+/g, ' ') : 'Unknown', color: 'text.disabled' };
  }
};

// ─── Plain autonomy choices for the (hidden) advanced panel ──────────────────

interface AutonomyChoice {
  value: AgentAutonomyLevel;
  title: string;
  help: string;
  recommended?: boolean;
}

const AUTONOMY_CHOICES: AutonomyChoice[] = [
  {
    value: 'Observe',
    title: 'Ask me before doing anything',
    help: 'I’ll answer questions and suggest next steps, but I won’t take any action on my own.',
  },
  {
    value: 'Suggest',
    title: 'Handle routine tasks, ask before big ones',
    help: 'I’ll take care of small, everyday steps and check with you before anything important.',
    recommended: true,
  },
  {
    value: 'Act',
    title: 'Full autopilot',
    help: 'I’ll act on my own whenever I can, pausing only for the dollar limits you set below.',
  },
];

// ─── Advanced settings (the old "autonomy policy" cockpit, tucked away) ───────

const AdvancedSettings: React.FC = () => {
  const queryClient = useQueryClient();
  const { enqueueSnackbar } = useSnackbar();
  const [open, setOpen] = useState(false);

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
      enqueueSnackbar('Your settings are saved', { variant: 'success' });
      queryClient.setQueryData(['agent-policy'], saved);
      setDraft(saved);
    },
    onError: () => enqueueSnackbar('Sorry, I couldn’t save that. Please try again.', { variant: 'error' }),
  });

  const set = <K extends keyof AgentPolicy>(key: K, value: AgentPolicy[K]) =>
    setDraft((p) => (p ? { ...p, [key]: value } : p));
  const dirty = data && draft ? JSON.stringify(draft) !== JSON.stringify(data) : false;

  return (
    <Paper sx={{ borderRadius: 3, overflow: 'hidden' }}>
      <ButtonBase
        onClick={() => setOpen((v) => !v)}
        aria-expanded={open}
        sx={{
          width: '100%',
          display: 'flex',
          alignItems: 'center',
          gap: 1.25,
          px: 2.5,
          py: 1.75,
          justifyContent: 'flex-start',
        }}
      >
        <SettingsIcon sx={{ color: 'text.secondary' }} />
        <Box sx={{ textAlign: 'left', flex: 1 }}>
          <Typography variant="subtitle1" sx={{ fontWeight: 700 }}>
            Advanced settings
          </Typography>
          <Typography variant="caption" color="text.secondary">
            Choose how much I can handle on my own.
          </Typography>
        </Box>
        <ExpandIcon sx={{ color: 'text.secondary', transform: open ? 'rotate(180deg)' : 'none', transition: 'transform 0.2s' }} />
      </ButtonBase>

      <Collapse in={open}>
        <Divider />
        <Box sx={{ p: 2.5 }}>
          {isLoading ? (
            <Box sx={{ display: 'flex', justifyContent: 'center', py: 3 }}>
              <CircularProgress size={24} />
            </Box>
          ) : isError || !draft ? (
            <Alert severity="error" action={<Button size="small" onClick={() => refetch()}>Try again</Button>}>
              I couldn't load your settings.
            </Alert>
          ) : (
            <>
              <FormControl component="fieldset" sx={{ width: '100%' }}>
                <Typography variant="subtitle2" sx={{ fontWeight: 700, mb: 1 }}>
                  How much should I handle on my own?
                </Typography>
                <RadioGroup
                  value={draft.autonomyLevel}
                  onChange={(e) => set('autonomyLevel', e.target.value as AgentAutonomyLevel)}
                >
                  <Stack spacing={1}>
                    {AUTONOMY_CHOICES.map((choice) => {
                      const selected = draft.autonomyLevel === choice.value;
                      return (
                        <Paper
                          key={choice.value}
                          variant="outlined"
                          sx={{
                            borderRadius: 2,
                            borderColor: selected ? 'primary.main' : 'divider',
                            borderWidth: selected ? 2 : 1,
                            bgcolor: selected ? 'action.hover' : 'transparent',
                            transition: 'all 0.15s',
                          }}
                        >
                          <FormControlLabel
                            value={choice.value}
                            control={<Radio sx={{ alignSelf: 'flex-start', mt: 0.5 }} />}
                            sx={{ alignItems: 'flex-start', m: 0, px: 1.5, py: 1.25, width: '100%' }}
                            label={
                              <Box sx={{ py: 0.25 }}>
                                <Stack direction="row" spacing={1} sx={{ alignItems: 'center', flexWrap: 'wrap' }}>
                                  <Typography variant="body2" sx={{ fontWeight: 700 }}>
                                    {choice.title}
                                  </Typography>
                                  {choice.recommended && (
                                    <Chip size="small" label="Recommended" color="primary" variant="outlined" sx={{ height: 20, fontWeight: 700, fontSize: '0.65rem' }} />
                                  )}
                                </Stack>
                                <Typography variant="caption" color="text.secondary">
                                  {choice.help}
                                </Typography>
                              </Box>
                            }
                          />
                        </Paper>
                      );
                    })}
                  </Stack>
                </RadioGroup>
              </FormControl>

              <Typography variant="subtitle2" sx={{ fontWeight: 700, mt: 3, mb: 1 }}>
                Spending limits
              </Typography>
              <Stack direction={{ xs: 'column', sm: 'row' }} spacing={1.5}>
                <TextField
                  fullWidth
                  size="small"
                  type="number"
                  label="Auto-approve awards under"
                  value={draft.maxAutoAwardValue}
                  onChange={(e) => set('maxAutoAwardValue', Number(e.target.value))}
                  slotProps={{ input: { startAdornment: <InputAdornment position="start">$</InputAdornment> } }}
                />
                <TextField
                  fullWidth
                  size="small"
                  type="number"
                  label="Auto-approve orders under"
                  value={draft.maxAutoOrderValue}
                  onChange={(e) => set('maxAutoOrderValue', Number(e.target.value))}
                  slotProps={{ input: { startAdornment: <InputAdornment position="start">$</InputAdornment> } }}
                />
              </Stack>
              <Typography variant="caption" color="text.secondary" sx={{ display: 'block', mt: 0.75 }}>
                Anything above these amounts will always come to you for approval.
              </Typography>

              <Typography variant="subtitle2" sx={{ fontWeight: 700, mt: 3, mb: 1 }}>
                Always check with me first
              </Typography>
              <Stack spacing={0.25}>
                <FormControlLabel
                  control={<Switch checked={draft.requireApprovalForAwards} onChange={(e) => set('requireApprovalForAwards', e.target.checked)} />}
                  label={<Typography variant="body2" sx={{ fontWeight: 600 }}>Before awarding a supplier</Typography>}
                />
                <FormControlLabel
                  control={<Switch checked={draft.requireApprovalForOrders} onChange={(e) => set('requireApprovalForOrders', e.target.checked)} />}
                  label={<Typography variant="body2" sx={{ fontWeight: 600 }}>Before placing an order</Typography>}
                />
                <FormControlLabel
                  control={<Switch checked={draft.requireApprovalForSupplierEmails} onChange={(e) => set('requireApprovalForSupplierEmails', e.target.checked)} />}
                  label={<Typography variant="body2" sx={{ fontWeight: 600 }}>Before emailing a supplier</Typography>}
                />
              </Stack>

              <Button
                variant="contained"
                startIcon={mutation.isPending ? <CircularProgress size={16} color="inherit" /> : <SaveIcon />}
                disabled={!dirty || mutation.isPending}
                onClick={() => draft && mutation.mutate(draft)}
                sx={{ fontWeight: 700, mt: 2.5 }}
              >
                {dirty ? 'Save changes' : 'Saved'}
              </Button>
            </>
          )}
        </Box>
      </Collapse>
    </Paper>
  );
};

// ─── The story of what Nexora has been doing ─────────────────────────────────

const ActivityPage: React.FC = () => {
  const { data: audit = [], isLoading, isError, refetch } = useQuery({
    queryKey: ['agent-audit'],
    queryFn: () => copilotService.getAudit(100),
  });

  return (
    <Box sx={{ width: '100%', px: 1, py: 1, maxWidth: 860, mx: 'auto' }}>
      <Stack direction="row" spacing={1.5} sx={{ mb: 0.5, alignItems: 'center' }}>
        <Avatar sx={{ width: 40, height: 40, background: 'linear-gradient(135deg, #4682B4 0%, #0ea5e9 100%)' }}>
          <HistoryIcon />
        </Avatar>
        <Box>
          <Typography variant="h5" sx={{ fontWeight: 800, letterSpacing: '-0.02em' }}>
            Here's what I've been doing
          </Typography>
          <Typography variant="body2" color="text.secondary">
            A plain-language record of every step I've taken for you.
          </Typography>
        </Box>
      </Stack>

      <Divider sx={{ my: 2 }} />

      <Box sx={{ mb: 2.5 }}>
        <AdvancedSettings />
      </Box>

      {isLoading ? (
        <Box sx={{ display: 'flex', justifyContent: 'center', py: 6 }}>
          <CircularProgress />
        </Box>
      ) : isError ? (
        <Alert severity="error" action={<Button size="small" onClick={() => refetch()}>Try again</Button>}>
          I couldn't load your activity just now.
        </Alert>
      ) : audit.length === 0 ? (
        <Paper sx={{ p: 6, borderRadius: 3, textAlign: 'center' }}>
          <HistoryIcon sx={{ fontSize: 48, color: 'text.disabled', mb: 1.5 }} />
          <Typography variant="h6" sx={{ fontWeight: 700 }}>
            Nothing here yet
          </Typography>
          <Typography variant="body2" color="text.secondary" sx={{ mt: 0.5 }}>
            Once I start helping out, everything I do will show up here.
          </Typography>
        </Paper>
      ) : (
        <Stack spacing={0}>
          {audit.map((entry, i) => {
            const info = decisionInfo(entry.decision);
            const tool = humanizeTool(entry.toolName);
            const last = i === audit.length - 1;
            return (
              <Box key={entry.id} sx={{ display: 'flex', gap: 2, alignItems: 'stretch' }}>
                {/* Timeline rail: colored dot + connecting line */}
                <Stack sx={{ alignItems: 'center', width: 14, flexShrink: 0 }}>
                  <Box sx={{ width: 12, height: 12, borderRadius: '50%', bgcolor: info.color, mt: 2, flexShrink: 0, boxShadow: (t) => `0 0 0 3px ${t.palette.background.paper}` }} />
                  {!last && <Box sx={{ flex: 1, width: 2, bgcolor: 'divider', my: 0.5 }} />}
                </Stack>

                {/* Event card */}
                <Paper variant="outlined" sx={{ flex: 1, p: 1.75, borderRadius: 2.5, mb: 1.5, minWidth: 0 }}>
                  <Stack direction="row" spacing={1} sx={{ alignItems: 'flex-start' }}>
                    <Box component="span" sx={{ fontSize: '1.1rem', lineHeight: 1.4, flexShrink: 0 }}>{tool.icon}</Box>
                    <Box sx={{ flex: 1, minWidth: 0 }}>
                      <Stack direction="row" spacing={1} sx={{ alignItems: 'baseline', flexWrap: 'wrap', mb: 0.25 }}>
                        <Typography variant="subtitle2" sx={{ fontWeight: 700 }}>
                          {tool.label}
                        </Typography>
                        <Typography variant="caption" sx={{ fontWeight: 700, color: info.color }}>
                          · {info.label}
                        </Typography>
                      </Stack>
                      {entry.summary && (
                        <Typography variant="body2" color="text.secondary">
                          {entry.summary}
                        </Typography>
                      )}
                      <Typography variant="caption" color="text.disabled" sx={{ display: 'block', mt: 0.5 }}>
                        {entry.actor} · {dayjs(entry.createdOn).format('MMM D [at] h:mm A')}
                      </Typography>
                    </Box>
                  </Stack>
                </Paper>
              </Box>
            );
          })}
        </Stack>
      )}
    </Box>
  );
};

export default ActivityPage;
