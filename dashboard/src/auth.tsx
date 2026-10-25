import { createContext, useCallback, useContext, useEffect, useState, type ReactNode } from "react";
import { api, ApiError, type User } from "./api";

interface AuthState {
  user: User | null;
  loading: boolean;
  setUser: (user: User | null) => void;
  signOut: () => Promise<void>;
}

const AuthContext = createContext<AuthState | null>(null);

export function AuthProvider({ children }: { children: ReactNode }) {
  const [user, setUser] = useState<User | null>(null);
  const [loading, setLoading] = useState(true);

  useEffect(() => {
    api.me()
      .then(setUser)
      .catch((error) => {
        if (!(error instanceof ApiError && error.status === 401)) console.error(error);
      })
      .finally(() => setLoading(false));
  }, []);

  const signOut = useCallback(async () => {
    try {
      await api.logout();
    } finally {
      setUser(null);
    }
  }, []);

  return <AuthContext.Provider value={{ user, loading, setUser, signOut }}>{children}</AuthContext.Provider>;
}

export function useAuth(): AuthState {
  const value = useContext(AuthContext);
  if (!value) throw new Error("useAuth must be used inside AuthProvider");
  return value;
}
