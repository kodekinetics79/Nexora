// ---------------------------------------------------------------------------
// Platform-scoped axios instance
//
// Every `/api/platform/*` request goes through THIS instance — never the tenant
// `axiosInstance`. It attaches the dedicated PLATFORM token (scope=platform)
// from `platformSession`, NOT the tenant token, so the two auth contexts stay
// completely isolated.
//
// On a 401 it clears the platform session. Because `PlatformGuard` is wired to
// the same session store, that clear causes the guard to re-render the platform
// login screen in place — no full-page reload, so no redirect loop.
// ---------------------------------------------------------------------------

import axios from 'axios';
import {
  clearPlatformSession,
  getPlatformToken,
} from '../auth/platformSession';

const platformHttp = axios.create({
  // Same backend host as the tenant app; platform routes live under /api/platform.
  baseURL: import.meta.env.VITE_API_BASE_URL,
  headers: {
    'Content-Type': 'application/json',
  },
});

// The platform login call itself must NOT carry a (possibly stale) token and
// must NOT be force-logged-out on a 401 — a bad password is a normal failure.
const LOGIN_PATH = '/api/platform/auth/login';

/**
 * Endpoints where a 401 means "that credential was wrong", NOT "your session has ended".
 *
 * These CHECK a secret rather than being authorised by one, so treating their 401 as a dead
 * session signs the operator out for a typo. `reauthenticate` is the step-up on a purge, an
 * export or an invoice finalisation: one mistyped character used to close the confirmation
 * dialog, discard the reason and the typed tenant name, and drop the operator on the sign-in
 * screen — which reads as the console crashing, on the most dangerous screen it has. MFA
 * enrollment confirmation returns 401 for a wrong six-digit code and had the same problem.
 *
 * The session is genuinely gone only when a 401 comes back from a route that was USING it.
 */
const CREDENTIAL_CHECK_PATHS = [
  LOGIN_PATH,
  '/api/platform/auth/reauthenticate',
  '/api/platform/auth/mfa/challenge',
  '/api/platform/auth/mfa/enrollment/confirm',
];

platformHttp.interceptors.request.use(
  (config) => {
    const url = config.url ?? '';
    if (!url.includes(LOGIN_PATH)) {
      const token = getPlatformToken();
      if (token) {
        config.headers.Authorization = `Bearer ${token}`;
      }
    }
    return config;
  },
  (error) => Promise.reject(error),
);

platformHttp.interceptors.response.use(
  (response) => response,
  (error) => {
    const status = error.response?.status;
    const url: string = error.config?.url ?? '';
    const isCredentialCheck = CREDENTIAL_CHECK_PATHS.some((path) => url.includes(path));

    // A 401 on any authed platform call means the platform token is gone or
    // invalid. Clear the session; the guard re-renders the login screen in
    // place (the store emit drives it) — no navigation, no reload loop.
    if (status === 401 && !isCredentialCheck) {
      clearPlatformSession();
    }
    return Promise.reject(error);
  },
);

export default platformHttp;
