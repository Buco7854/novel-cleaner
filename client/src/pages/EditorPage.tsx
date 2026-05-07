import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { ArrowLeft, Check, FileText, Loader2, RotateCcw, Save, ThumbsDown, ThumbsUp } from "lucide-react";
import { useEffect, useMemo, useRef, useState } from "react";
import { useTranslation } from "react-i18next";
import { Link, useParams } from "react-router-dom";
import { acceptHunk, commitPages, discardPage, getPage, listPages, PageEntry, rejectHunk, writePage } from "../api/pages";
import { getJob } from "../api/jobs";
import { useToast } from "../contexts/ToastContext";

/**
 * Page-level editor backed by the job's git repo. The left pane is a flat
 * list of every page in chapter order; the right pane shows the working-tree
 * content of the selected page plus the unified diff against HEAD when the
 * page has been modified (by the worker, by the user, or both).
 *
 * Saves are debounced and write to the working tree only — the user
 * explicitly commits via the top-bar button. Discard rolls one page back to
 * HEAD; the per-hunk accept/reject UI lands once the server-side hunk
 * applier ships.
 */
export function EditorPage() {
  const { t } = useTranslation();
  const toast = useToast();
  const { id = "" } = useParams();
  const qc = useQueryClient();

  const job = useQuery({ queryKey: ["job", id], queryFn: () => getJob(id), enabled: !!id });
  const pages = useQuery({
    queryKey: ["pages", id],
    queryFn: () => listPages(id),
    enabled: !!id,
    refetchInterval: 4000, // pick up worker writes without manual refresh
  });

  const [selected, setSelected] = useState<string | null>(null);

  // Auto-select the first page once the listing arrives, so the right pane
  // isn't empty when the user lands here. Keep the user's choice on later
  // refetches.
  useEffect(() => {
    if (!selected && pages.data && pages.data.length > 0) {
      setSelected(pages.data[0].path);
    }
  }, [pages.data, selected]);

  const page = useQuery({
    queryKey: ["page", id, selected],
    queryFn: () => getPage(id, selected!),
    enabled: !!id && !!selected,
  });

  // Local buffer for the content the user is editing. Synced from the
  // server response when the selected page changes; debounced PUTs send it
  // back. We need a ref-mirror because the debounce timer reads the latest
  // value without re-binding.
  const [draft, setDraft] = useState<string>("");
  const draftRef = useRef("");
  const lastSavedRef = useRef("");
  useEffect(() => {
    if (page.data) {
      setDraft(page.data.content);
      draftRef.current = page.data.content;
      lastSavedRef.current = page.data.content;
    }
  }, [page.data]);

  const save = useMutation({
    mutationFn: (content: string) => writePage(id, selected!, content),
    onSuccess: (_d, content) => {
      lastSavedRef.current = content;
      qc.invalidateQueries({ queryKey: ["page", id, selected] });
      qc.invalidateQueries({ queryKey: ["pages", id] });
    },
    onError: (e) => toast.error(t("editor.saveFailed"), e instanceof Error ? e.message : ""),
  });

  // Debounced auto-save. Fires 600ms after the user stops typing, and only
  // if the buffer differs from what we last wrote — keeps idle pages off
  // the wire.
  useEffect(() => {
    draftRef.current = draft;
    if (!selected || !page.data) return;
    if (draft === lastSavedRef.current) return;
    const t = setTimeout(() => {
      if (draftRef.current === lastSavedRef.current) return;
      save.mutate(draftRef.current);
    }, 600);
    return () => clearTimeout(t);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [draft, selected]);

  const discard = useMutation({
    mutationFn: () => discardPage(id, selected!),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ["page", id, selected] });
      qc.invalidateQueries({ queryKey: ["pages", id] });
    },
    onError: (e) => toast.error(t("editor.discardFailed"), e instanceof Error ? e.message : ""),
  });

  const commit = useMutation({
    mutationFn: () => commitPages(id, "User edits"),
    onSuccess: (r) => {
      if (r.committed) toast.success(t("editor.commitSuccess"));
      else toast.error(t("editor.commitNothing"));
      qc.invalidateQueries({ queryKey: ["page", id, selected] });
      qc.invalidateQueries({ queryKey: ["pages", id] });
    },
    onError: (e) => toast.error(t("editor.commitFailed"), e instanceof Error ? e.message : ""),
  });

  const dirtyCount = useMemo(
    () => (pages.data ?? []).filter((p) => p.status === "Modified").length,
    [pages.data],
  );

  if (!job.data) {
    return (
      <div className="grid h-full place-items-center">
        <Loader2 className="h-5 w-5 animate-spin text-stone-400" />
      </div>
    );
  }

  return (
    <div className="flex h-[calc(100vh-6rem)] flex-col gap-4">
      <div className="flex flex-wrap items-center gap-3">
        <Link to={`/jobs/${id}`} className="btn-ghost shrink-0">
          <ArrowLeft className="h-4 w-4" /> {t("editor.backToJob")}
        </Link>
        <FileText className="h-5 w-5 shrink-0 text-stone-400" />
        <h1 className="min-w-0 flex-1 truncate text-xl font-semibold tracking-tight sm:text-2xl">
          {job.data.fileName}
        </h1>
        <span className="text-xs text-stone-500 dark:text-stone-400">
          {t("editor.dirtyCount", { n: dirtyCount })}
        </span>
        <button
          className="btn-primary shrink-0"
          onClick={() => commit.mutate()}
          disabled={commit.isPending || dirtyCount === 0}
          title={t("editor.commitHint")}
        >
          {commit.isPending ? <Loader2 className="h-4 w-4 animate-spin" /> : <Check className="h-4 w-4" />}
          {t("editor.commit")}
        </button>
      </div>

      <div className="grid min-h-0 flex-1 grid-cols-[14rem_1fr] gap-4 sm:grid-cols-[18rem_1fr]">
        <FileTree
          pages={pages.data ?? []}
          selected={selected}
          onSelect={setSelected}
          loading={pages.isLoading}
        />

        <div className="card flex min-h-0 min-w-0 flex-col">
          {!selected ? (
            <div className="grid flex-1 place-items-center text-sm text-stone-500">
              {t("editor.selectAPage")}
            </div>
          ) : page.isLoading ? (
            <div className="grid flex-1 place-items-center">
              <Loader2 className="h-5 w-5 animate-spin text-stone-400" />
            </div>
          ) : !page.data ? (
            <div className="grid flex-1 place-items-center text-sm text-stone-500">
              {t("editor.pageNotFound")}
            </div>
          ) : (
            <PageView
              jobId={id}
              path={page.data.path}
              status={page.data.status}
              diff={page.data.diff}
              draft={draft}
              onChange={setDraft}
              onDiscard={() => discard.mutate()}
              discarding={discard.isPending}
              saving={save.isPending}
              dirty={draft !== lastSavedRef.current}
            />
          )}
        </div>
      </div>
    </div>
  );
}

