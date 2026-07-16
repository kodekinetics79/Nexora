// ---------------------------------------------------------------------------
// Platform (owner) authentication
//
// The platform console is gated on a DEDICATED platform JWT (scope=platform,
// audience nexora-platform) obtained from `POST /api/platform/auth/login`. This
// is SEPARATE from the tenant RBAC session used elsewhere in the app.
//
// This hook is a thin React binding over `platformSession` (the storage-backed
// external store) plus the login/logout side-effects. Components read
// `isPlatformAuthed` / `platformUser` and call `platformLogin` / `platformLogout`.
// ---------------------------------------------------------------------------

import { useCallback, useSyncExternalStore } from 'react';
import platformHttp from '../api/platformHttp';
import {
  clearPlatformSession,
  getPlatformAuthedSnapshot,
  getPlatformUser,
  setPlatformSession,
  subscribePlatformSession,
  userFromLogin,
  type PlatformSessionUser,
} from './platformSession';

const LOGIN_PATH = '/api/platform/auth/login';

export interface PlatformAuth {
  /** True when a live platform-scoped token is present. */
  isPlatformAuthed: boolean;
  /** The signed-in platform operator (or null). */
  platformUser: PlatformSessionUser | null;
  /** Authenticate against the platform IdP and store the platform token. */
  platformLogin: (email: string, password: string) => Promise<void>;
  /** Drop the platform session (returns the console to the login screen). */
  platformLogout: () => void;
}

export const usePlatformAuth = (): PlatformAuth => {
  const isPlatformAuthed = useSyncExternalStore(
    subscribePlatformSession,
    getPlatformAuthedSnapshot,
    () => false,
  );
  // Re-derived on every session change (subscribe drives the re-render).
  const platformUser = useSyncExternalStore(
    subscribePlatformSession,
    getPlatformUser,
    () => null,
  );

  const platformLogin = useCallback(async (email: string, password: string) => {
    const { data } = await platformHttp.post<Record<string, unknown>>(LOGIN_PATH, {
      email,
      password,
    });
    const token = typeof data?.token === 'string' ? data.token : undefined;
    if (!token) {
      throw new Error('Platform login did not return a token.');
    }
    setPlatformSession(token, userFromLogin(data, token, email));
  }, []);

  const platformLogout = useCallback(() => {
    clearPlatformSession();
  }, []);

  return { isPlatformAuthed, platformUser, platformLogin, platformLogout };
};
