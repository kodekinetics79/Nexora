import axios from 'axios';
import { clearImpersonation, getImpersonation } from './impersonation';

const axiosInstance = axios.create({
  baseURL: import.meta.env.VITE_API_BASE_URL,
  headers: {
    'Content-Type': 'application/json',
  },
});

// Add interceptor for auth token. When a platform operator is impersonating a
// tenant, the short-lived read-only impersonation token (sessionStorage,
// `nexora_impersonation`) takes precedence over any real tenant session token —
// the real `localStorage['token']` is never touched.
axiosInstance.interceptors.request.use(
  (config) => {
    const impersonation = getImpersonation();
    const token = impersonation?.token ?? localStorage.getItem('token');
    if (token) {
      config.headers.Authorization = `Bearer ${token}`;
    }
    return config;
  },
  (error) => Promise.reject(error)
);

// FE-11: Requests that are part of the login / bootstrap flow must never
// trigger a redirect on 401 — otherwise a failed login or a pre-auth
// bootstrap call would bounce the user around the /login screen in a loop.
const AUTH_EXEMPT_PATHS = ['/api/Auth/Login'];

// Debounce guard: once we start redirecting, ignore every subsequent 401 so a
// burst of parallel background calls (or a reload race) can't loop.
let isRedirectingToLogin = false;

// Add interceptor for handling 401 Unauthorized
axiosInstance.interceptors.response.use(
  (response) => response,
  (error) => {
    const status = error.response?.status;
    const url: string = error.config?.url ?? '';
    const isAuthExempt = AUTH_EXEMPT_PATHS.some((path) => url.includes(path));

    // An expired/revoked impersonation token must end the impersonation and
    // return the operator to the platform console — never bounce to /login or
    // clear a real tenant session that may coexist in localStorage.
    if (status === 401 && getImpersonation()) {
      clearImpersonation();
      window.location.assign('/platform/tenants');
      return Promise.reject(error);
    }

    // Only treat a 401 as a genuine auth-required failure when the user
    // actually holds a session token. A 401 with no token (e.g. a background
    // call that fires on the login screen) must not force a reload.
    const hasSession = !!localStorage.getItem('token');

    if (
      status === 401 &&
      !isAuthExempt &&
      hasSession &&
      !isRedirectingToLogin &&
      window.location.pathname !== '/login'
    ) {
      isRedirectingToLogin = true;
      localStorage.removeItem('token');
      localStorage.removeItem('userData');
      window.location.href = '/login';
    }
    return Promise.reject(error);
  }
);

export default axiosInstance;
