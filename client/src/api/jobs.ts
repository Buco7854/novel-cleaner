import { api } from "./client";

export type JobStatus = "Queued" | "Running" | "Paused" | "Completed" | "Failed" | "Canceled";

export interface JobSummary {
  id: string;
  fileName: string;
  sizeBytes: number;
  status: JobStatus;
  scanAll: boolean;
  model: string;
  removed: number;
  createdAt: string;
  completedAt: string | null;
  hasOutput: boolean;
}

export interface JobsPage {
  items: JobSummary[];
  total: number;
  page: number;
  size: number;
}

export interface JobLog {
  timestamp: string;
  level: string;
  message: string;
  /** Optional raw verbatim payload (e.g. raw LLM response after fence-strip). */
  detail?: string | null;
  /** Lines that share a groupId belong to the same per-document storyline. */
  groupId?: string | null;
}

export interface JobDetail {
  id: string;
  fileName: string;
  status: JobStatus;
  model: string;
  scanAll: boolean;
  patternCount: number;
  contextWindow: number;
  maxWorkers: number;
  removed: number;
  error: string | null;
  createdAt: string;
  startedAt: string | null;
  completedAt: string | null;
  hasOutput: boolean;
  /** True when a drop folder is configured AND the job's owner has BookDrop permission. */
  canDrop: boolean;
  logs: JobLog[];
}

export const PAGE_SIZE = 20;
export const listJobs = (page = 1, size = PAGE_SIZE) =>
  api<JobsPage>(`/api/jobs/?page=${page}&size=${size}`);
export const getJob   = (id: string) => api<JobDetail>(`/api/jobs/${id}`);
export const deleteJob = (id: string) => api(`/api/jobs/${id}`, { method: "DELETE" });
export const downloadUrl = (id: string) => `/api/jobs/${id}/download`;
export const copyToDrop = (id: string) =>
  api<{ destination: string }>(`/api/jobs/${id}/drop`, { method: "POST" });
export const pauseJob   = (id: string) => api<{ ok: boolean }>(`/api/jobs/${id}/pause`,  { method: "POST" });
export const resumeJob  = (id: string) => api<{ ok: boolean }>(`/api/jobs/${id}/resume`, { method: "POST" });

export async function uploadJobs(
  files: File[],
  overrides: { scanAll?: boolean; model?: string; patterns?: string[] }
): Promise<string[]> {
  const fd = new FormData();
  for (const f of files) fd.append("files", f, f.name);
  if (overrides.scanAll !== undefined) fd.append("scanAll", String(overrides.scanAll));
  if (overrides.model)   fd.append("model", overrides.model);
  if (overrides.patterns) fd.append("patterns", JSON.stringify(overrides.patterns));
  const result = await api<{ ids: string[] }>("/api/jobs/", { method: "POST", body: fd });
  return result.ids;
}
