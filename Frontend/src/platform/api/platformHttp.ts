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
    const isLogin = url.includes(LOGIN_PATH);

    // A 401 on any authed platform call means the platform token is gone or
    // invalid. Clear the session; the guard re-renders the login screen in
    // place (the store emit drives it) — no navigation, no reload loop.
    if (status === 401 && !isLogin) {
      clearPlatformSession();
    }
    return Promise.reject(error);
  },
);

export default platformHttp;
