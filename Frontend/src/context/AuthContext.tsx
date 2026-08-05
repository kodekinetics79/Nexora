import React, {
  createContext,
  useCallback,
  useContext,
  useRef,
  useState,
  type ReactNode,
  useEffect,
} from "react";
import { jwtDecode } from "jwt-decode";
import { getImpersonation } from "../api/impersonation";
import userService from "../api/services/userService";
import { presentableErrorMessage } from "../utils/apiErrors";

// FE-12: proactively handle JWT expiry instead of waiting for a failed call.
const SESSION_EXPIRED_MESSAGE = "Your session has expired. Please sign in again.";
// Log the user out slightly before the token actually expires to avoid a
// last-second request that would 401.
const EXPIRY_SKEW_MS = 30_000;
// setTimeout clamps delays larger than a 32-bit int; don't schedule past that.
const MAX_TIMEOUT_MS = 2_147_483_647;

// Returns the token's `exp` (in ms since epoch) or null if it can't be read.
const getTokenExpiry = (token: string): number | null => {
  try {
    const { exp } = jwtDecode<{ exp?: number }>(token);
    return typeof exp === "number" ? exp * 1000 : null;
  } catch {
    return null;
  }
};

const isTokenExpired = (token: string): boolean => {
  const expiry = getTokenExpiry(token);
  // No/invalid `exp` claim: don't force a proactive logout — leave the
  // reactive 401 path as the backstop.
  if (expiry === null) return false;
  return Date.now() >= expiry - EXPIRY_SKEW_MS;
};

/**
 * Shape version of the cached `userData` snapshot in localStorage.
 *
 * Bump this whenever the meaning of a cached field changes. A snapshot written by an older build
 * is DISCARDED rather than migrated: v1 permissions had no `canView`, and under the old rules the
 * mere presence of a row meant "can view". Trusting a v1 snapshot under v2 rules would silently
 * grant read on every module the user has any row for.
 */
export const PERMISSION_SCHEMA_VERSION = 2;

/** How long a loaded permission set is considered fresh. Mirrors the server's ~60s RBAC cache. */
export const PERMISSIONS_STALE_MS = 60_000;

export const PERMISSIONS_LOAD_FAILED_MESSAGE =
  "Could not load your permissions. Contact your administrator.";

export interface Permission {
  /** Absent on `/me/permissions` rows — that endpoint returns effective grants, not table rows. */
  id?: number;
  roleId?: number;
  moduleId: number;
  moduleName: string;
  /** Explicit read grant. `undefined` (a pre-v2 payload) is treated as NOT granted. */
  canView?: boolean;
  canCreate?: boolean;
  canEdit?: boolean;
  canDelete?: boolean;
}

export interface UserData {
  id?: number;
  email?: string;
  userName?: string;
  roleId?: number;
  roleName?: string;
  isSuperAdmin?: boolean;
  isManager?: boolean;
  businessUnitId?: number;
  permissions?: Permission[];
  /** Written by `setUserData`; checked on load. See `PERMISSION_SCHEMA_VERSION`. */
  schemaVersion?: number;
}

interface AuthContextType {
  token: string | null;
  userData: UserData;
  setToken: (token: string | null) => void;
  setUserData: (data: UserData) => void;
  logout: () => void;
  hasPermission: (moduleName: string, action?: 'view' | 'create' | 'edit' | 'delete') => boolean;
  /**
   * Human-readable reason the permission set could not be loaded, or null.
   *
   * Non-null means `hasPermission` is answering from an EMPTY set — the UI must say so instead of
   * rendering "Access Denied", which would blame the user for an infrastructure failure.
   */
  permissionsError: string | null;
  permissionsLoading: boolean;
  /** Re-reads `/api/User/me/permissions`. Honours the stale window unless `force` is set. */
  refreshPermissions: (options?: { force?: boolean }) => Promise<void>;
}

const AuthContext = createContext<AuthContextType | undefined>(undefined);

