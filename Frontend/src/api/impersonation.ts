// ---------------------------------------------------------------------------
// Impersonation session record (platform operator → tenant workspace)
//
// When a platform operator impersonates a tenant, the short-lived READ-ONLY
// tenant token is stored here — in its own sessionStorage key — and NEVER in
// the tenant `localStorage['token']`, so a real tenant session in the same
// browser is never clobbered. The tenant axios layer prefers this token while
// the record is present; `ImpersonationBanner` renders the fixed banner and
// the exit path. The token is enforced read-only server-side regardless.
// ---------------------------------------------------------------------------

export const IMPERSONATION_KEY = 'nexora_impersonation';

export interface ImpersonationRecord {
  /** Short-lived read-only tenant token. */
  token: string;
  /** Revocation key (the token's `jti` claim); null when it could not be read. */
  jti: string | null;
  tenantId: string;
  tenantName: string;
  /** ISO expiry of the token. */
  expiresAt: string;
  reason: string;
}

const isExpired = (record: ImpersonationRecord): boolean => {
  const expiry = Date.parse(record.expiresAt);
  return Number.isFinite(expiry) && Date.now() >= expiry;
};

/**
 * Returns the active impersonation record, or null. An expired or unreadable
 * record is cleared on read so no caller ever sends a doomed token.
 */
export const getImpersonation = (): ImpersonationRecord | null => {
  const raw = sessionStorage.getItem(IMPERSONATION_KEY);
  if (!raw) return null;
  try {
    const record = JSON.parse(raw) as ImpersonationRecord;
    if (!record || typeof record.token !== 'string' || isExpired(record)) {
      sessionStorage.removeItem(IMPERSONATION_KEY);
      return null;
    }
    return record;
  } catch {
    sessionStorage.removeItem(IMPERSONATION_KEY);
    return null;
  }
};

export const setImpersonation = (record: ImpersonationRecord): void => {
  sessionStorage.setItem(IMPERSONATION_KEY, JSON.stringify(record));
};

export const clearImpersonation = (): void => {
  sessionStorage.removeItem(IMPERSONATION_KEY);
};
