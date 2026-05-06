import { api, apiJson } from "./client";

export interface AuthConfig {
  password: boolean;
  oidc: boolean;
  /** Human-readable name of the SSO provider, shown on the SSO button. */
  oidcDisplayName: string;
  allowSelfRegister: boolean;
}

export interface CurrentUser {
  id: string;
  email: string | null;
  displayName: string | null;
  provider: string;
  roles: string[];
}

export interface MeResponse {
  authenticated: boolean;
  user?: CurrentUser;
}

export const fetchAuthConfig = () => api<AuthConfig>("/api/auth/config");
export const fetchMe         = () => api<MeResponse>("/api/auth/me");
export const passwordLogin   = (email: string, password: string) =>
  apiJson<{ ok: boolean }>("/api/auth/login", "POST", { email, password });
export const logout          = () => apiJson<{ ok: boolean }>("/api/auth/logout", "POST", {});
export const oidcLoginUrl    = (returnUrl = "/") =>
  `/api/auth/oidc/login?returnUrl=${encodeURIComponent(returnUrl)}`;
