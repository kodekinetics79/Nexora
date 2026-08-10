// ---------------------------------------------------------------------------
// Platform (owner) session store
//
// The platform console sits ABOVE tenants and is gated on a DEDICATED platform
// JWT (audience `nexora-platform`, scope `platform`) issued by
// `POST /api/platform/auth/login`. This token is SEPARATE from the tenant
// session token (`localStorage['token']`) and is deliberately kept in its own
// `sessionStorage` key so the two never collide and a tenant logout can't strip
// platform scope (and vice-versa).
//
// This module is the single source of truth for that session. It exposes a tiny
// external store (subscribe / getSnapshot) so React components re-render when
// the session flips, PLUS plain getters the platform axios instance uses to
// attach the token and clear the session on a 401 — without importing React.
// ---------------------------------------------------------------------------

import { jwtDecode } from 'jwt-decode';

// Dedicated storage keys — kept SEPARATE from the tenant token (`token`).
const TOKEN_KEY = 'nexora_platform_token';
const USER_KEY = 'nexora_platform_user';
const CHANNEL_NAME = 'nexora-platform-session-v1';

type SessionMessage =
  | { type: 'session-request'; source: string; nonce: string }
  | { type: 'session-response'; source: string; target: string; nonce: string; token: string; user: PlatformSessionUser }
  | { type: 'session-updated'; source: string; token: string; user: PlatformSessionUser }
  | { type: 'session-cleared'; source: string };

let cachedUserRaw: string | null = null;
let cachedUser: PlatformSessionUser | null = null;
const tabId = typeof crypto !== 'undefined' && typeof crypto.randomUUID === 'function'
  ? crypto.randomUUID()
  : `${Date.now()}-${Math.random()}`;
let channel: BroadcastChannel | null = null;
let pendingNonce: string | null = null;

export interface PlatformSessionUser {
  id?: string;
  email: string;
  name?: string;
  role?: string;
}

// --- external store plumbing ------------------------------------------------

const listeners = new Set<() => void>();
const emit = () => listeners.forEach((l) => l());

export const subscribePlatformSession = (cb: () => void): (() => void) => {
  listeners.add(cb);
  // Cross-tab sync: another tab logging in/out updates sessionStorage.
  window.addEventListener('storage', cb);
  return () => {
    listeners.delete(cb);
    window.removeEventListener('storage', cb);
  };
};

// --- token expiry -----------------------------------------------------------

const isTokenExpired = (token: string): boolean => {
  try {
    const { exp } = jwtDecode<{ exp?: number }>(token);
    if (typeof exp !== 'number') return false; // no exp claim → don't force-expire
    return Date.now() >= exp * 1000;
  } catch {
    // Not a decodable JWT — treat as usable and let the server 401 if invalid.
    return false;
  }
};

// --- reads ------------------------------------------------------------------

export const getPlatformToken = (): string | null => {
  const token = sessionStorage.getItem(TOKEN_KEY);
  if (token && isTokenExpired(token)) {
    // Expired on read — clear so callers never send a doomed request.
    clearPlatformSession();
    return null;
  }
  return token;
};

export const getPlatformUser = (): PlatformSessionUser | null => {
  const raw = sessionStorage.getItem(USER_KEY);
  if (!raw) return null;
  if (raw === cachedUserRaw) return cachedUser;

  try {
    cachedUser = JSON.parse(raw) as PlatformSessionUser;
    cachedUserRaw = raw;
    return cachedUser;
  } catch {
    cachedUser = null;
    cachedUserRaw = raw;
    return null;
  }
};

/** Snapshot used by `useSyncExternalStore` — true when a live token is present. */
export const getPlatformAuthedSnapshot = (): boolean => getPlatformToken() !== null;

/**
 * Whether the live platform token carries `amr=mfa`.
 *
 * Sec-D2: this is the CLIENT MIRROR of `PlatformPolicies.PlatformScope`, which requires that
 * exact claim. It is read from the same token the server will read, so the two cannot drift into
 * disagreement the way a separate "hasEnrolledMfa" boolean on the login body would.
 *
 * It is not a security control — the server is — it is what stops a password-only operator being
 * dropped into a console where every screen answers 403 with nothing saying which one to open.
 * Anything unreadable answers FALSE, so a malformed or absent token routes to enrollment rather
 * than into the console.
 */
export const getPlatformMfaAuthenticatedSnapshot = (): boolean => {
  const token = getPlatformToken();
  if (!token) return false;
  try {
    const { amr } = jwtDecode<{ amr?: string | string[] }>(token);
    if (typeof amr === 'string') return amr === 'mfa';
    return Array.isArray(amr) && amr.includes('mfa');
  } catch {
    return false;
  }
};

