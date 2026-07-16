import React, { useCallback, useEffect, useRef, useState } from 'react';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { useNavigate } from 'react-router-dom';
import { useSnackbar } from 'notistack';
import dayjs from 'dayjs';
import {
  Box,
  Paper,
  Typography,
  IconButton,
  TextField,
  Button,
  ButtonBase,
  Chip,
  Avatar,
  CircularProgress,
  Collapse,
  List,
  ListItemButton,
  ListItemText,
  Divider,
  Tooltip,
  Stack,
  alpha,
} from '@mui/material';
import {
  AutoAwesome as SparkleIcon,
  Send as SendIcon,
  Add as AddIcon,
  Person as PersonIcon,
  Inbox as ApprovalsIcon,
  History as ActivityIcon,
  ChatBubbleOutlined as ChatIcon,
  ExpandMore as ExpandIcon,
  KeyboardArrowRight as ArrowIcon,
} from '@mui/icons-material';
import copilotService, {
  type AgentStreamEvent,
  type AgentSessionSummary,
} from '../../api/services/copilotService';
import { humanizeTool } from './humanize';

// ─── Local view models ──────────────────────────────────────────────────────

type ActivityKind = 'tool_call' | 'tool_result' | 'approval';

interface ChatActivity {
  id: string;
  kind: ActivityKind;
  name: string;
  summary?: string;
  ok?: boolean;
  approvalId?: string;
}

interface ChatMessage {
  id: string;
  role: 'user' | 'assistant';
  content: string;
  activities: ChatActivity[];
  streaming?: boolean;
  errored?: boolean;
}

// Plain-language example prompts shown on a fresh conversation.
const SUGGESTED_PROMPTS: { icon: string; text: string }[] = [
  { icon: '🔍', text: 'Show me the RFQs closing this week' },
  { icon: '🏭', text: 'Who has quoted on RFQ-1001?' },
  { icon: '📤', text: 'Send RFQ-1001 to its top 3 suppliers' },
  { icon: '⚖️', text: 'Which supplier should I award RFQ-1001 to?' },
];

let idCounter = 0;
const nextId = () => `${Date.now()}-${idCounter++}`;

// ─── Quiet, plain-English record of what the assistant did ───────────────────

/** One human-readable line for a single activity. */
const activityLine = (a: ChatActivity): { icon: string; text: string } => {
  const tool = humanizeTool(a.name);
  if (a.kind === 'approval') {
    return { icon: '⏳', text: `Waiting for your approval to ${tool.action}` };
  }
  if (a.kind === 'tool_result') {
    if (a.summary) return { icon: a.ok ? '✅' : '⚠️', text: a.summary };
    return { icon: a.ok ? '✅' : '⚠️', text: a.ok ? `${tool.label} — done` : `${tool.label} — couldn't complete` };
  }
  return { icon: tool.icon, text: tool.label };
};

