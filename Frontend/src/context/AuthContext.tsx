import React, {
  createContext,
  useCallback,
  useContext,
  useState,
  type ReactNode,
  useEffect,
} from "react";
import { jwtDecode } from "jwt-decode";

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

export interface Permission {
  id: number;
  roleId: number;
  moduleId: number;
  moduleName: string;
  canCreate: boolean;
  canEdit: boolean;
  canDelete: boolean;
}

interface UserData {
  id?: number;
  email?: string;
  userName?: string;
  roleId?: number;
  roleName?: string;
  isSuperAdmin?: boolean;
  isManager?: boolean;
  businessUnitId?: number;
  permissions?: Permission[];
}

interface AuthContextType {
  token: string | null;
  userData: UserData;
  setToken: (token: string | null) => void;
  setUserData: (data: UserData) => void;
  logout: () => void;
  hasPermission: (moduleName: string, action?: 'view' | 'create' | 'edit' | 'delete') => boolean;
}

const AuthContext = createContext<AuthContextType | undefined>(undefined);

export const AuthProvider: React.FC<{ children: ReactNode }> = ({ children }) => {
  const [token, setTokenState] = useState<string | null>(() => {
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
  const [userData, setUserDataState] = useState<UserData>(() => {
    const stored = localStorage.getItem("userData");
    return stored ? JSON.parse(stored) : {};
  });

  const setToken = (newToken: string | null) => {
    setTokenState(newToken);
    if (newToken) {
      localStorage.setItem("token", newToken);
    } else {
      localStorage.removeItem("token");
    }
  };

  const setUserData = (data: UserData) => {
    setUserDataState(data);
    localStorage.setItem("userData", JSON.stringify(data));
  };

  const logout = () => {
    setToken(null);
    setUserData({});
    localStorage.clear();
    window.location.href = "/login";
  };

  const hasPermission = useCallback((moduleName: string, action: 'view' | 'create' | 'edit' | 'delete' = 'view') => {
    if (userData.isSuperAdmin === true) return true;
    if (!userData.permissions) return false;

    const permission = userData.permissions.find(
      p => p.moduleName.trim().toLowerCase() === moduleName.trim().toLowerCase()
    );

    if (!permission) return false;

    switch (action) {
      case 'create': return permission.canCreate;
      case 'edit': return permission.canEdit;
      case 'delete': return permission.canDelete;
      case 'view': return true; // If they are in the list, they can at least view
      default: return false;
    }
  }, [userData.isSuperAdmin, userData.permissions]);

  // FE-12: while the app is open, schedule a proactive logout for the moment
  // the current token expires, redirecting to /login with a friendly notice
  // rather than waiting for the next request to fail with a 401.
  useEffect(() => {
    if (!token) return;

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