// --- writes -----------------------------------------------------------------

export const setPlatformSession = (token: string, user: PlatformSessionUser): void => {
  persistPlatformSession(token, user);
  channel?.postMessage({ type: 'session-updated', source: tabId, token, user } satisfies SessionMessage);
};

const persistPlatformSession = (token: string, user: PlatformSessionUser): void => {
  const userRaw = JSON.stringify(user);
  sessionStorage.setItem(TOKEN_KEY, token);
  sessionStorage.setItem(USER_KEY, userRaw);
  cachedUserRaw = userRaw;
  cachedUser = user;
  emit();
};

export const clearPlatformSession = (): void => {
  clearPersistedPlatformSession();
  channel?.postMessage({ type: 'session-cleared', source: tabId } satisfies SessionMessage);
};

const clearPersistedPlatformSession = (): void => {
  sessionStorage.removeItem(TOKEN_KEY);
  sessionStorage.removeItem(USER_KEY);
  cachedUserRaw = null;
  cachedUser = null;
  emit();
};

// BroadcastChannel is same-origin by browser contract. It lets a newly-opened tab
// request the current platform session without weakening storage to localStorage.
// A targeted, single-use nonce prevents an unrelated/stale response from being
// accepted. The server-side PlatformSession ledger remains authoritative: every
// transferred token is still live-validated on its next API request.
const startCrossTabBridge = (): void => {
  if (typeof window === 'undefined' || typeof BroadcastChannel === 'undefined') return;

  channel = new BroadcastChannel(CHANNEL_NAME);
  channel.addEventListener('message', (event: MessageEvent<unknown>) => {
    const message = event.data as Partial<SessionMessage> | null;
    if (!message || typeof message !== 'object' || message.source === tabId) return;

    if (message.type === 'session-request'
      && typeof message.source === 'string'
      && typeof message.nonce === 'string') {
      const token = getPlatformToken();
      const user = getPlatformUser();
      if (token && user) {
        channel?.postMessage({
          type: 'session-response', source: tabId, target: message.source,
          nonce: message.nonce, token, user,
        } satisfies SessionMessage);
      }
      return;
    }

    if (message.type === 'session-response'
      && message.target === tabId
      && message.nonce === pendingNonce
      && typeof message.token === 'string'
      && message.user && typeof message.user === 'object') {
      pendingNonce = null;
      persistPlatformSession(message.token, message.user as PlatformSessionUser);
      return;
    }

    if (message.type === 'session-updated'
      && typeof message.token === 'string'
      && message.user && typeof message.user === 'object') {
      persistPlatformSession(message.token, message.user as PlatformSessionUser);
      return;
    }

    if (message.type === 'session-cleared') clearPersistedPlatformSession();
  });

  if (!sessionStorage.getItem(TOKEN_KEY)) {
    pendingNonce = typeof crypto !== 'undefined' && typeof crypto.randomUUID === 'function'
      ? crypto.randomUUID()
      : `${Date.now()}-${Math.random()}`;
    channel.postMessage({ type: 'session-request', source: tabId, nonce: pendingNonce } satisfies SessionMessage);
  }
};

startCrossTabBridge();

// --- login response → session user -----------------------------------------

/**
 * Best-effort extraction of a display user from the login response body and/or
 * the platform JWT. The backend is only contractually guaranteed to return a
 * `token`; anything else is opportunistic so the UI can show who is signed in.
 */
export const userFromLogin = (
  body: Record<string, unknown>,
  token: string,
  fallbackEmail: string,
): PlatformSessionUser => {
  let claims: Record<string, unknown> = {};
  try {
    claims = jwtDecode<Record<string, unknown>>(token);
  } catch {
    claims = {};
  }

  const pick = (...vals: unknown[]): string | undefined =>
    vals.find((v): v is string => typeof v === 'string' && v.length > 0);

  const bodyUser = (body.user ?? {}) as Record<string, unknown>;

  return {
    id: pick(body.id, bodyUser.id, claims.sub),
    email: pick(body.email, bodyUser.email, claims.email, fallbackEmail) ?? fallbackEmail,
    name: pick(body.name, body.userName, bodyUser.name, claims.name),
    // No fallback role. The console gates destructive, billing and support controls on
    // this value, and inventing "Platform Owner" for a response that never named a role
    // would show an operator an authority the server will refuse.
    role: pick(body.platformRole, body.role, bodyUser.role, claims.platformRole, claims.role),
  };
};
