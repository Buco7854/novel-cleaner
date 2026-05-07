import { apiJson } from "./client";

/**
 * Job-level actions. The legacy `/api/jobs/{id}/reviews/*` proposal APIs
 * were retired when the editor switched to a git-backed model — the editor
 * surface now lives in `./pages.ts`. These three handlers stayed because
 * they're job lifecycle actions, not review queries.
 */
export const finalizeJob = (jobId: string) =>
  apiJson<{ ok: boolean }>(`/api/jobs/${jobId}/finalize`, "POST", {});

export const reprocessJob = (jobId: string) =>
  apiJson<{ id: string }>(`/api/jobs/${jobId}/reprocess`, "POST", {});

export const rerunAi = (jobId: string) =>
  apiJson<{ ok: boolean }>(`/api/jobs/${jobId}/rerun-ai`, "POST", {});