interface FileTreeProps {
  pages: PageEntry[];
  selected: string | null;
  onSelect: (p: string) => void;
  loading: boolean;
}

function FileTree({ pages, selected, onSelect, loading }: FileTreeProps) {
  const { t } = useTranslation();
  return (
    <div className="card flex min-h-0 flex-col">
      <div className="border-b border-stone-200 px-3 py-2 text-xs font-medium uppercase tracking-wide text-stone-500 dark:border-stone-800">
        {t("editor.pages", { n: pages.length })}
      </div>
      <div className="min-h-0 flex-1 overflow-y-auto">
        {loading ? (
          <div className="grid place-items-center p-6">
            <Loader2 className="h-4 w-4 animate-spin text-stone-400" />
          </div>
        ) : pages.length === 0 ? (
          <div className="px-3 py-6 text-center text-xs text-stone-500">
            {t("editor.noPages")}
          </div>
        ) : (
          <ul className="py-1">
            {pages.map((p) => {
              const name = p.path.replace(/^pages\//, "");
              const isSel = p.path === selected;
              const isDirty = p.status === "Modified";
              return (
                <li key={p.path}>
                  <button
                    onClick={() => onSelect(p.path)}
                    className={
                      "flex w-full items-center gap-2 px-3 py-1.5 text-left text-xs " +
                      (isSel
                        ? "bg-stone-100 font-medium dark:bg-stone-800"
                        : "hover:bg-stone-50 dark:hover:bg-stone-900")
                    }
                  >
                    <span
                      className={
                        "h-1.5 w-1.5 shrink-0 rounded-full " +
                        (isDirty ? "bg-amber-500" : "bg-transparent")
                      }
                      aria-label={isDirty ? "modified" : "clean"}
                    />
                    <span className="truncate font-mono">{name}</span>
                  </button>
                </li>
              );
            })}
          </ul>
        )}
      </div>
    </div>
  );
}

interface PageViewProps {
  jobId: string;
  path: string;
  status: string;
  diff: string;
  draft: string;
  onChange: (s: string) => void;
  onDiscard: () => void;
  discarding: boolean;
  saving: boolean;
  dirty: boolean;
}

function PageView({ jobId, path, status, diff, draft, onChange, onDiscard, discarding, saving, dirty }: PageViewProps) {
  const { t } = useTranslation();
  const name = path.replace(/^pages\//, "");
  const isModified = status === "Modified";

  return (
    <>
      <div className="flex flex-wrap items-center gap-2 border-b border-stone-200 px-3 py-2 text-xs dark:border-stone-800">
        <span className="font-mono text-stone-700 dark:text-stone-200">{name}</span>
        {isModified && (
          <span className="rounded bg-amber-100 px-1.5 py-0.5 font-medium text-amber-800 dark:bg-amber-500/20 dark:text-amber-300">
            {t("editor.modified")}
          </span>
        )}
        <span className="ml-auto flex items-center gap-2 text-stone-500">
          {saving && <Loader2 className="h-3 w-3 animate-spin" aria-label="saving" />}
          {!saving && dirty && <Save className="h-3 w-3" aria-label="unsaved" />}
          {!saving && !dirty && isModified && <span>{t("editor.savedDirtyHint")}</span>}
          {!saving && !dirty && !isModified && <span>{t("editor.clean")}</span>}
        </span>
        {isModified && (
          <button
            onClick={onDiscard}
            disabled={discarding}
            className="btn-ghost h-7 text-xs"
            title={t("editor.discardHint")}
          >
            {discarding ? <Loader2 className="h-3 w-3 animate-spin" /> : <RotateCcw className="h-3 w-3" />}
            {t("editor.discard")}
          </button>
        )}
      </div>

      <div className="grid min-h-0 flex-1 grid-cols-1 gap-0 lg:grid-cols-2">
        <textarea
          value={draft}
          onChange={(e) => onChange(e.target.value)}
          spellCheck={false}
          className="min-h-0 resize-none border-0 bg-transparent p-3 font-serif text-sm leading-relaxed outline-none"
          placeholder={t("editor.emptyPlaceholder") ?? ""}
        />
        <div className="min-h-0 overflow-y-auto border-stone-200 bg-stone-50 p-3 dark:border-stone-800 dark:bg-stone-900/40 lg:border-l">
          <DiffView jobId={jobId} path={path} diff={diff} />
        </div>
      </div>
    </>
  );
}

interface ParsedHunk {
  header: string;
  body: string[];
}

/**
 * Splits a unified diff into hunks for inline accept/reject controls. The
 * pre-hunk preamble (file headers, "diff --git", "index …") is dropped — the
 * editor already shows the file path in the toolbar, so repeating it here
 * just adds noise.
 */
function parseDiffHunks(diff: string): ParsedHunk[] {
  const hunks: ParsedHunk[] = [];
  if (!diff) return hunks;
  let cur: ParsedHunk | null = null;
  for (const line of diff.split("\n")) {
    if (line.startsWith("@@")) {
      cur = { header: line, body: [] };
      hunks.push(cur);
      continue;
    }
    if (!cur) continue;
    if (line.startsWith("\\")) continue;
    const c = line[0];
    if (c === " " || c === "+" || c === "-") cur.body.push(line);
  }
  return hunks;
}

function DiffView({ jobId, path, diff }: { jobId: string; path: string; diff: string }) {
  const { t } = useTranslation();
  const toast = useToast();
  const qc = useQueryClient();

  const hunks = useMemo(() => parseDiffHunks(diff), [diff]);

  const reject = useMutation({
    mutationFn: (index: number) => rejectHunk(jobId, path, index),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ["page", jobId, path] });
      qc.invalidateQueries({ queryKey: ["pages", jobId] });
    },
    onError: (e) => toast.error(t("editor.rejectFailed"), e instanceof Error ? e.message : ""),
  });

  const accept = useMutation({
    mutationFn: (index: number) => acceptHunk(jobId, path, index),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ["page", jobId, path] });
      qc.invalidateQueries({ queryKey: ["pages", jobId] });
    },
    onError: (e) => toast.error(t("editor.acceptFailed"), e instanceof Error ? e.message : ""),
  });

  if (hunks.length === 0) {
    return <div className="text-xs italic text-stone-500">{t("editor.noDiff")}</div>;
  }

  // Track which hunk is mid-flight so we can disable just its buttons,
  // not the whole panel — react-query exposes the mutate() argument as
  // .variables for the in-flight call.
  const busy = reject.isPending
    ? { idx: reject.variables, kind: "reject" as const }
    : accept.isPending
    ? { idx: accept.variables, kind: "accept" as const }
    : null;

  return (
    <div className="space-y-3">
      {hunks.map((h, i) => (
        <div
          key={i}
          className="overflow-hidden rounded border border-stone-200 dark:border-stone-700"
        >
          <div className="flex items-center gap-2 border-b border-stone-200 bg-stone-100 px-2 py-1 text-xs dark:border-stone-700 dark:bg-stone-800">
            <span className="flex-1 truncate font-mono text-violet-600 dark:text-violet-300">
              {h.header}
            </span>
            <button
              onClick={() => accept.mutate(i)}
              disabled={busy?.idx === i}
              className="btn-ghost h-6 px-2 text-xs"
              title={t("editor.acceptHunkHint")}
            >
              {busy?.idx === i && busy.kind === "accept" ? (
                <Loader2 className="h-3 w-3 animate-spin" />
              ) : (
                <ThumbsUp className="h-3 w-3" />
              )}
              {t("editor.acceptHunk")}
            </button>
            <button
              onClick={() => reject.mutate(i)}
              disabled={busy?.idx === i}
              className="btn-ghost h-6 px-2 text-xs"
              title={t("editor.rejectHunkHint")}
            >
              {busy?.idx === i && busy.kind === "reject" ? (
                <Loader2 className="h-3 w-3 animate-spin" />
              ) : (
                <ThumbsDown className="h-3 w-3" />
              )}
              {t("editor.rejectHunk")}
            </button>
          </div>
          <pre className="whitespace-pre-wrap break-words font-mono text-xs leading-snug">
            {h.body.map((line, j) => {
              let cls = "";
              if (line.startsWith("+")) {
                cls = "bg-emerald-100/70 text-emerald-900 dark:bg-emerald-500/15 dark:text-emerald-200";
              } else if (line.startsWith("-")) {
                cls = "bg-rose-100/70 text-rose-900 dark:bg-rose-500/15 dark:text-rose-200";
              }
              return (
                <div key={j} className={cls}>
                  {line || " "}
                </div>
              );
            })}
          </pre>
        </div>
      ))}
    </div>
  );
}
