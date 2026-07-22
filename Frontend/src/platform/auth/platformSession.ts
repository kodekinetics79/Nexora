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

let cachedUserRaw: string | null = null;
let cachedUser: PlatformSessionUser | null = null;

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

// --- writes -----------------------------------------------------------------

export const setPlatformSession = (token: string, user: PlatformSessionUser): void => {
  const userRaw = JSON.stringify(user);
  sessionStorage.setItem(TOKEN_KEY, token);
  sessionStorage.setItem(USER_KEY, userRaw);
  cachedUserRaw = userRaw;
  cachedUser = user;
  emit();
};

export const clearPlatformSession = (): void => {
  sessionStorage.removeItem(TOKEN_KEY);
  sessionStorage.removeItem(USER_KEY);
  cachedUserRaw = null;
  cachedUser = null;
  emit();
};

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
    role: pick(body.platformRole, body.role, bodyUser.role, claims.platformRole, claims.role) ?? 'Platform Owner',
  };
};
