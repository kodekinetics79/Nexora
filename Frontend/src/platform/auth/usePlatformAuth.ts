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
  getPlatformMfaAuthenticatedSnapshot,
  getPlatformUser,
  setPlatformSession,
  subscribePlatformSession,
  userFromLogin,
  type PlatformSessionUser,
} from './platformSession';

const LOGIN_PATH = '/api/platform/auth/login';
const MFA_CHALLENGE_PATH = '/api/platform/auth/mfa/challenge';

export interface PlatformMfaChallenge {
  challengeId: string;
  expiresAtUtc: string;
  email: string;
}

export type PlatformLoginResult =
  /**
   * Signed in. `mfaEnrollmentRequired` is true when the operator has never enrolled a second
   * factor: the token is real but the server (PlatformPolicies.Enrollment) will refuse it on
   * every platform screen except the MFA panel, so the console must send them there rather than
   * to a dashboard that would answer 403 on every tile with nothing saying why.
   */
  | { mfaRequired: false; mfaEnrollmentRequired: boolean }
  | { mfaRequired: true; challenge: PlatformMfaChallenge };

export interface PlatformAuth {
  /** True when a live platform-scoped token is present. */
  isPlatformAuthed: boolean;
  /**
   * True when that token also carries `amr=mfa`. False means the operator signed in with a
   * password and has never enrolled a second factor: the server (Sec-D2) will refuse every
   * platform endpoint except MFA enrollment and logout, so the console must show the enrollment
   * step rather than the console.
   */
  isPlatformMfaAuthenticated: boolean;
  /** The signed-in platform operator (or null). */
  platformUser: PlatformSessionUser | null;
  /** Authenticate against the platform IdP and store the platform token. */
  platformLogin: (email: string, password: string) => Promise<PlatformLoginResult>;
  platformCompleteMfa: (
    challenge: PlatformMfaChallenge,
    verification: { totpCode?: string; recoveryCode?: string },
  ) => Promise<void>;
  /** Drop the platform session (returns the console to the login screen). */
  platformLogout: () => Promise<void>;
}

export const usePlatformAuth = (): PlatformAuth => {
  const isPlatformAuthed = useSyncExternalStore(
    subscribePlatformSession,
    getPlatformAuthedSnapshot,
    () => false,
  );
  const isPlatformMfaAuthenticated = useSyncExternalStore(
    subscribePlatformSession,
    getPlatformMfaAuthenticatedSnapshot,
    () => false,
  );
  // Re-derived on every session change (subscribe drives the re-render).
  const platformUser = useSyncExternalStore(
    subscribePlatformSession,
    getPlatformUser,
    () => null,
  );

  const platformLogin = useCallback(async (email: string, password: string): Promise<PlatformLoginResult> => {
    const { data } = await platformHttp.post<Record<string, unknown>>(LOGIN_PATH, {
      email,
      password,
    });
    if (data?.mfaRequired === true) {
      const challengeId = typeof data.mfaChallengeId === 'string' ? data.mfaChallengeId : '';
      const expiresAtUtc = typeof data.mfaChallengeExpiresAtUtc === 'string' ? data.mfaChallengeExpiresAtUtc : '';
      if (!challengeId || !expiresAtUtc) throw new Error('Platform login did not return a valid MFA challenge.');
      return { mfaRequired: true, challenge: { challengeId, expiresAtUtc, email } };
    }
    const token = typeof data?.token === 'string' ? data.token : undefined;
    if (!token) {
      throw new Error('Platform login did not return a token.');
    }
    setPlatformSession(token, userFromLogin(data, token, email));
    return { mfaRequired: false, mfaEnrollmentRequired: data?.mfaEnrollmentRequired === true };
  }, []);

  const platformCompleteMfa = useCallback(async (
    challenge: PlatformMfaChallenge,
    verification: { totpCode?: string; recoveryCode?: string },
  ) => {
    const { data } = await platformHttp.post<Record<string, unknown>>(MFA_CHALLENGE_PATH, {
      challengeId: challenge.challengeId,
      totpCode: verification.totpCode || null,
      recoveryCode: verification.recoveryCode || null,
    });
    const token = typeof data?.token === 'string' ? data.token : undefined;
    if (!token) throw new Error('MFA challenge did not return a platform token.');
    setPlatformSession(token, userFromLogin(data, token, challenge.email));
  }, []);

  const platformLogout = useCallback(async () => {
    try {
      await platformHttp.post('/api/platform/auth/logout');
    } finally {
      clearPlatformSession();
    }
  }, []);

  return {
    isPlatformAuthed,
    isPlatformMfaAuthenticated,
    platformUser,
    platformLogin,
    platformCompleteMfa,
    platformLogout,
  };
};
