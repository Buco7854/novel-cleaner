import { api, apiJson } from "./client";

export type NovelStatus =
  | "Queued"
  | "Running"
  | "Paused"
  | "Completed"
  | "Failed"
  | "Canceled"
  | "AwaitingReview"
  | "Idle";

export interface Novel {
  id: string;
  fileName: string;
  /** EPUB OPF dc:title — preferred display label, falls back to fileName. */
  title: string | null;
  /** EPUB OPF dc:creator — shown next to the title in lists. */
  author: string | null;
  sizeBytes: number;
  status: NovelStatus;
  model: string;
  removed: number;
  createdAt: string;
  completedAt: string | null;
  hasOutput: boolean;
}

export interface NovelMetadata {
  title: string | null;
  author: string | null;
  language: string | null;
  publisher: string | null;
  description: string | null;
}

export interface NovelsPage {
  items: Novel[];
  total: number;
  page: number;
  size: number;
}

export interface NovelLog {
  timestamp: string;
  level: string;
  message: string;
  /** Optional raw verbatim payload (e.g. raw LLM response after fence-strip). */
  detail?: string | null;
  /** Lines that share a groupId belong to the same per-document storyline. */
  groupId?: string | null;
}

export interface NovelDetail {
  id: string;
  fileName: string;
  /** EPUB OPF metadata — every field optional. UI falls back to fileName when
   *  title is empty. */
  title: string | null;
  author: string | null;
  language: string | null;
  publisher: string | null;
  description: string | null;
  status: NovelStatus;
  model: string;
  maxWorkers: number;
  removed: number;
  error: string | null;
  createdAt: string;
  startedAt: string | null;
  completedAt: string | null;
  hasOutput: boolean;
  /** True when an admin has set a drop folder. The actual BookDrop permission
   *  is checked server-side at POST /drop time. */
  dropConfigured: boolean;
  /** Per-novel AI instructions appended last in the prompt stack
   *  (admin → user → novel). */
  systemPrompt: string;
  logs: NovelLog[];
}

export const PAGE_SIZE = 20;
export const listNovels = (page = 1, size = PAGE_SIZE) =>
  api<NovelsPage>(`/api/novels/?page=${page}&size=${size}`);
export const getNovel = (id: string) => api<NovelDetail>(`/api/novels/${id}`);
export const deleteNovel = (id: string) => api(`/api/novels/${id}`, { method: "DELETE" });
export const downloadUrl = (id: string) => `/api/novels/${id}/download`;
export const pushToFolder = (id: string) =>
  api<{ destination: string }>(`/api/novels/${id}/drop`, { method: "POST" });
export const pauseNovel = (id: string) => api<{ ok: boolean }>(`/api/novels/${id}/pause`, { method: "POST" });
export const resumeNovel = (id: string) => api<{ ok: boolean }>(`/api/novels/${id}/resume`, { method: "POST" });
export const cancelNovel = (id: string) => api<{ ok: boolean }>(`/api/novels/${id}/cancel`, { method: "POST" });
/** Wipes the persisted log lines for a novel — used by the editor's "Clear"
 *  button so a refresh doesn't repopulate the panel from server history. */
export const clearNovelLogs = (id: string) => api(`/api/novels/${id}/logs`, { method: "DELETE" });

/** Per-novel AI instructions — appended after admin + user prompts. */
export const saveNovelPrompt = (id: string, systemPrompt: string) =>
  apiJson<null>(`/api/novels/${id}/prompt`, "PUT", { systemPrompt });

/** Edit the EPUB metadata. Persists into the DB (drives display labels) and
 *  rewrites the OPF in the input EPUB so the next download inherits the
 *  edits without a separate re-export step. */
export const saveNovelMetadata = (id: string, metadata: NovelMetadata) =>
  apiJson<NovelMetadata>(`/api/novels/${id}/metadata`, "PUT", metadata);

/** Best label for display: EPUB title when present, otherwise filename. */
export function novelDisplayName(n: { title?: string | null; fileName: string }): string {
  return n.title?.trim() || n.fileName;
}

export async function uploadNovels(files: File[]): Promise<string[]> {
  const fd = new FormData();
  for (const f of files) fd.append("files", f, f.name);
  const result = await api<{ ids: string[] }>("/api/novels/", { method: "POST", body: fd });
  return result.ids;
}
