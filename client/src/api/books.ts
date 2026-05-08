import { api, apiJson } from "./client";

export type BookStatus =
  | "Queued"
  | "Running"
  | "Paused"
  | "Completed"
  | "Failed"
  | "Canceled"
  | "AwaitingReview"
  | "Idle";

export interface Book {
  id: string;
  fileName: string;
  /** EPUB OPF dc:title — preferred display label, falls back to fileName. */
  title: string | null;
  /** EPUB OPF dc:creator — shown next to the title in lists. */
  author: string | null;
  sizeBytes: number;
  status: BookStatus;
  model: string;
  removed: number;
  createdAt: string;
  completedAt: string | null;
  hasOutput: boolean;
}

export interface BookMetadata {
  title: string | null;
  author: string | null;
  language: string | null;
  publisher: string | null;
  description: string | null;
}

export interface BooksPage {
  items: Book[];
  total: number;
  page: number;
  size: number;
}

export interface BookLog {
  timestamp: string;
  level: string;
  message: string;
  /** Optional raw verbatim payload (e.g. raw LLM response after fence-strip). */
  detail?: string | null;
  /** Lines that share a groupId belong to the same per-document storyline. */
  groupId?: string | null;
}

export interface BookDetail {
  id: string;
  fileName: string;
  /** EPUB OPF metadata — every field optional. UI falls back to fileName when
   *  title is empty. */
  title: string | null;
  author: string | null;
  language: string | null;
  publisher: string | null;
  description: string | null;
  status: BookStatus;
  model: string;
  removed: number;
  error: string | null;
  createdAt: string;
  startedAt: string | null;
  completedAt: string | null;
  hasOutput: boolean;
  /** True when an admin has set a drop folder. The actual drop-folder
   *  permission is checked server-side at POST /drop time. */
  dropConfigured: boolean;
  /** Per-book AI instructions appended last in the prompt stack
   *  (admin → user → book). */
  systemPrompt: string;
  logs: BookLog[];
}

export const PAGE_SIZE = 20;
export const listBooks = (page = 1, size = PAGE_SIZE) =>
  api<BooksPage>(`/api/books/?page=${page}&size=${size}`);
export const getBook = (id: string) => api<BookDetail>(`/api/books/${id}`);
export const deleteBook = (id: string) => api(`/api/books/${id}`, { method: "DELETE" });
export const downloadUrl = (id: string) => `/api/books/${id}/download`;
export const pushToFolder = (id: string) =>
  api<{ destination: string }>(`/api/books/${id}/drop`, { method: "POST" });
export const pauseBook = (id: string) => api<{ ok: boolean }>(`/api/books/${id}/pause`, { method: "POST" });
export const resumeBook = (id: string) => api<{ ok: boolean }>(`/api/books/${id}/resume`, { method: "POST" });
export const cancelBook = (id: string) => api<{ ok: boolean }>(`/api/books/${id}/cancel`, { method: "POST" });
/** Wipes the persisted log lines for a book — used by the editor's "Clear"
 *  button so a refresh doesn't repopulate the panel from server history. */
export const clearBookLogs = (id: string) => api(`/api/books/${id}/logs`, { method: "DELETE" });

/** Per-book AI instructions — appended after admin + user prompts. */
export const saveBookPrompt = (id: string, systemPrompt: string) =>
  apiJson<null>(`/api/books/${id}/prompt`, "PUT", { systemPrompt });

/** Edit the EPUB metadata. Persists into the DB (drives display labels) and
 *  rewrites the OPF in the input EPUB so the next download inherits the
 *  edits without a separate re-export step. */
export const saveBookMetadata = (id: string, metadata: BookMetadata) =>
  apiJson<BookMetadata>(`/api/books/${id}/metadata`, "PUT", metadata);

/** Best label for display: EPUB title when present, otherwise filename. */
export function bookDisplayName(n: { title?: string | null; fileName: string }): string {
  return n.title?.trim() || n.fileName;
}

export async function uploadBooks(files: File[]): Promise<string[]> {
  const fd = new FormData();
  for (const f of files) fd.append("files", f, f.name);
  const result = await api<{ ids: string[] }>("/api/books/", { method: "POST", body: fd });
  return result.ids;
}
