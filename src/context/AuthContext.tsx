import React, {
  createContext,
  useContext,
  useState,
  type ReactNode,
  useEffect,
} from "react";
import axiosInstance from "../api/axiosInstance";

interface BusinessUnit {
  id: number;
  businessUnitName: string;
}

interface UserData {
  id?: number;
  email?: string;
  userName?: string;
  roleId?: number;
  roleName?: string;
  businessUnitId?: number;
}

interface AuthContextType {
  token: string | null;
  userData: UserData;
  businessUnits: BusinessUnit[];
  loadingBusinessUnits: boolean;
  setToken: (token: string | null) => void;
  setUserData: (data: UserData) => void;
  logout: () => void;
}

const AuthContext = createContext<AuthContextType | undefined>(undefined);

export const AuthProvider: React.FC<{ children: ReactNode }> = ({ children }) => {
  const [token, setTokenState] = useState<string | null>(localStorage.getItem("token"));
  const [businessUnits, setBusinessUnits] = useState<BusinessUnit[]>([]);
  const [loadingBusinessUnits, setLoadingBusinessUnits] = useState<boolean>(false);
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

  useEffect(() => {
    const fetchBusinessUnits = async () => {
      setLoadingBusinessUnits(true);
      try {
        const response = await axiosInstance.get("/api/BusinessUnit/Dropdown");
        setBusinessUnits(response.data);
      } catch (err) {
        console.error("Failed to fetch business units", err);
      } finally {
        setLoadingBusinessUnits(false);
      }
    };
    fetchBusinessUnits();
  }, []);

  return (
    <AuthContext.Provider
      value={{
        token,
        userData,
        setToken,
        setUserData,
        logout,
        businessUnits,
        loadingBusinessUnits,
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
