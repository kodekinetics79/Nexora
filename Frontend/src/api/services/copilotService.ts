import axiosInstance from '../axiosInstance';

// ─── Contract types (authoritative — mirror the backend agent API) ──────────

export type AgentAutonomyLevel = 'Observe' | 'Suggest' | 'Act';

/** Discriminated union of every server-sent event emitted by POST /api/agent/chat. */
export type AgentStreamEvent =
  | { type: 'session'; sessionId: string }
  | { type: 'token'; text: string }
  | { type: 'tool_call'; name: string; input?: unknown }
  | { type: 'tool_result'; name: string; ok: boolean; summary?: string }
  | { type: 'approval_required'; approvalId: string; toolName: string; summary?: string }
  | { type: 'done'; messageId: string }
  | { type: 'error'; message: string };

/** Shape returned when the backend answers with a non-streaming JSON fallback. */
export interface AgentChatJsonFallback {
  sessionId: string;
  reply: string;
  events?: AgentStreamEvent[];
}

export interface AgentSessionSummary {
  id: string;
  title: string;
  updatedOn: string;
  messageCount: number;
}

export type AgentMessageRole = 'user' | 'assistant' | 'tool';

export interface AgentMessage {
  role: AgentMessageRole;
  content: string;
  toolName?: string;
  toolResultSummary?: string;
  createdOn: string;
}

export interface AgentSessionDetail {
  id: string;
  title: string;
  messages: AgentMessage[];
}

export type AgentApprovalStatus = 'pending' | 'approved' | 'rejected';

export interface AgentApproval {
  id: string;
  toolName: string;
  summary: string;
  status: AgentApprovalStatus;
  requestedOn: string;
}

export interface AgentApprovalDecision {
  id: string;
  status: AgentApprovalStatus;
  resultSummary: string;
}

export type AgentAuditDecision = 'executed' | 'held' | 'denied' | string;

export interface AgentAuditEntry {
  id: string;
  actor: string;
  toolName: string;
  decision: AgentAuditDecision;
  summary: string;
  createdOn: string;
}

export interface AgentPolicy {
  autonomyLevel: AgentAutonomyLevel;
  /**
   * The currency the two caps below are denominated in. Null means the tenant has never set
   * one, which suspends auto-approval entirely: the guardrail cannot compare an undenominated
   * cap with anything, so every amount-bearing action routes to a human.
   */
  currencyId: number | null;
  /** Server-computed mirror of `currencyId != null`. */
  capsAreDenominated: boolean;
  maxAutoAwardValue: number;
  maxAutoOrderValue: number;
  requireApprovalForAwards: boolean;
  requireApprovalForOrders: boolean;
  requireApprovalForSupplierEmails: boolean;
}

// ─── Streaming chat ─────────────────────────────────────────────────────────

export interface StreamChatRequest {
  sessionId?: string;
  message: string;
}

export interface StreamChatHandlers {
  /** Invoked for every decoded agent event (streamed or replayed from JSON fallback). */
  onEvent: (event: AgentStreamEvent) => void;
  /** Fired once the stream (or fallback) completes without a transport error. */
  onComplete?: () => void;
  /** Transport/parse-level failure (network drop, non-2xx, unreadable body). */
  onError?: (error: Error) => void;
  /** AbortSignal so callers can cancel an in-flight stream. */
  signal?: AbortSignal;
}

const API_BASE = (import.meta.env.VITE_API_BASE_URL ?? '').replace(/\/$/, '');

/**
 * Consume POST /api/agent/chat.
 *
 * Uses fetch()+ReadableStream (NOT EventSource) so we can attach the bearer
 * token in the Authorization header — the token is never placed anywhere else.
 * The SSE body is buffered and split on the blank-line (`\n\n`) event delimiter
 * so partial network chunks never truncate a JSON payload. If the server
 * instead returns `application/json`, we treat it as a one-shot fallback and
 * replay its `events` (or synthesize token/done from `reply`).
 */
