import { api, apiJson } from "./client";

export type PageStatus = "Clean" | "Modified";

export interface PageEntry {
  path: string;
  orderIndex: number;
  status: PageStatus;
  /** EPUB document name this page mirrors — keyed against worker log groupIds. */
  docName: string | null;
}

export interface PageDetail {
  path: string;
  content: string;
  status: PageStatus;
  /** Unified diff against HEAD; empty string when the page is clean. */
  diff: string;
}

export const listPages = (novelId: string) =>
  api<PageEntry[]>(`/api/novels/${novelId}/pages/`);

export const getPage = (novelId: string, path: string) =>
  api<PageDetail>(`/api/novels/${novelId}/pages/page?path=${encodeURIComponent(path)}`);

export const writePage = (novelId: string, path: string, content: string) =>
  apiJson<null>(`/api/novels/${novelId}/pages/page?path=${encodeURIComponent(path)}`, "PUT", { content });

export const commitPages = (novelId: string, message?: string) =>
  apiJson<{ committed: boolean }>(`/api/novels/${novelId}/pages/commit`, "POST", { message });

export const commitPage = (novelId: string, path: string, message?: string) =>
  apiJson<{ committed: boolean }>(
    `/api/novels/${novelId}/pages/commit-page?path=${encodeURIComponent(path)}`,
    "POST", { message });

/**
 * Batch accept: commit every modified page among <code>paths</code> as a
 * single git commit. Clean paths are silently skipped server-side.
 * Returned <code>committed</code> is false when none of the listed paths
 * had changes.
 */
export const commitManyPages = (novelId: string, paths: string[], message?: string) =>
  apiJson<{ committed: boolean }>(
    `/api/novels/${novelId}/pages/commit-many`,
    "POST", { paths, message });

/**
 * Batch reject: discard working-tree changes for every page in
 * <code>paths</code>. Pages with no changes are skipped server-side.
 */
export const discardManyPages = (novelId: string, paths: string[]) =>
  apiJson<null>(`/api/novels/${novelId}/pages/discard-many`, "POST", { paths });

export const discardPage = (novelId: string, path: string) =>
  apiJson<null>(`/api/novels/${novelId}/pages/discard?path=${encodeURIComponent(path)}`, "POST", {});

export const rejectHunk = (novelId: string, path: string, index: number) =>
  apiJson<null>(
    `/api/novels/${novelId}/pages/reject-hunk?path=${encodeURIComponent(path)}&index=${index}`,
    "POST", {});

export const acceptHunk = (novelId: string, path: string, index: number) =>
  apiJson<null>(
    `/api/novels/${novelId}/pages/accept-hunk?path=${encodeURIComponent(path)}&index=${index}`,
    "POST", {});

/**
 * Wipe and re-init the editor repo from the original EPUB. Loses any
 * uncommitted edits in the working tree and rewrites history. Used when
 * an extraction-pipeline change has shipped and existing repos need to
 * pick up the fix.
 */
export const resetRepo = (novelId: string) =>
  apiJson<{ pages: number }>(`/api/novels/${novelId}/pages/reset`, "POST", {});

/** URL of the rendered HTML preview for a single page. Designed for an
 *  iframe `src` — the server returns a fully-formed HTML/XHTML document.
 *
 *  - `working` (default): the user's pending state, rendered with the
 *    publisher's stylesheet — what the export would look like.
 *  - `diff`: synthetic single-page rendering with removed paragraphs
 *    wrapped <del> (red strikethrough) and inserted ones <ins> (green).
 *    Sacrifices typography for clarity. */
export type PreviewSource = "working" | "diff";

export const pagePreviewUrl = (
  novelId: string,
  path: string,
  source: PreviewSource = "working",
) =>
  `/api/novels/${novelId}/pages/preview?path=${encodeURIComponent(path)}&source=${source}`;

export interface PageRevision {
  sha: string;
  shortSha: string;
  message: string;
  timestamp: string;
  author: string;
}

/** History (commits that touched this page), newest-first. */
export const listPageHistory = (novelId: string, path: string) =>
  api<PageRevision[]>(`/api/novels/${novelId}/pages/history?path=${encodeURIComponent(path)}`);

/** Page content as it was at <code>sha</code>. */
export const getPageAtCommit = (novelId: string, path: string, sha: string) =>
  api<{ content: string }>(
    `/api/novels/${novelId}/pages/at?path=${encodeURIComponent(path)}&sha=${encodeURIComponent(sha)}`,
  );

/** Drop the page's content at <code>sha</code> into the working tree —
 *  surfaces as a pending diff for review, doesn't commit. */
export const restorePageToCommit = (novelId: string, path: string, sha: string) =>
  apiJson<null>(
    `/api/novels/${novelId}/pages/restore?path=${encodeURIComponent(path)}&sha=${encodeURIComponent(sha)}`,
    "POST", {});
