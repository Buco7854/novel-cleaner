import { api, apiJson } from "./client";

export type ProposalSource = "Llm" | "User";
export type ProposalDecision = "Pending" | "Accepted" | "Rejected";

export interface ChapterReviewSummary {
  documentName: string;
  orderIndex: number;
  accepted: number;
  rejected: number;
  pending: number;
  llmCount: number;
  userCount: number;
}

export interface Proposal {
  id: number;
  text: string;
  reason: string;
  source: ProposalSource;
  decision: ProposalDecision;
}

export interface ChapterReview {
  documentName: string;
  visibleText: string;
  proposals: Proposal[];
}

export const listReviews = (jobId: string) =>
  api<ChapterReviewSummary[]>(`/api/jobs/${jobId}/reviews/`);

export const getChapterReview = (jobId: string, doc: string) =>
  api<ChapterReview>(`/api/jobs/${jobId}/reviews/chapter?doc=${encodeURIComponent(doc)}`);

export const decideProposal = (jobId: string, proposalId: number, decision: ProposalDecision) =>
  apiJson<null>(`/api/jobs/${jobId}/reviews/decisions/${proposalId}`, "POST", { decision });

export const addUserProposal = (jobId: string, doc: string, text: string, reason?: string) =>
  apiJson<{ id: number }>(
    `/api/jobs/${jobId}/reviews/proposals?doc=${encodeURIComponent(doc)}`, "POST",
    { text, reason });

export const deleteProposal = (jobId: string, proposalId: number) =>
  api(`/api/jobs/${jobId}/reviews/proposals/${proposalId}`, { method: "DELETE" });

export const repromptChapter = (
  jobId: string, doc: string, range?: { start: number; end: number }
) =>
  apiJson<{ added: number }>(
    `/api/jobs/${jobId}/reviews/reprompt?doc=${encodeURIComponent(doc)}`, "POST",
    range ? { rangeStart: range.start, rangeEnd: range.end } : {});

export const finalizeJob = (jobId: string) =>
  apiJson<{ ok: boolean }>(`/api/jobs/${jobId}/finalize`, "POST", {});

export const reprocessJob = (jobId: string) =>
  apiJson<{ id: string }>(`/api/jobs/${jobId}/reprocess`, "POST", {});
