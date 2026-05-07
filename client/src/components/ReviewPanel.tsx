import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { Check, FileText, Loader2, Plus, RefreshCw, Trash2, X } from "lucide-react";
import { useEffect, useMemo, useRef, useState } from "react";
import { useTranslation } from "react-i18next";
import {
  addUserProposal,
  ChapterReviewSummary,
  decideProposal,
  deleteProposal,
  getChapterReview,
  listReviews,
  Proposal,
  ProposalDecision,
  repromptChapter,
} from "../api/reviews";
import { useToast } from "../contexts/ToastContext";
import { clsx } from "../utils/clsx";

/**
 * Two-pane review screen: chapter list on the left, currently-selected
 * chapter's proposals on the right. Hosts the text-selection-to-add-removal
 * interaction and the per-chapter re-prompt button. Driven entirely by the
 * server's /api/jobs/{id}/reviews surface — no local-only state for proposal
 * decisions.
 */
export function ReviewPanel({ jobId }: { jobId: string }) {
  const { t } = useTranslation();
  const reviews = useQuery({
    queryKey: ["reviews", jobId],
    queryFn: () => listReviews(jobId),
  });
  const [selectedDoc, setSelectedDoc] = useState<string | null>(null);

  // Auto-select the first chapter the first time the list loads.
  useEffect(() => {
    if (!selectedDoc && reviews.data && reviews.data.length > 0) {
      setSelectedDoc(reviews.data[0].documentName);
    }
  }, [reviews.data, selectedDoc]);

  if (reviews.isLoading) {
    return (
      <div className="card flex items-center gap-2 p-4 text-sm text-stone-500">
        <Loader2 className="h-4 w-4 animate-spin" /> {t("review.loading")}
      </div>
    );
  }
  if (!reviews.data || reviews.data.length === 0) {
    return (
      <div className="card p-4 text-sm text-stone-500">{t("review.noChapters")}</div>
    );
  }

  return (
    <div className="grid grid-cols-1 gap-4 lg:grid-cols-[18rem_1fr]">
      <ChapterList
        items={reviews.data}
        selected={selectedDoc}
        onSelect={setSelectedDoc}
      />
      {selectedDoc ? (
        <ChapterReviewView jobId={jobId} doc={selectedDoc} />
      ) : (
        <div className="card p-4 text-sm text-stone-500">{t("review.selectAChapter")}</div>
      )}
    </div>
  );
}

function ChapterList({
  items, selected, onSelect,
}: {
  items: ChapterReviewSummary[];
  selected: string | null;
  onSelect: (doc: string) => void;
}) {
  const { t } = useTranslation();
  return (
    <div className="card max-h-[70vh] overflow-y-auto">
      <div className="border-b border-stone-200 px-3 py-2 text-xs font-medium uppercase tracking-wide text-stone-500 dark:border-stone-800">
        {t("review.chapters")}
      </div>
      <ul>
        {items.map((c) => {
          const isSel = c.documentName === selected;
          return (
            <li key={c.documentName}>
              <button
                onClick={() => onSelect(c.documentName)}
                className={clsx(
                  "flex w-full items-start gap-2 px-3 py-2 text-left text-sm hover:bg-stone-50 dark:hover:bg-stone-800",
                  isSel && "bg-stone-100 dark:bg-stone-800"
                )}
              >
                <FileText className="mt-0.5 h-3.5 w-3.5 shrink-0 text-stone-400" />
                <div className="min-w-0 flex-1">
                  <div className="truncate font-mono text-xs">{shortName(c.documentName)}</div>
                  <div className="mt-0.5 flex flex-wrap gap-1 text-[10px]">
                    {c.accepted > 0 && (
                      <span className="rounded bg-emerald-50 px-1.5 py-0.5 text-emerald-700 dark:bg-emerald-500/10 dark:text-emerald-300">
                        {t("review.accepted", { n: c.accepted })}
                      </span>
                    )}
                    {c.rejected > 0 && (
                      <span className="rounded bg-rose-50 px-1.5 py-0.5 text-rose-700 dark:bg-rose-500/10 dark:text-rose-300">
                        {t("review.rejected", { n: c.rejected })}
                      </span>
                    )}
                    {c.pending > 0 && (
                      <span className="rounded bg-amber-50 px-1.5 py-0.5 text-amber-700 dark:bg-amber-500/10 dark:text-amber-300">
                        {t("review.pending", { n: c.pending })}
                      </span>
                    )}
                    {c.userCount > 0 && (
                      <span className="rounded bg-sky-50 px-1.5 py-0.5 text-sky-700 dark:bg-sky-500/10 dark:text-sky-300">
                        +{c.userCount}
                      </span>
                    )}
                  </div>
                </div>
              </button>
            </li>
          );
        })}
      </ul>
    </div>
  );
}

