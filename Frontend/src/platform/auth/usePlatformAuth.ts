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

/**
 * Where the "remember this browser" token lives.
 *
 * localStorage, NOT the sessionStorage the platform TOKEN uses — and the difference is the whole
 * point of the control. The token is a live credential and must die with the tab; this is a
 * statement that a second factor was already proved on THIS MACHINE, and it has to outlive the
 * tab or every new window is a fresh challenge and the control does nothing.
 *
 * It is not a session and grants nothing on its own: presented at login it can only SKIP a
 * challenge for the user it belongs to, the server holds only its SHA-256, and the server-side row
 * carries the expiry and the revocation flag that decide whether it still counts.
 */
const BROWSER_TRUST_KEY = 'nexora_platform_browser_trust';

const readBrowserTrust = (): string | null => {
  try {
    return window.localStorage.getItem(BROWSER_TRUST_KEY);
  } catch {
    // A browser with storage denied simply gets challenged every time, which is the safe side.
    return null;
  }
};

const writeBrowserTrust = (token: string | null) => {
  try {
    if (token) window.localStorage.setItem(BROWSER_TRUST_KEY, token);
    else window.localStorage.removeItem(BROWSER_TRUST_KEY);
  } catch {
    /* storage denied — see above */
  }
};

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
    verification: { totpCode?: string; recoveryCode?: string; rememberBrowser?: boolean },
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
      // Sent unconditionally; the server ignores anything it does not recognise, and a token
      // belonging to a different operator matches nothing because the lookup is scoped by user.
      browserTrustToken: readBrowserTrust(),
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
    verification: { totpCode?: string; recoveryCode?: string; rememberBrowser?: boolean },
  ) => {
    const { data } = await platformHttp.post<Record<string, unknown>>(MFA_CHALLENGE_PATH, {
      challengeId: challenge.challengeId,
      totpCode: verification.totpCode || null,
      recoveryCode: verification.recoveryCode || null,
      rememberBrowser: verification.rememberBrowser === true,
    });
    const token = typeof data?.token === 'string' ? data.token : undefined;
    if (!token) throw new Error('MFA challenge did not return a platform token.');
    // The one and only time the raw trust token exists outside the server. Stored before the
    // session so a failure here cannot leave a browser believing it is trusted when it is not.
    if (typeof data?.browserTrustToken === 'string' && data.browserTrustToken) {
      writeBrowserTrust(data.browserTrustToken);
    }
    setPlatformSession(token, userFromLogin(data, token, challenge.email));
  }, []);

  const platformLogout = useCallback(async () => {
    try {
      await platformHttp.post('/api/platform/auth/logout');
    } finally {
      clearPlatformSession();
      // Signing out ends the session, not the browser's trust: the operator proved a second factor
      // on this machine and that fact is still true. Revoking the trust is a separate, deliberate
      // act (DELETE /api/platform/auth/browser-trusts/{id}) — clearing it here would make "sign
      // out" silently mean "and challenge me again tomorrow", which is not what it says.
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