async function streamChat(
  { sessionId, message }: StreamChatRequest,
  handlers: StreamChatHandlers,
): Promise<void> {
  const { onEvent, onComplete, onError, signal } = handlers;
  const token = localStorage.getItem('token');

  try {
    const res = await fetch(`${API_BASE}/api/agent/chat`, {
      method: 'POST',
      headers: {
        'Content-Type': 'application/json',
        Accept: 'text/event-stream',
        ...(token ? { Authorization: `Bearer ${token}` } : {}),
      },
      body: JSON.stringify({ sessionId, message }),
      signal,
    });

    if (!res.ok) {
      throw new Error(`Agent request failed (${res.status})`);
    }

    const contentType = res.headers.get('Content-Type') ?? '';

    // ── Non-streaming JSON fallback ──
    if (contentType.includes('application/json')) {
      const data = (await res.json()) as AgentChatJsonFallback;
      if (data.sessionId) {
        onEvent({ type: 'session', sessionId: data.sessionId });
      }
      if (Array.isArray(data.events) && data.events.length > 0) {
        for (const ev of data.events) onEvent(ev);
      } else if (typeof data.reply === 'string') {
        onEvent({ type: 'token', text: data.reply });
        onEvent({ type: 'done', messageId: '' });
      }
      onComplete?.();
      return;
    }

    // ── SSE stream ──
    if (!res.body) {
      throw new Error('Agent response has no readable body');
    }

    const reader = res.body.getReader();
    const decoder = new TextDecoder();
    let buffer = '';

    const flushEvent = (raw: string) => {
      // A single SSE event may span multiple `data:` lines; concatenate them.
      const dataLines = raw
        .split('\n')
        .map((line) => line.trimStart())
        .filter((line) => line.startsWith('data:'))
        .map((line) => line.slice(5).trimStart());

      if (dataLines.length === 0) return;
      const payload = dataLines.join('\n').trim();
      if (!payload || payload === '[DONE]') return;

      try {
        onEvent(JSON.parse(payload) as AgentStreamEvent);
      } catch {
        // Ignore malformed keep-alive/comment frames rather than killing the stream.
      }
    };

    while (true) {
      const { done, value } = await reader.read();
      if (done) break;
      buffer += decoder.decode(value, { stream: true });

      let delimiterIndex: number;
      // Events are separated by a blank line. Handle both \n\n and \r\n\r\n.
      while (
        (delimiterIndex = buffer.search(/\r?\n\r?\n/)) !== -1
      ) {
        const rawEvent = buffer.slice(0, delimiterIndex);
        const match = buffer.slice(delimiterIndex).match(/^\r?\n\r?\n/);
        buffer = buffer.slice(delimiterIndex + (match ? match[0].length : 2));
        flushEvent(rawEvent);
      }
    }

    // Flush any trailing event with no final delimiter.
    buffer += decoder.decode();
    if (buffer.trim()) flushEvent(buffer);

    onComplete?.();
  } catch (err) {
    if ((err as Error)?.name === 'AbortError') return;
    onError?.(err instanceof Error ? err : new Error(String(err)));
  }
}

// ─── REST endpoints ─────────────────────────────────────────────────────────

const copilotService = {
  streamChat,

  getSessions: async (): Promise<AgentSessionSummary[]> => {
    const res = await axiosInstance.get<AgentSessionSummary[]>('/api/agent/sessions');
    return res.data;
  },

  getSession: async (id: string): Promise<AgentSessionDetail> => {
    const res = await axiosInstance.get<AgentSessionDetail>(`/api/agent/sessions/${id}`);
    return res.data;
  },

  getApprovals: async (status: AgentApprovalStatus = 'pending'): Promise<AgentApproval[]> => {
    const res = await axiosInstance.get<AgentApproval[]>('/api/agent/approvals', {
      params: { status },
    });
    return res.data;
  },

  approve: async (id: string): Promise<AgentApprovalDecision> => {
    const res = await axiosInstance.post<AgentApprovalDecision>(`/api/agent/approvals/${id}/approve`);
    return res.data;
  },

  reject: async (id: string): Promise<AgentApprovalDecision> => {
    const res = await axiosInstance.post<AgentApprovalDecision>(`/api/agent/approvals/${id}/reject`);
    return res.data;
  },

  getAudit: async (take = 100): Promise<AgentAuditEntry[]> => {
    const res = await axiosInstance.get<AgentAuditEntry[]>('/api/agent/audit', {
      params: { take },
    });
    return res.data;
  },

  getPolicy: async (): Promise<AgentPolicy> => {
    const res = await axiosInstance.get<AgentPolicy>('/api/agent/policy');
    return res.data;
  },

  updatePolicy: async (body: AgentPolicy): Promise<AgentPolicy> => {
    const res = await axiosInstance.put<AgentPolicy>('/api/agent/policy', body);
    return res.data;
  },
};

export default copilotService;
