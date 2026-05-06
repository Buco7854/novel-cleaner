import { api, apiJson } from "./client";

export interface AdminUser {
  id: string;
  email: string;
  displayName: string | null;
  provider: string;
  isDisabled: boolean;
  createdAt: string;
  lastLoginAt: string | null;
  roles: string[];
}

export const listAdminUsers = () => api<AdminUser[]>("/api/admin/users/");

export const createAdminUser = (req: {
  email: string; password: string; displayName?: string; isAdmin: boolean;
}) => apiJson<{ id: string }>("/api/admin/users/", "POST", req);

export const updateAdminUser = (id: string, req: {
  displayName?: string; isDisabled?: boolean; isAdmin?: boolean;
}) => apiJson<null>(`/api/admin/users/${id}`, "PATCH", req);

export const setUserPassword = (id: string, password: string) =>
  apiJson<null>(`/api/admin/users/${id}/password`, "POST", { password });

export const deleteAdminUser = (id: string) =>
  api(`/api/admin/users/${id}`, { method: "DELETE" });