function ChapterReviewView({ jobId, doc }: { jobId: string; doc: string }) {
  const { t } = useTranslation();
  const toast = useToast();
  const qc = useQueryClient();
  const textRef = useRef<HTMLPreElement>(null);
  const [selectionRange, setSelectionRange] = useState<{ start: number; end: number; text: string } | null>(null);

  const chapter = useQuery({
    queryKey: ["review", jobId, doc],
    queryFn: () => getChapterReview(jobId, doc),
  });

  const invalidate = () => {
    qc.invalidateQueries({ queryKey: ["review", jobId, doc] });
    qc.invalidateQueries({ queryKey: ["reviews", jobId] });
  };

  const decide = useMutation({
    mutationFn: ({ id, decision }: { id: number; decision: ProposalDecision }) =>
      decideProposal(jobId, id, decision),
    onSuccess: invalidate,
    onError: (e) => toast.error(t("review.actionFailed"), e instanceof Error ? e.message : ""),
  });

  const remove = useMutation({
    mutationFn: (id: number) => deleteProposal(jobId, id),
    onSuccess: invalidate,
    onError: (e) => toast.error(t("review.actionFailed"), e instanceof Error ? e.message : ""),
  });

  const addUser = useMutation({
    mutationFn: (text: string) => addUserProposal(jobId, doc, text),
    onSuccess: () => { setSelectionRange(null); invalidate(); },
    onError: (e) => toast.error(t("review.actionFailed"), e instanceof Error ? e.message : ""),
  });

  const reprompt = useMutation({
    mutationFn: (range?: { start: number; end: number }) => repromptChapter(jobId, doc, range),
    onSuccess: ({ added }) => {
      toast.success(t("review.repromptDone", { n: added }));
      invalidate();
    },
    onError: (e) => toast.error(t("review.actionFailed"), e instanceof Error ? e.message : ""),
  });

  // Track text selection inside the visible-text pane. Offsets are computed
  // against the chapter's visibleText (single text node, no markup), so a
  // straight startOffset/endOffset against the same node works.
  function handleSelectionChange() {
    const sel = window.getSelection();
    if (!sel || sel.rangeCount === 0 || sel.isCollapsed) {
      setSelectionRange(null);
      return;
    }
    const r = sel.getRangeAt(0);
    const node = textRef.current?.firstChild;
    if (!node || r.startContainer !== node || r.endContainer !== node) {
      setSelectionRange(null);
      return;
    }
    const start = r.startOffset;
    const end = r.endOffset;
    const txt = chapter.data?.visibleText.slice(start, end) ?? "";
    if (txt.trim().length === 0) { setSelectionRange(null); return; }
    setSelectionRange({ start, end, text: txt });
  }

  // Group proposals by source. LLM proposals are immutable text-wise — only
  // the decision toggles. User proposals can be deleted.
  const grouped = useMemo(() => {
    const llm = chapter.data?.proposals.filter(p => p.source === "Llm") ?? [];
    const user = chapter.data?.proposals.filter(p => p.source === "User") ?? [];
    return { llm, user };
  }, [chapter.data]);

  if (chapter.isLoading) {
    return <div className="card flex items-center gap-2 p-4 text-sm text-stone-500"><Loader2 className="h-4 w-4 animate-spin" /> {t("review.loading")}</div>;
  }
  if (!chapter.data) {
    return <div className="card p-4 text-sm text-stone-500">{t("review.noChapter")}</div>;
  }

  return (
    <div className="space-y-3">
      <div className="card p-3">
        <div className="mb-2 flex items-center justify-between gap-2">
          <div className="font-mono text-xs text-stone-500">{shortName(chapter.data.documentName)}</div>
          <div className="flex items-center gap-2">
            <button
              className="btn-secondary"
              onClick={() => reprompt.mutate(undefined)}
              disabled={reprompt.isPending}
              title={t("review.repromptChapterHint")}
            >
              {reprompt.isPending ? <Loader2 className="h-3.5 w-3.5 animate-spin" /> : <RefreshCw className="h-3.5 w-3.5" />}
              {t("review.repromptChapter")}
            </button>
          </div>
        </div>
        <pre
          ref={textRef}
          onMouseUp={handleSelectionChange}
          onKeyUp={handleSelectionChange}
          className="max-h-[40vh] overflow-y-auto whitespace-pre-wrap break-words rounded bg-stone-50 p-3 text-xs leading-relaxed text-stone-800 dark:bg-stone-900 dark:text-stone-200"
        >
          {chapter.data.visibleText}
        </pre>
        {selectionRange && (
          <div className="mt-2 flex flex-wrap items-center gap-2 rounded border border-sky-200 bg-sky-50 px-3 py-2 text-xs dark:border-sky-500/30 dark:bg-sky-500/10">
            <span className="text-sky-700 dark:text-sky-300">
              {t("review.selected", { n: selectionRange.text.length })}:{" "}
              <span className="font-mono">"{ellipsize(selectionRange.text, 60)}"</span>
            </span>
            <div className="ml-auto flex gap-2">
              <button
                className="btn-primary"
                onClick={() => addUser.mutate(selectionRange.text)}
                disabled={addUser.isPending}
              >
                <Plus className="h-3.5 w-3.5" /> {t("review.addRemoval")}
              </button>
              <button
                className="btn-secondary"
                onClick={() => reprompt.mutate({ start: selectionRange.start, end: selectionRange.end })}
                disabled={reprompt.isPending}
              >
                <RefreshCw className="h-3.5 w-3.5" /> {t("review.repromptSelection")}
              </button>
            </div>
          </div>
        )}
      </div>

      <ProposalSection
        title={t("review.llmProposals", { n: grouped.llm.length })}
        proposals={grouped.llm}
        onDecide={(id, d) => decide.mutate({ id, decision: d })}
      />
      {grouped.user.length > 0 && (
        <ProposalSection
          title={t("review.userProposals", { n: grouped.user.length })}
          proposals={grouped.user}
          onDecide={(id, d) => decide.mutate({ id, decision: d })}
          onDelete={(id) => remove.mutate(id)}
        />
      )}
    </div>
  );
}