const ToolActivityLog: React.FC<{ activities: ChatActivity[] }> = ({ activities }) => {
  const navigate = useNavigate();
  const [open, setOpen] = useState(false);

  const approvals = activities.filter((a) => a.kind === 'approval');
  const steps = activities.filter((a) => a.kind !== 'approval');

  return (
    <Box sx={{ width: '100%' }}>
      {/* Anything waiting on the user stays visible — it's actionable. */}
      {approvals.length > 0 && (
        <Paper
          variant="outlined"
          sx={{
            display: 'flex',
            alignItems: 'center',
            gap: 1,
            px: 1.5,
            py: 1,
            mb: 0.75,
            borderRadius: 2,
            borderColor: 'warning.light',
            bgcolor: (t) => alpha(t.palette.warning.main, 0.08),
          }}
        >
          <Box component="span" sx={{ fontSize: '1rem', lineHeight: 1 }}>⏳</Box>
          <Typography variant="body2" sx={{ flex: 1, color: 'text.secondary' }}>
            {approvals.length === 1
              ? 'One step needs your approval before I can finish.'
              : `${approvals.length} steps need your approval before I can finish.`}
          </Typography>
          <Button size="small" variant="text" onClick={() => navigate('/copilot/approvals')} sx={{ fontWeight: 700, flexShrink: 0 }}>
            Review
          </Button>
        </Paper>
      )}

      {/* Everything else is calm, secondary detail — tucked away by default. */}
      {steps.length > 0 && (
        <Box>
          <ButtonBase
            onClick={() => setOpen((v) => !v)}
            aria-expanded={open}
            sx={{
              display: 'flex',
              alignItems: 'center',
              gap: 0.5,
              px: 0.5,
              py: 0.25,
              borderRadius: 1,
              color: 'text.secondary',
              '&:hover': { color: 'text.primary' },
            }}
          >
            <ExpandIcon sx={{ fontSize: 18, transform: open ? 'rotate(180deg)' : 'none', transition: 'transform 0.2s' }} />
            <Typography variant="caption" sx={{ fontWeight: 600 }}>
              {open ? 'Hide details' : `Show what I did (${steps.length})`}
            </Typography>
          </ButtonBase>
          <Collapse in={open}>
            <Stack spacing={0.5} sx={{ pl: 0.5, pt: 0.75 }}>
              {steps.map((a) => {
                const line = activityLine(a);
                return (
                  <Stack key={a.id} direction="row" spacing={1} sx={{ alignItems: 'flex-start' }}>
                    <Box component="span" sx={{ fontSize: '0.9rem', lineHeight: 1.5, flexShrink: 0 }}>{line.icon}</Box>
                    <Typography variant="body2" color="text.secondary" sx={{ lineHeight: 1.5 }}>
                      {line.text}
                    </Typography>
                  </Stack>
                );
              })}
            </Stack>
          </Collapse>
        </Box>
      )}
    </Box>
  );
};

// ─── Message bubble ─────────────────────────────────────────────────────────

const MessageBubble: React.FC<{ message: ChatMessage }> = ({ message }) => {
  const isUser = message.role === 'user';
  const working = !isUser && message.streaming && !message.content;
  // While thinking with nothing written yet, show a gentle live status line.
  const liveStep = working
    ? [...message.activities].reverse().find((a) => a.kind !== 'approval')
    : undefined;

  return (
    <Box sx={{ display: 'flex', gap: 1.5, flexDirection: isUser ? 'row-reverse' : 'row' }}>
      <Avatar
        sx={{
          width: 34,
          height: 34,
          bgcolor: isUser ? 'primary.main' : 'secondary.main',
          background: isUser ? undefined : 'linear-gradient(135deg, #4682B4 0%, #0ea5e9 100%)',
          flexShrink: 0,
        }}
      >
        {isUser ? <PersonIcon sx={{ fontSize: 19 }} /> : <SparkleIcon sx={{ fontSize: 19 }} />}
      </Avatar>
      <Box sx={{ maxWidth: '82%', display: 'flex', flexDirection: 'column', gap: 0.75, alignItems: isUser ? 'flex-end' : 'flex-start' }}>
        {!isUser && message.activities.length > 0 && <ToolActivityLog activities={message.activities} />}

        {working && (
          <Stack direction="row" spacing={1} sx={{ alignItems: 'center', px: 0.5, py: 0.5 }}>
            <CircularProgress size={14} thickness={5} />
            <Typography variant="body2" color="text.secondary">
              {liveStep ? `${humanizeTool(liveStep.name).icon} ${humanizeTool(liveStep.name).label}…` : 'Thinking…'}
            </Typography>
          </Stack>
        )}

        {(message.content || (message.streaming && !working)) && (
          <Paper
            elevation={0}
            sx={{
              px: 1.75,
              py: 1.25,
              borderRadius: 2.5,
              border: '1px solid',
              borderColor: isUser ? 'transparent' : 'divider',
              bgcolor: isUser ? 'primary.main' : 'background.paper',
              color: isUser ? 'primary.contrastText' : 'text.primary',
              background: isUser ? (theme) => `linear-gradient(135deg, ${theme.palette.primary.main} 0%, ${theme.palette.primary.dark} 100%)` : undefined,
              whiteSpace: 'pre-wrap',
              wordBreak: 'break-word',
              borderTopRightRadius: isUser ? 4 : 20,
              borderTopLeftRadius: isUser ? 20 : 4,
            }}
          >
            <Typography variant="body2" sx={{ lineHeight: 1.6, color: message.errored ? 'error.main' : 'inherit' }}>
              {message.content}
              {message.streaming && (
                <Box component="span" sx={{ display: 'inline-block', width: 8, height: 15, ml: 0.25, bgcolor: 'text.secondary', borderRadius: 0.5, animation: 'copilotBlink 1s steps(2) infinite', verticalAlign: 'middle' }} />
              )}
            </Typography>
          </Paper>
        )}
      </Box>
    </Box>
  );
};

