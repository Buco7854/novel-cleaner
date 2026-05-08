import { api } from "./client";

/** Active-book row returned by the admin usage endpoint. */
export interface AdminUsageBook {
  id: string;
  title: string | null;
  fileName: string;
  author: string | null;
  ownerEmail: string | null;
  ownerDisplayName: string | null;
  /** Mirrors the BookStatus enum the worker writes; admin page only ever
   *  sees Queued / Running / Paused because the endpoint filters terminal
   *  states out. */
  status: "Queued" | "Running" | "Paused";
  model: string;
  createdAt: string;
  startedAt: string | null;
}

export const listAdminUsage = () => api<AdminUsageBook[]>("/api/admin/usage/");
