// ---------------------------------------------------------------------------
// Account-activation API (public).
//
// Deliberately its own axios instance. The tenant `axiosInstance` attaches a
// session token and, on a 401, wipes localStorage and hard-navigates to /login —
// behaviour that would be actively wrong here, where the caller is by definition
// not signed in and a rejected token is an ordinary outcome to be explained, not
// a session failure. The platform instance is wrong for the opposite reason: it
// would attach an operator's control-plane token to a customer's request.
// ---------------------------------------------------------------------------

import axios, { isAxiosError } from 'axios';

const activationHttp = axios.create({
  baseURL: import.meta.env.VITE_API_BASE_URL,
  headers: { 'Content-Type': 'application/json' },
});

/**
 * `unavailable` is separated from the four token verdicts on purpose: "we could
 * not check" must never be shown to a customer as "your link is invalid", which
 * sends them to support over a transient outage.
 */
export type ActivationState = 'valid' | 'expired' | 'used' | 'revoked' | 'invalid' | 'unavailable';

export interface ActivationChallenge {
  state: ActivationState;
  /**
   * Populated ONLY when the token is valid. A rejected token tells the caller
   * nothing about which account it belonged to, so a harvested or guessed link
   * cannot be used to confirm that an address is registered.
   */
  email: string | null;
  tenantName: string | null;
  expiresAtUtc: string | null;
}

type ChallengeBody = {
  status?: string | null;
  email?: string | null;
  tenantName?: string | null;
  expiresAtUtc?: string | null;
};

const KNOWN_STATES: ActivationState[] = ['valid', 'expired', 'used', 'revoked', 'invalid'];

const readState = (value: unknown): ActivationState | null => {
  const normalized = typeof value === 'string' ? value.trim().toLowerCase() : '';
  return KNOWN_STATES.find((state) => state === normalized) ?? null;
};

/**
 * Maps the transport outcome onto a verdict. The server may name the state in
 * the body; when it does not, the status code carries it — 404 for a token that
 * does not exist, 410 for one that is past its expiry, 409 for one already
 * spent, 403 for one an operator withdrew.
 */
const stateFromStatus = (status: number | undefined): ActivationState => {
  if (status === 404) return 'invalid';
  if (status === 410) return 'expired';
  if (status === 409) return 'used';
  if (status === 403) return 'revoked';
  if (status === 400) return 'invalid';
  return 'unavailable';
};

export const inspectActivationToken = async (token: string): Promise<ActivationChallenge> => {
  try {
    const { data } = await activationHttp.get<ChallengeBody>(
      `/api/tenant-activation/${encodeURIComponent(token)}`,
    );
    const state = readState(data?.status) ?? 'valid';
    if (state !== 'valid') {
      return { state, email: null, tenantName: null, expiresAtUtc: null };
    }
    return {
      state: 'valid',
      email: data?.email ?? null,
      tenantName: data?.tenantName ?? null,
      expiresAtUtc: data?.expiresAtUtc ?? null,
    };
  } catch (error) {
    if (!isAxiosError(error)) return { state: 'unavailable', email: null, tenantName: null, expiresAtUtc: null };
    const body = error.response?.data as ChallengeBody | undefined;
    const state = readState(body?.status) ?? stateFromStatus(error.response?.status);
    return { state, email: null, tenantName: null, expiresAtUtc: null };
  }
};

export interface ActivationOutcome {
  ok: boolean;
  /** Set when the attempt failed for a reason the customer can act on. */
  state: ActivationState | null;
}

export const completeActivation = async (token: string, password: string): Promise<ActivationOutcome> => {
  try {
    await activationHttp.post(`/api/tenant-activation/${encodeURIComponent(token)}`, { password });
    return { ok: true, state: null };
  } catch (error) {
    if (!isAxiosError(error)) throw error;
    const body = error.response?.data as ChallengeBody | undefined;
    const state = readState(body?.status) ?? stateFromStatus(error.response?.status);
    // A token verdict is a state the page can explain in its own words. Anything
    // else (a rejected password, a server fault) is re-thrown so the caller can
    // render the server's own reason through the error-presentation boundary.
    if (state === 'unavailable' || state === 'invalid') throw error;
    return { ok: false, state };
  }
};
