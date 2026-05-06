import { apiJson } from "./client";

/**
 * Novel-level lifecycle actions. The legacy `/api/novels/{id}/reviews/*`
 * proposal APIs were retired when the editor switched to a git-backed
 * model — the editor surface now lives in `./pages.ts`.
 */
export const finalizeNovel = (novelId: string) =>
  apiJson<{ ok: boolean }>(`/api/novels/${novelId}/finalize`, "POST", {});

/**
 * Clone a completed novel — creates a new Idle entry whose source is the
 * cleaned output of the original. Returns the new id so the caller can
 * navigate to it. Doesn't auto-run anything: the user clicks Run AI on
 * the clone when ready, same as for any fresh upload.
 */
export const cloneNovel = (novelId: string) =>
  apiJson<{ id: string }>(`/api/novels/${novelId}/clone`, "POST", {});

/**
 * Run the AI pass against this novel. When <code>pages</code> is omitted
 * or empty, runs against every chapter; otherwise restricts to the listed
 * page paths (e.g. <code>"pages/0001_foo.txt"</code>).
 */
export const runAi = (novelId: string, pages?: string[]) =>
  apiJson<{ ok: boolean }>(`/api/novels/${novelId}/run-ai`, "POST", { pages });
