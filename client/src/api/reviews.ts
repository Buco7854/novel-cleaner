import { apiJson } from "./client";

/**
 * Book-level lifecycle actions. The legacy `/api/books/{id}/reviews/*`
 * proposal APIs were retired when the editor switched to a git-backed
 * model — the editor surface now lives in `./pages.ts`.
 */
export const finalizeBook = (bookId: string) =>
  apiJson<{ ok: boolean }>(`/api/books/${bookId}/finalize`, "POST", {});

/**
 * Clone a completed book — creates a new Idle entry whose source is the
 * cleaned output of the original. Returns the new id so the caller can
 * navigate to it. Doesn't auto-run anything: the user clicks Run AI on
 * the clone when ready, same as for any fresh upload.
 */
export const cloneBook = (bookId: string) =>
  apiJson<{ id: string }>(`/api/books/${bookId}/clone`, "POST", {});

/**
 * Run the AI pass against this book. When <code>pages</code> is omitted
 * or empty, runs against every chapter; otherwise restricts to the listed
 * page paths (e.g. <code>"pages/0001_foo.txt"</code>).
 */
export const runAi = (bookId: string, pages?: string[]) =>
  apiJson<{ ok: boolean }>(`/api/books/${bookId}/run-ai`, "POST", { pages });