function ProposalSection({
  title, proposals, onDecide, onDelete,
}: {
  title: string;
  proposals: Proposal[];
  onDecide: (id: number, decision: ProposalDecision) => void;
  onDelete?: (id: number) => void;
}) {
  const { t } = useTranslation();
  return (
    <div className="card">
      <div className="border-b border-stone-200 px-3 py-2 text-xs font-medium uppercase tracking-wide text-stone-500 dark:border-stone-800">
        {title}
      </div>
      {proposals.length === 0 ? (
        <div className="p-4 text-xs text-stone-500">{t("review.noProposals")}</div>
      ) : (
        <ul className="divide-y divide-stone-200 dark:divide-stone-800">
          {proposals.map((p) => (
            <li key={p.id} className="px-3 py-2">
              <div className="flex items-start gap-2">
                <DecisionBadge decision={p.decision} />
                <div className="min-w-0 flex-1">
                  <div className="font-mono text-xs text-stone-800 dark:text-stone-200">
                    "{ellipsize(p.text, 240)}"
                  </div>
                  {p.reason && (
                    <div className="mt-0.5 text-[11px] text-stone-500 dark:text-stone-400">{p.reason}</div>
                  )}
                </div>
                <div className="flex shrink-0 gap-1">
                  <button
                    className={clsx(
                      "btn-ghost",
                      p.decision === "Accepted" && "ring-1 ring-emerald-300 dark:ring-emerald-500/40"
                    )}
                    onClick={() => onDecide(p.id, "Accepted")}
                    title={t("review.accept")}
                  >
                    <Check className="h-3.5 w-3.5" />
                  </button>
                  <button
                    className={clsx(
                      "btn-ghost",
                      p.decision === "Rejected" && "ring-1 ring-rose-300 dark:ring-rose-500/40"
                    )}
                    onClick={() => onDecide(p.id, "Rejected")}
                    title={t("review.reject")}
                  >
                    <X className="h-3.5 w-3.5" />
                  </button>
                  {onDelete && (
                    <button
                      className="btn-ghost"
                      onClick={() => onDelete(p.id)}
                      title={t("review.delete")}
                    >
                      <Trash2 className="h-3.5 w-3.5" />
                    </button>
                  )}
                </div>
              </div>
            </li>
          ))}
        </ul>
      )}
    </div>
  );
}

function DecisionBadge({ decision }: { decision: ProposalDecision }) {
  const cls = decision === "Accepted"
    ? "bg-emerald-50 text-emerald-700 dark:bg-emerald-500/10 dark:text-emerald-300"
    : decision === "Rejected"
    ? "bg-rose-50 text-rose-700 dark:bg-rose-500/10 dark:text-rose-300"
    : "bg-amber-50 text-amber-700 dark:bg-amber-500/10 dark:text-amber-300";
  return (
    <span className={clsx("mt-0.5 shrink-0 rounded px-1.5 py-0.5 text-[10px] font-medium", cls)}>
      {decision}
    </span>
  );
}

function shortName(name: string): string {
  // EPUB/Text/0042_… → 0042_…
  const last = name.split("/").pop() ?? name;
  return last;
}

function ellipsize(s: string, n: number) {
  return s.length <= n ? s : s.slice(0, n) + "…";
}
