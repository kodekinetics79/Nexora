// ---------------------------------------------------------------------------
// Password-reset API (public).
//
// Deliberately its own axios instance, for the same reason
// pages/Activation/activationApi.ts has one. The tenant `axiosInstance` attaches
// a session token and, on a 401, wipes localStorage and hard-navigates to
// /login — behaviour that would be actively wrong here, where the caller is by
// definition not signed in and a rejected token is an ordinary outcome to be
// explained, not a session failure. The platform instance is wrong for the
// opposite reason: it would attach an operator's control-plane token to a
// customer's request.
//
// There is a second, sharper reason on this flow specifically. Somebody using
// the reset form may still have a stale tenant token in localStorage from an
// expired session — that is a common way to end up needing a reset at all. An
// interceptor that reacted to that token would turn a working recovery into a
// redirect loop, at the exact moment the user has no other way in.
// ---------------------------------------------------------------------------

import axios, { isAxiosError } from 'axios';

const passwordResetHttp = axios.create({
  baseURL: import.meta.env.VITE_API_BASE_URL,
  headers: { 'Content-Type': 'application/json' },
});

/**
 * `unavailable` is separated from the four token verdicts on purpose: "we could
 * not check" must never be shown to a customer as "your link is invalid", which
 * sends them to support over a transient outage.
 *
 * The vocabulary matches the activation flow's exactly, because the server
 * deliberately answers with the same words and codes for the same situations.
 */
export type ResetTokenState = 'valid' | 'expired' | 'used' | 'revoked' | 'invalid' | 'unavailable';

export interface ResetChallenge {
  state: ResetTokenState;
  /**
   * Populated ONLY when the token is valid, and always masked ("l***a@acme.com").
   * A rejected token tells the caller nothing about which account it belonged
   * to, so a harvested or guessed link cannot confirm that an address is
   * registered.
   */
  email: string | null;
  firstName: string | null;
  expiresAtUtc: string | null;
}

type ChallengeBody = {
  status?: string | null;
  email?: string | null;
  firstName?: string | null;
  expiresAtUtc?: string | null;
};

const KNOWN_STATES: ResetTokenState[] = ['valid', 'expired', 'used', 'revoked', 'invalid'];

const readState = (value: unknown): ResetTokenState | null => {
  const normalized = typeof value === 'string' ? value.trim().toLowerCase() : '';
  return KNOWN_STATES.find((state) => state === normalized) ?? null;
};

/**
 * Maps the transport outcome onto a verdict. The server names the state in the
 * body; when the body does not survive (a proxy error page, a truncated
 * response) the status code carries it — 404 for a token that does not exist,
 * 410 for one past its expiry, 409 for one already spent, 403 for one
 * superseded by a newer request.
 */
const stateFromStatus = (status: number | undefined): ResetTokenState => {
  if (status === 404) return 'invalid';
  if (status === 410) return 'expired';
  if (status === 409) return 'used';
  if (status === 403) return 'revoked';
  if (status === 400) return 'invalid';
  return 'unavailable';
};

const rejected = (state: ResetTokenState): ResetChallenge => ({
  state,
  email: null,
  firstName: null,
  expiresAtUtc: null,
});

/**
 * Ask for a reset link.
 *
 * <p>Resolves the same way for every address, and that is the contract, not an
 * implementation detail: the server answers 202 with one fixed body whether or
 * not the address belongs to an account. This function must never grow a return
 * value the page could branch on — the moment the UI says something different
 * for a known address, the whole server-side enumeration defence is undone by
 * the client.</p>
 *
 * <p>Transport failures still reject, because "we could not reach the server" is
 * about US, not about the address, and a user who is offline needs to know their
 * request did not happen.</p>
 */
export const requestPasswordReset = async (email: string): Promise<void> => {
  await passwordResetHttp.post('/api/password-reset/requests', { email });
};

export const inspectResetToken = async (token: string): Promise<ResetChallenge> => {
  try {
    const { data } = await passwordResetHttp.get<ChallengeBody>(
      `/api/password-reset/${encodeURIComponent(token)}`,
    );
    const state = readState(data?.status) ?? 'valid';
    if (state !== 'valid') return rejected(state);
    return {
      state: 'valid',
      email: data?.email ?? null,
      firstName: data?.firstName ?? null,
      expiresAtUtc: data?.expiresAtUtc ?? null,
    };
  } catch (error) {
    if (!isAxiosError(error)) return rejected('unavailable');
    const body = error.response?.data as ChallengeBody | undefined;
    return rejected(readState(body?.status) ?? stateFromStatus(error.response?.status));
  }
};

export interface ResetOutcome {
  ok: boolean;
  /** Set when the attempt failed for a reason the page can explain in its own words. */
  state: ResetTokenState | null;
}

export const completePasswordReset = async (
  token: string,
  password: string,
): Promise<ResetOutcome> => {
  try {
    await passwordResetHttp.post(`/api/password-reset/${encodeURIComponent(token)}`, { password });
    return { ok: true, state: null };
  } catch (error) {
    if (!isAxiosError(error)) throw error;
    const body = error.response?.data as ChallengeBody | undefined;
    const state = readState(body?.status) ?? stateFromStatus(error.response?.status);
    // A token verdict is a state the page can explain in its own words. Anything
    // else (a rejected password, a rate limit, a server fault) is re-thrown so
    // the caller renders the server's own reason through the error-presentation
    // boundary. A 400 carrying no `status` is a password the policy refused —
    // stateFromStatus maps it to 'invalid', which is why 'invalid' is re-thrown
    // rather than rendered as a dead link.
    if (state === 'unavailable' || state === 'invalid') throw error;
    return { ok: false, state };
  }
};
