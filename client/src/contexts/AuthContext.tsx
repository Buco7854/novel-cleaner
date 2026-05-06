import { createContext, useContext, useEffect, useMemo, useState } from "react";
import {
  AuthConfig, CurrentUser, fetchAuthConfig, fetchMe, logout as doLogout,
  passwordLogin,
} from "../api/auth";
import { checkSetupNeeded } from "../api/setup";

interface AuthState {
  loading: boolean;
  setupNeeded: boolean;
  config: AuthConfig | null;
  user: CurrentUser | null;
  isAdmin: boolean;
  refresh: () => Promise<void>;
  login: (email: string, password: string) => Promise<void>;
  logout: () => Promise<void>;
}

const Ctx = createContext<AuthState | null>(null);

export function AuthProvider({ children }: { children: React.ReactNode }) {
  const [loading, setLoading] = useState(true);
  const [setupNeeded, setSetupNeeded] = useState(false);
  const [config, setConfig] = useState<AuthConfig | null>(null);
  const [user, setUser] = useState<CurrentUser | null>(null);

  async function refresh() {
    setLoading(true);
    try {
      const [setup, cfg, me] = await Promise.all([
        checkSetupNeeded(),
        fetchAuthConfig(),
        fetchMe(),
      ]);
      setSetupNeeded(setup.needed);
      setConfig(cfg);
      setUser(me.authenticated ? me.user! : null);
    } finally {
      setLoading(false);
    }
  }

  useEffect(() => { refresh(); }, []);

  const value = useMemo<AuthState>(() => ({
    loading,
    setupNeeded,
    config,
    user,
    isAdmin: user?.roles?.includes("Admin") ?? false,
    refresh,
    login: async (email, password) => { await passwordLogin(email, password); await refresh(); },
    logout: async () => { await doLogout(); setUser(null); },
  }), [loading, setupNeeded, config, user]);

  return <Ctx.Provider value={value}>{children}</Ctx.Provider>;
}

export function useAuth() {
  const v = useContext(Ctx);
  if (!v) throw new Error("AuthProvider missing");
  return v;
}