/**
 * Reads the cached snapshot, discarding anything written by a different schema version.
 * Discarding is safe: `refreshPermissions` repopulates the whole snapshot from the server on
 * mount, and until it does `hasPermission` fails closed.
 */
const readCachedUserData = (): UserData => {
  const stored = localStorage.getItem("userData");
  if (!stored) return {};
  try {
    const parsed = JSON.parse(stored) as UserData;
    if (!parsed || typeof parsed !== "object") return {};
    if (parsed.schemaVersion !== PERMISSION_SCHEMA_VERSION) {
      localStorage.removeItem("userData");
      return {};
    }
    return parsed;
  } catch {
    localStorage.removeItem("userData");
    return {};
  }
};

export const AuthProvider: React.FC<{ children: ReactNode }> = ({ children }) => {
  const [token, setTokenState] = useState<string | null>(() => {
    // Platform impersonation: while an impersonation record is active, the
    // read-only impersonation token IS the session for the tenant app. It lives
    // in sessionStorage (never localStorage) so a real tenant session in the
    // same browser is untouched.
    const impersonation = getImpersonation();
    if (impersonation) return impersonation.token;

    // FE-12: if a stored token is already expired on load, clear the session up
    // front so protected routes never render (and never fire doomed requests).
    const stored = localStorage.getItem("token");
    if (stored && isTokenExpired(stored)) {
      localStorage.removeItem("token");
      localStorage.removeItem("userData");
      sessionStorage.setItem("authNotice", SESSION_EXPIRED_MESSAGE);
      return null;
    }
    return stored;
  });
  const [userData, setUserDataState] = useState<UserData>(readCachedUserData);
  const [permissionsError, setPermissionsError] = useState<string | null>(null);
  const [permissionsLoading, setPermissionsLoading] = useState(false);
  /** Epoch ms of the last successful `/me/permissions` read; 0 means "never loaded". */
  const lastLoadedAtRef = useRef(0);
  /** Guards against overlapping loads (StrictMode double-effect, mount + focus in the same tick). */
  const inFlightRef = useRef<Promise<void> | null>(null);
  const tokenRef = useRef<string | null>(token);
  tokenRef.current = token;

  const setToken = (newToken: string | null) => {
    setTokenState(newToken);
    if (newToken) {
      localStorage.setItem("token", newToken);
    } else {
      localStorage.removeItem("token");
    }
  };

  const setUserData = useCallback((data: UserData) => {
    const stamped: UserData = { ...data, schemaVersion: PERMISSION_SCHEMA_VERSION };
    setUserDataState(stamped);
    localStorage.setItem("userData", JSON.stringify(stamped));
  }, []);

  /**
   * Loads identity + grants from the server and replaces the snapshot wholesale.
   *
   * A failure here is NOT swallowed. The old bootstrap logged the error and continued with an
   * empty permission array, which is indistinguishable from "this user legitimately has no
   * access" — the exact reason a whole screen could go blank with no explanation.
   */
  const refreshPermissions = useCallback(async (options?: { force?: boolean }) => {
    // Impersonating platform operators hold no tenant grants by design; the read-only token would
    // only produce noise here, and `hasPermission` already short-circuits for them.
    if (getImpersonation()) return;
    if (!tokenRef.current) return;
    if (inFlightRef.current) return inFlightRef.current;
    if (!options?.force && Date.now() - lastLoadedAtRef.current < PERMISSIONS_STALE_MS) return;

    const load = (async () => {
      setPermissionsLoading(true);
      try {
        const me = await userService.getMyPermissions();
        setUserDataState((previous) => {
          const next: UserData = {
            ...previous,
            id: me.userId ?? previous.id,
            roleId: me.roleId ?? previous.roleId,
            roleName: me.roleName ?? previous.roleName,
            businessUnitId: me.businessUnitId ?? previous.businessUnitId,
            isSuperAdmin: me.isSuperAdmin === true,
            isManager: me.isManager === true,
            permissions: me.permissions ?? [],
            schemaVersion: PERMISSION_SCHEMA_VERSION,
          };
          localStorage.setItem("userData", JSON.stringify(next));
          return next;
        });
        lastLoadedAtRef.current = Date.now();
        setPermissionsError(null);
      } catch (error) {
        setPermissionsError(presentableErrorMessage(error, PERMISSIONS_LOAD_FAILED_MESSAGE));
      } finally {
        setPermissionsLoading(false);
        inFlightRef.current = null;
      }
    })();

    inFlightRef.current = load;
    return load;
  }, []);

  const logout = () => {
    setToken(null);
    setUserData({});
    lastLoadedAtRef.current = 0;
    setPermissionsError(null);
    localStorage.clear();
    window.location.href = "/login";
  };

  const hasPermission = useCallback((moduleName: string, action: 'view' | 'create' | 'edit' | 'delete' = 'view') => {
    // Impersonating platform operators have no tenant permission rows; let them
    // SEE everything — every mutation is rejected server-side by the read-only
    // impersonation middleware regardless of what the client renders.
    if (getImpersonation()) return true;
    if (userData.isSuperAdmin === true) return true;
    if (!userData.permissions) return false;

    const permission = userData.permissions.find(
      p => p.moduleName.trim().toLowerCase() === moduleName.trim().toLowerCase()
    );

    if (!permission) return false;

    switch (action) {
      case 'create': return permission.canCreate === true;
      case 'edit': return permission.canEdit === true;
      case 'delete': return permission.canDelete === true;
      // Read is an EXPLICIT grant. This used to `return true` for any row that existed, which made
      // "revoke everything" write an all-false row and thereby grant read on every module.
      case 'view': return permission.canView === true;
      default: return false;
    }
  }, [userData.isSuperAdmin, userData.permissions]);

  // Keep the permission set fresh: once on mount (so a reload never runs on a stale snapshot) and
  // whenever the tab regains focus. Both honour the stale window, so switching tabs rapidly does
  // not hammer the endpoint. Server-side grant changes become visible within ~60s, matching the
  // backend's own RBAC cache TTL rather than pretending to be instant.
  useEffect(() => {
    if (!token) return;
    void refreshPermissions();

    const onFocus = () => { void refreshPermissions(); };
    window.addEventListener('focus', onFocus);
    return () => window.removeEventListener('focus', onFocus);
  }, [token, refreshPermissions]);

  // FE-12: while the app is open, schedule a proactive logout for the moment
  // the current token expires, redirecting to /login with a friendly notice
  // rather than waiting for the next request to fail with a 401.
  useEffect(() => {
    if (!token) return;
    // Impersonation expiry is owned by ImpersonationBanner (it exits back to
    // the platform console); the tenant-session logout path must not fire.
    if (getImpersonation()) return;

    const expireSession = () => {
      localStorage.removeItem("token");
      localStorage.removeItem("userData");
      sessionStorage.setItem("authNotice", SESSION_EXPIRED_MESSAGE);
      setUserDataState({});
      setTokenState(null);
      if (window.location.pathname !== "/login") {
        window.location.href = "/login";
      }
    };

    const expiry = getTokenExpiry(token);
    if (expiry === null) return;

    const msUntilLogout = expiry - EXPIRY_SKEW_MS - Date.now();
    if (msUntilLogout <= 0) {
      expireSession();
      return;
    }
    if (msUntilLogout > MAX_TIMEOUT_MS) return;

    const timer = window.setTimeout(expireSession, msUntilLogout);
    return () => window.clearTimeout(timer);
  }, [token]);

  return (
    <AuthContext.Provider
      value={{
        token,
        userData,
        setToken,
        setUserData,
        logout,
        hasPermission,
        permissionsError,
        permissionsLoading,
        refreshPermissions,
      }}
    >
      {children}
    </AuthContext.Provider>
  );
};

export const useAuth = () => {
  const context = useContext(AuthContext);
  if (!context) {
    throw new Error("useAuth must be used within an AuthProvider");
  }
  return context;
};