// ─── Main page ──────────────────────────────────────────────────────────────

const CopilotPage: React.FC = () => {
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const { enqueueSnackbar } = useSnackbar();

  const [sessionId, setSessionId] = useState<string | undefined>(undefined);
  const [messages, setMessages] = useState<ChatMessage[]>([]);
  const [input, setInput] = useState('');
  const [streaming, setStreaming] = useState(false);

  const abortRef = useRef<AbortController | null>(null);
  const scrollRef = useRef<HTMLDivElement | null>(null);
  const assistantIdRef = useRef<string | null>(null);

  const { data: sessions = [], isLoading: sessionsLoading } = useQuery<AgentSessionSummary[]>({
    queryKey: ['agent-sessions'],
    queryFn: copilotService.getSessions,
  });

  // Auto-scroll to bottom whenever the conversation grows or streams.
  useEffect(() => {
    scrollRef.current?.scrollTo({ top: scrollRef.current.scrollHeight, behavior: 'smooth' });
  }, [messages]);

  useEffect(() => () => abortRef.current?.abort(), []);

  const patchAssistant = useCallback((updater: (m: ChatMessage) => ChatMessage) => {
    const id = assistantIdRef.current;
    if (!id) return;
    setMessages((prev) => prev.map((m) => (m.id === id ? updater(m) : m)));
  }, []);

  const handleEvent = useCallback(
    (event: AgentStreamEvent) => {
      switch (event.type) {
        case 'session':
          setSessionId((cur) => cur ?? event.sessionId);
          break;
        case 'token':
          patchAssistant((m) => ({ ...m, content: m.content + event.text }));
          break;
        case 'tool_call':
          patchAssistant((m) => ({
            ...m,
            activities: [
              ...m.activities,
              { id: nextId(), kind: 'tool_call', name: event.name },
            ],
          }));
          break;
        case 'tool_result':
          patchAssistant((m) => ({
            ...m,
            activities: [
              ...m.activities,
              { id: nextId(), kind: 'tool_result', name: event.name, ok: event.ok, summary: event.summary },
            ],
          }));
          break;
        case 'approval_required':
          patchAssistant((m) => ({
            ...m,
            activities: [
              ...m.activities,
              { id: nextId(), kind: 'approval', name: event.toolName, summary: event.summary, approvalId: event.approvalId },
            ],
          }));
          break;
        case 'done':
          patchAssistant((m) => ({ ...m, streaming: false }));
          break;
        case 'error':
          patchAssistant((m) => ({ ...m, streaming: false, errored: true, content: m.content || event.message }));
          enqueueSnackbar(event.message || 'Something went wrong', { variant: 'error' });
          break;
      }
    },
    [patchAssistant, enqueueSnackbar],
  );

  const send = useCallback(
    (text: string) => {
      const trimmed = text.trim();
      if (!trimmed || streaming) return;

      const userMsg: ChatMessage = { id: nextId(), role: 'user', content: trimmed, activities: [] };
      const assistantMsg: ChatMessage = { id: nextId(), role: 'assistant', content: '', activities: [], streaming: true };
      assistantIdRef.current = assistantMsg.id;

      setMessages((prev) => [...prev, userMsg, assistantMsg]);
      setInput('');
      setStreaming(true);

      const controller = new AbortController();
      abortRef.current = controller;

      void copilotService.streamChat(
        { sessionId, message: trimmed },
        {
          signal: controller.signal,
          onEvent: handleEvent,
          onComplete: () => {
            setStreaming(false);
            patchAssistant((m) => ({ ...m, streaming: false }));
            queryClient.invalidateQueries({ queryKey: ['agent-sessions'] });
            queryClient.invalidateQueries({ queryKey: ['agent-approvals'] });
          },
          onError: (err) => {
            setStreaming(false);
            patchAssistant((m) => ({
              ...m,
              streaming: false,
              errored: true,
              content: m.content || `I couldn't reach the assistant just now. Please try again. (${err.message})`,
            }));
            enqueueSnackbar('Lost connection — please try again', { variant: 'error' });
          },
        },
      );
    },
    [sessionId, streaming, handleEvent, patchAssistant, queryClient, enqueueSnackbar],
  );

  const handleKeyDown = (e: React.KeyboardEvent) => {
    if (e.key === 'Enter' && !e.shiftKey) {
      e.preventDefault();
      send(input);
    }
  };

  const startNewChat = () => {
    abortRef.current?.abort();
    setStreaming(false);
    setMessages([]);
    setSessionId(undefined);
    assistantIdRef.current = null;
  };

  const loadSession = async (id: string) => {
    if (streaming) return;
    try {
      const detail = await copilotService.getSession(id);
      const loaded: ChatMessage[] = [];
      let pendingAssistant: ChatMessage | null = null;
      for (const msg of detail.messages) {
        if (msg.role === 'user') {
          if (pendingAssistant) {
            loaded.push(pendingAssistant);
            pendingAssistant = null;
          }
          loaded.push({ id: nextId(), role: 'user', content: msg.content, activities: [] });
        } else if (msg.role === 'tool') {
          if (!pendingAssistant) {
            pendingAssistant = { id: nextId(), role: 'assistant', content: '', activities: [] };
          }
          pendingAssistant.activities.push({
            id: nextId(),
            kind: 'tool_result',
            name: msg.toolName || 'tool',
            ok: true,
            summary: msg.toolResultSummary || msg.content,
          });
        } else {
          if (pendingAssistant) {
            pendingAssistant.content = msg.content;
            loaded.push(pendingAssistant);
            pendingAssistant = null;
          } else {
            loaded.push({ id: nextId(), role: 'assistant', content: msg.content, activities: [] });
          }
        }
      }
      if (pendingAssistant) loaded.push(pendingAssistant);
      setMessages(loaded);
      setSessionId(detail.id);
    } catch {
      enqueueSnackbar('Could not open that conversation', { variant: 'error' });
    }
  };

  const isEmpty = messages.length === 0;

  return (
    <Box sx={{ display: 'flex', gap: 2, height: 'calc(100vh - 128px)', px: 1, py: 1 }}>
      <style>{'@keyframes copilotBlink { 0%,100%{opacity:1} 50%{opacity:0} }'}</style>

      {/* ── Left rail: sessions ── */}
      <Paper
        sx={{
          width: 260,
          flexShrink: 0,
          display: { xs: 'none', md: 'flex' },
          flexDirection: 'column',
          borderRadius: 3,
          overflow: 'hidden',
        }}
      >
        <Box sx={{ p: 1.5 }}>
          <Button fullWidth variant="contained" startIcon={<AddIcon />} onClick={startNewChat} disabled={streaming} sx={{ fontWeight: 700 }}>
            New chat
          </Button>
        </Box>
        <Divider />
        <Box sx={{ px: 1.5, pt: 1.5, pb: 0.5 }}>
          <Typography variant="caption" sx={{ fontWeight: 800, color: 'text.secondary', textTransform: 'uppercase', letterSpacing: '0.08em' }}>
            Recent
          </Typography>
        </Box>
        <Box sx={{ flex: 1, overflowY: 'auto', px: 0.5 }}>
          {sessionsLoading ? (
            <Box sx={{ display: 'flex', justifyContent: 'center', py: 3 }}>
              <CircularProgress size={22} />
            </Box>
          ) : sessions.length === 0 ? (
            <Typography variant="caption" sx={{ display: 'block', px: 1.5, py: 2, color: 'text.disabled', fontStyle: 'italic' }}>
              No conversations yet.
            </Typography>
          ) : (
            <List dense disablePadding>
              {sessions.map((s) => (
                <ListItemButton
                  key={s.id}
                  selected={s.id === sessionId}
                  onClick={() => loadSession(s.id)}
                  sx={{ borderRadius: 2, mx: 0.5, mb: 0.25, alignItems: 'flex-start' }}
                >
                  <ChatIcon sx={{ fontSize: 17, mr: 1, mt: 0.4, color: 'text.secondary' }} />
                  <ListItemText
                    primary={s.title || 'Untitled chat'}
                    secondary={`${s.messageCount} msg · ${dayjs(s.updatedOn).format('MMM D')}`}
                    slotProps={{
                      primary: { noWrap: true, sx: { fontSize: '0.8rem', fontWeight: 600 } },
                      secondary: { sx: { fontSize: '0.68rem' } },
                    }}
                  />
                </ListItemButton>
              ))}
            </List>
          )}
        </Box>
        <Divider />
        <Stack direction="row" spacing={0.5} sx={{ p: 1 }}>
          <Button size="small" fullWidth startIcon={<ApprovalsIcon sx={{ fontSize: 17 }} />} onClick={() => navigate('/copilot/approvals')} sx={{ color: 'text.secondary', fontSize: '0.72rem' }}>
            Approvals
          </Button>
          <Button size="small" fullWidth startIcon={<ActivityIcon sx={{ fontSize: 17 }} />} onClick={() => navigate('/copilot/activity')} sx={{ color: 'text.secondary', fontSize: '0.72rem' }}>
            Activity
          </Button>
        </Stack>
      </Paper>

      {/* ── Main chat column ── */}
      <Paper sx={{ flex: 1, display: 'flex', flexDirection: 'column', borderRadius: 3, overflow: 'hidden', minWidth: 0 }}>
        {/* Header */}
        <Box sx={{ px: 2.5, py: 1.75, borderBottom: '1px solid', borderColor: 'divider', display: 'flex', alignItems: 'center', gap: 1.5 }}>
          <Avatar sx={{ width: 38, height: 38, background: 'linear-gradient(135deg, #4682B4 0%, #0ea5e9 100%)' }}>
            <SparkleIcon />
          </Avatar>
          <Box sx={{ minWidth: 0 }}>
            <Typography variant="subtitle1" sx={{ fontWeight: 800, letterSpacing: '-0.01em', lineHeight: 1.2 }}>
              Sourcing Assistant
            </Typography>
            <Typography variant="caption" color="text.secondary">
              Here to help with your RFQs, suppliers, and orders
            </Typography>
          </Box>
          <Box sx={{ flex: 1 }} />
          <Chip
            size="small"
            icon={<Box sx={{ width: 8, height: 8, borderRadius: '50%', bgcolor: streaming ? 'warning.main' : 'success.main', ml: 1 }} />}
            label={streaming ? 'Working…' : 'Ready'}
            variant="outlined"
            sx={{ fontWeight: 700, fontSize: '0.72rem' }}
          />
        </Box>

        {/* Conversation */}
        <Box
          ref={scrollRef}
          role="log"
          aria-live="polite"
          aria-busy={streaming}
          aria-label="Conversation with your sourcing assistant"
          sx={{ flex: 1, overflowY: 'auto', px: { xs: 1.5, sm: 3 }, py: 2.5 }}
        >
          {isEmpty ? (
            <Box sx={{ height: '100%', display: 'flex', flexDirection: 'column', alignItems: 'center', justifyContent: 'center', textAlign: 'center', gap: 3, py: 4 }}>
              <Avatar sx={{ width: 68, height: 68, background: 'linear-gradient(135deg, #4682B4 0%, #0ea5e9 100%)', boxShadow: (t) => `0 12px 30px -8px ${alpha(t.palette.primary.main, 0.6)}` }}>
                <SparkleIcon sx={{ fontSize: 34 }} />
              </Avatar>
              <Box sx={{ maxWidth: 520 }}>
                <Typography variant="h5" sx={{ fontWeight: 800, letterSpacing: '-0.02em' }}>
                  Hi — I'm your sourcing assistant
                </Typography>
                <Typography variant="body1" color="text.secondary" sx={{ mt: 1 }}>
                  Ask me anything about your RFQs and suppliers, or try one of these:
                </Typography>
              </Box>
              <Box
                sx={{
                  width: '100%',
                  maxWidth: 620,
                  display: 'grid',
                  gap: 1.5,
                  gridTemplateColumns: { xs: '1fr', sm: '1fr 1fr' },
                }}
              >
                {SUGGESTED_PROMPTS.map((p) => (
                  <ButtonBase
                    key={p.text}
                    onClick={() => send(p.text)}
                    sx={{
                      textAlign: 'left',
                      justifyContent: 'flex-start',
                      p: 2,
                      borderRadius: 3,
                      border: '1px solid',
                      borderColor: 'divider',
                      bgcolor: 'background.paper',
                      transition: 'all 0.15s',
                      '&:hover': { borderColor: 'primary.main', bgcolor: 'action.hover', transform: 'translateY(-1px)' },
                    }}
                  >
                    <Box component="span" sx={{ fontSize: '1.4rem', mr: 1.5, lineHeight: 1 }}>{p.icon}</Box>
                    <Typography variant="body2" sx={{ fontWeight: 600, flex: 1 }}>
                      {p.text}
                    </Typography>
                    <ArrowIcon sx={{ fontSize: 18, color: 'text.disabled' }} />
                  </ButtonBase>
                ))}
              </Box>
            </Box>
          ) : (
            <Stack spacing={2.5}>
              {messages.map((m) => (
                <MessageBubble key={m.id} message={m} />
              ))}
            </Stack>
          )}
        </Box>

        {/* Composer */}
        <Box sx={{ px: { xs: 1.5, sm: 2.5 }, py: 1.75, borderTop: '1px solid', borderColor: 'divider' }}>
          <Box sx={{ display: 'flex', gap: 1, alignItems: 'flex-end' }}>
            <TextField
              fullWidth
              multiline
              maxRows={6}
              value={input}
              onChange={(e) => setInput(e.target.value)}
              onKeyDown={handleKeyDown}
              placeholder="Type your question…  (Enter to send, Shift+Enter for a new line)"
              aria-label="Message your sourcing assistant"
              disabled={streaming}
              size="small"
              sx={{ '& .MuiOutlinedInput-root': { borderRadius: 3 } }}
            />
            <Tooltip title={streaming ? 'Working…' : 'Send'}>
              <span>
                <IconButton
                  color="primary"
                  aria-label="Send message"
                  onClick={() => send(input)}
                  disabled={streaming || !input.trim()}
                  sx={{
                    width: 44,
                    height: 44,
                    background: (t) => `linear-gradient(135deg, ${t.palette.primary.main} 0%, ${t.palette.primary.dark} 100%)`,
                    color: 'primary.contrastText',
                    '&:hover': { opacity: 0.92 },
                    '&.Mui-disabled': { background: 'action.disabledBackground', color: 'action.disabled' },
                  }}
                >
                  {streaming ? <CircularProgress size={20} sx={{ color: 'inherit' }} /> : <SendIcon sx={{ fontSize: 20 }} />}
                </IconButton>
              </span>
            </Tooltip>
          </Box>
          <Typography variant="caption" color="text.disabled" sx={{ display: 'block', mt: 0.75, textAlign: 'center' }}>
            I'll always ask first before doing anything sensitive — like emailing a supplier or placing an order.
          </Typography>
        </Box>
      </Paper>
    </Box>
  );
};

export default CopilotPage;
