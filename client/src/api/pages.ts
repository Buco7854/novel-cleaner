import { api, apiJson } from "./client";

export type PageStatus = "Clean" | "Modified";

export interface PageEntry {
  path: string;
  orderIndex: number;
  status: PageStatus;
}

export interface PageDetail {
  path: string;
  content: string;
  status: PageStatus;
  /** Unified diff against HEAD; empty string when the page is clean. */
  diff: string;
}

export const listPages = (jobId: string) =>
  api<PageEntry[]>(`/api/jobs/${jobId}/pages/`);

export const getPage = (jobId: string, path: string) =>
  api<PageDetail>(`/api/jobs/${jobId}/pages/page?path=${encodeURIComponent(path)}`);

export const writePage = (jobId: string, path: string, content: string) =>
  apiJson<null>(`/api/jobs/${jobId}/pages/page?path=${encodeURIComponent(path)}`, "PUT", { content });

export const commitPages = (jobId: string, message?: string) =>
  apiJson<{ committed: boolean }>(`/api/jobs/${jobId}/pages/commit`, "POST", { message });

export const discardPage = (jobId: string, path: string) =>
  apiJson<null>(`/api/jobs/${jobId}/pages/discard?path=${encodeURIComponent(path)}`, "POST", {});

export const rejectHunk = (jobId: string, path: string, index: number) =>
  apiJson<null>(
    `/api/jobs/${jobId}/pages/reject-hunk?path=${encodeURIComponent(path)}&index=${index}`,
    "POST", {});

export const acceptHunk = (jobId: string, path: string, index: number) =>
  apiJson<null>(
    `/api/jobs/${jobId}/pages/accept-hunk?path=${encodeURIComponent(path)}&index=${index}`,
    "POST", {});
