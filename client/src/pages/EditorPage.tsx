import { Menu, MenuButton, MenuItem, MenuItems, Transition, TransitionChild } from "@headlessui/react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { HubConnectionState } from "@microsoft/signalr";
import {
  AlertTriangle, ArrowLeft, BookOpen, Check, ChevronDown, ChevronRight,
  Download, FolderInput, History, Info, Loader2, MoreHorizontal, PanelLeft, Pause,
  Play, RefreshCcw, Save, Sparkles, Square, ThumbsDown, ThumbsUp, Trash2, X,
} from "lucide-react";
import { Fragment, useCallback, useEffect, useLayoutEffect, useMemo, useRef, useState } from "react";
import { useTranslation } from "react-i18next";
import { Link, useParams } from "react-router-dom";
import {
  acceptHunk, commitManyPages, commitPage, commitPages, discardManyPages,
  discardPage, getPage, getPageAtCommit, listPageHistory, listPages, PageEntry,
  pagePreviewUrl, PageRevision, PreviewSource, rejectHunk, resetRepo,
  restorePageToCommit, writePage,
} from "../api/pages";
import {
  cancelBook, clearBookLogs, downloadUrl, getBook, BookMetadata,
  bookDisplayName, BookStatus, pauseBook, pushToFolder, resumeBook,
  saveBookMetadata, saveBookPrompt,
} from "../api/books";
import { createBookHub, BookLogEvent } from "../api/booksHub";
import { runAi } from "../api/reviews";
import { fetchSettings } from "../api/settings";
import { BookStatusPill } from "../components/BookStatusPill";
import { useConfirm } from "../contexts/ConfirmContext";
import { useTheme } from "../contexts/ThemeContext";
import { useToast } from "../contexts/ToastContext";
import { clsx } from "../utils/clsx";
import { timeOnly } from "../utils/date";

interface LogLine {
  ts: string;
  level: string;
  message: string;
  detail?: string | null;
  groupId?: string | null;
}

const TREE_WIDTH_KEY = "editor.treeWidth";
const TREE_WIDTH_MIN = 180;
const TREE_WIDTH_MAX = 520;
const TREE_WIDTH_DEFAULT = 280;

/**
 * Single-pane book view that opens when the user clicks a file in the
 * library. Folds in the work that used to live in JobDetailPage:
 *  - status pill + run/pause/finalize/download buttons (overflow menu)
 *  - per-page editor (left = file tree, right = working-tree text + diff)
 *  - per-page checkboxes drive the "Run AI on selected" path
 *  - collapsible live log panel pinned to the bottom (SignalR feed)
 *
 * The Run-AI primary action lives at the foot of the file tree, visually
 * tied to the checkboxes that drive its filter. The tree column is
 * user-resizable on desktop (drag handle, persisted to localStorage) and
 * collapses to a slide-out drawer on mobile.
 */
export function EditorPage() {
  const { t } = useTranslation();
  const toast = useToast();
  const confirm = useConfirm();
  const { id = "" } = useParams();
  const qc = useQueryClient();

  const job = useQuery({
    queryKey: ["book", id],
    queryFn: () => getBook(id),
    enabled: !!id,
    refetchInterval: 4000,
  });
  // App-level settings (we only care about aiEnabled here — the file
  // tree's Run-AI button is hidden entirely when the admin turns it off).
  const settings = useQuery({ queryKey: ["settings"], queryFn: fetchSettings });
  const aiEnabled = settings.data?.app.aiEnabled ?? true;
  const pages = useQuery({
    queryKey: ["pages", id],
    queryFn: () => listPages(id),
    enabled: !!id,
    refetchInterval: 4000,
  });

  // ---- Page selection (drives "Run AI on selected") --------------------
  const [picked, setPicked] = useState<Set<string>>(new Set());
  const togglePick = (path: string) =>
    setPicked((cur) => {
      const next = new Set(cur);
      next.has(path) ? next.delete(path) : next.add(path);
      return next;
    });
  const pickAll = () => setPicked(new Set((pages.data ?? []).map((p) => p.path)));
  const pickNone = () => setPicked(new Set());

  // ---- Currently-displayed page ---------------------------------------
  const [selected, setSelected] = useState<string | null>(null);
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

  // ---- Editor draft buffer + debounced auto-save -----------------------
  const [draft, setDraft] = useState<string>("");
  const draftRef = useRef("");
  const lastSavedRef = useRef("");
  // Counter that ticks whenever the page query yields fresh content.
  // Drives the preview iframe's cache key so it reloads after every
  // mutation that touches the working tree — accept-hunk, reject-hunk,
  // discard, save, etc. All of them invalidate ["page", id, path],
  // which refetches and lands here with new content.
  const [previewTick, setPreviewTick] = useState(0);
  const lastPreviewContent = useRef<string | null>(null);

  useEffect(() => {
    if (page.data) {
      setDraft(page.data.content);
      draftRef.current = page.data.content;
      lastSavedRef.current = page.data.content;
      // Only bump on actual content changes — react-query may hand us a
      // new page.data reference with identical content, and an iframe
      // reload on every poll would steal the user's scroll position.
      if (page.data.content !== lastPreviewContent.current) {
        lastPreviewContent.current = page.data.content;
        setPreviewTick((n) => n + 1);
      }
    }
  }, [page.data]);

  // Keep Diff/Preview tab selection across chapter switches. PageView
  // unmounts during page-load loading state, so without lifting this
  // here the user would land back on Diff every time they pick a new
  // chapter from the file tree.
  const [editorTab, setEditorTab] = useState<"diff" | "preview">("diff");
  // Mobile-only: the edit textarea and view pane don't fit comfortably
  // stacked in a phone viewport, so on small screens we show one at a time.
  // Lifted here for the same reason as editorTab — survives chapter switches
  // (PageView unmounts during the loading state).
  const [mobilePane, setMobilePane] = useState<"edit" | "view">("edit");

  const save = useMutation({
    mutationFn: (content: string) => writePage(id, selected!, content),
    onSuccess: (_d, content) => {
      lastSavedRef.current = content;
      qc.invalidateQueries({ queryKey: ["page", id, selected] });
      qc.invalidateQueries({ queryKey: ["pages", id] });
      // previewTick is bumped centrally in the page.data effect — the
      // refetch this triggers will land there with new content.
    },
    onError: (e) => toast.error(t("editor.saveFailed"), e instanceof Error ? e.message : ""),
  });

  useEffect(() => {
    draftRef.current = draft;
    if (!selected || !page.data) return;
    if (draft === lastSavedRef.current) return;
    const tm = setTimeout(() => {
      if (draftRef.current === lastSavedRef.current) return;
      save.mutate(draftRef.current);
    }, 600);
    return () => clearTimeout(tm);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [draft, selected]);

  const discard = useMutation({
    mutationFn: () => discardPage(id, selected!),
    onSuccess: async () => {
      await Promise.all([
        qc.invalidateQueries({ queryKey: ["page", id, selected] }),
        qc.invalidateQueries({ queryKey: ["pages", id] }),
      ]);
    },
    onError: (e) => toast.error(t("editor.discardFailed"), e instanceof Error ? e.message : ""),
  });

  // Page-level "accept everything on this page" — commits just this one
  // page so the user can ratify it without sweeping in other pages'
  // pending edits.
  const acceptPage = useMutation({
    mutationFn: () => commitPage(id, selected!, `Accept page ${selected}`),
    onSuccess: async (r) => {
      if (r.committed) toast.success(t("editor.acceptPageSuccess"));
      else toast.error(t("editor.commitNothing"));
      await Promise.all([
        qc.invalidateQueries({ queryKey: ["page", id, selected] }),
        qc.invalidateQueries({ queryKey: ["pages", id] }),
      ]);
    },
    onError: (e) => toast.error(t("editor.acceptPageFailed"), e instanceof Error ? e.message : ""),
  });

  // Batch accept / reject — operate on whatever the user has checked in
  // the file tree. Skip clean paths server-side (silent no-op) and commit
  // the lot in one go. Selection is cleared on success.
  const acceptSelected = useMutation({
    mutationFn: (paths: string[]) =>
      commitManyPages(id, paths, `Accept ${paths.length} selected page(s)`),
    onSuccess: async (r) => {
      if (r.committed) {
        toast.success(t("editor.acceptSelectedSuccess"));
        setPicked(new Set());
      } else {
        toast.error(t("editor.commitNothing"));
      }
      await Promise.all([
        qc.invalidateQueries({ queryKey: ["page", id, selected] }),
        qc.invalidateQueries({ queryKey: ["pages", id] }),
      ]);
    },
    onError: (e) => toast.error(t("editor.acceptSelectedFailed"), e instanceof Error ? e.message : ""),
  });

  const rejectSelected = useMutation({
    mutationFn: (paths: string[]) => discardManyPages(id, paths),
    onSuccess: async () => {
      toast.success(t("editor.rejectSelectedSuccess"));
      setPicked(new Set());
      await Promise.all([
        qc.invalidateQueries({ queryKey: ["page", id, selected] }),
        qc.invalidateQueries({ queryKey: ["pages", id] }),
      ]);
    },
    onError: (e) => toast.error(t("editor.rejectSelectedFailed"), e instanceof Error ? e.message : ""),
  });

  const commit = useMutation({
    mutationFn: () => commitPages(id, "User edits"),
    onSuccess: async (r) => {
      if (r.committed) toast.success(t("editor.commitSuccess"));
      else toast.error(t("editor.commitNothing"));
      await Promise.all([
        qc.invalidateQueries({ queryKey: ["page", id, selected] }),
        qc.invalidateQueries({ queryKey: ["pages", id] }),
      ]);
    },
    onError: (e) => toast.error(t("editor.commitFailed"), e instanceof Error ? e.message : ""),
  });

  // ---- Job lifecycle actions ------------------------------------------
  const drop = useMutation({
    mutationFn: () => pushToFolder(id),
    onSuccess: (r) => toast.success(t("bookDetail.dropSuccess"), r.destination),
    onError: (e) => toast.error(t("bookDetail.dropFailed"), e instanceof Error ? e.message : ""),
  });

  const pause = useMutation({
    mutationFn: () => pauseBook(id),
    onSuccess: () => { setStatus("Paused"); qc.invalidateQueries({ queryKey: ["books"] }); },
    onError: (e) => toast.error(t("bookDetail.pauseFailed"), e instanceof Error ? e.message : ""),
  });

  const resume = useMutation({
    mutationFn: () => resumeBook(id),
    onSuccess: () => { setStatus("Running"); qc.invalidateQueries({ queryKey: ["books"] }); },
    onError: (e) => toast.error(t("bookDetail.resumeFailed"), e instanceof Error ? e.message : ""),
  });

  const cancel = useMutation({
    mutationFn: () => cancelBook(id),
    // Status flips to whatever the derived display rules return on next
    // fetch (Idle if clean, AwaitingReview if proposals were already
    // written to the working tree). Don't optimistically setStatus here —
    // letting the refetch land avoids a flash of "Canceled" before the
    // derived value arrives.
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ["book", id] });
      qc.invalidateQueries({ queryKey: ["books"] });
    },
    onError: (e) => toast.error(t("bookDetail.cancelFailed"), e instanceof Error ? e.message : ""),
  });

  // Clearing logs hits the server so a refresh doesn't repopulate them.
  // Wrapped in a mutation (not fire-and-forget) so a 4xx surfaces as a
  // toast instead of being silently swallowed — and so the cached job
  // query is invalidated, which re-fetches the (now empty) log list.
  const clearLogs = useMutation({
    mutationFn: () => clearBookLogs(id),
    onSuccess: () => {
      setLogs([]);
      qc.invalidateQueries({ queryKey: ["book", id] });
    },
    onError: (e) => toast.error("Could not clear logs", e instanceof Error ? e.message : ""),
  });

  const runAiMut = useMutation({
    mutationFn: (paths: string[]) => runAi(id, paths.length > 0 ? paths : undefined),
    onSuccess: () => {
      toast.success(t("editor.runAiQueued"));
      setStatus("Queued");
      setLogsOpen(true);
      qc.invalidateQueries({ queryKey: ["book", id] });
      qc.invalidateQueries({ queryKey: ["books"] });
    },
    onError: (e) => toast.error(t("editor.runAiFailed"), e instanceof Error ? e.message : ""),
  });

  const reset = useMutation({
    mutationFn: () => resetRepo(id),
    onSuccess: (r) => {
      toast.success(t("editor.resetSuccess", { n: r.pages }));
      // Drop everything that touches the repo so the editor refetches
      // fresh content + diff + page list.
      qc.invalidateQueries({ queryKey: ["pages", id] });
      qc.invalidateQueries({ queryKey: ["page", id] });
      qc.invalidateQueries({ queryKey: ["book", id] });
    },
    onError: (e) => toast.error(t("editor.resetFailed"), e instanceof Error ? e.message : ""),
  });

  // ---- Live status / log feed (SignalR) -------------------------------
  const [logs, setLogs] = useState<LogLine[]>([]);
  const [status, setStatus] = useState<BookStatus | null>(null);
  const [progress, setProgress] = useState<number | null>(null);
  const [done, setDone] = useState<number | null>(null);
  const [total, setTotal] = useState<number | null>(null);
  const [logsOpen, setLogsOpen] = useState(false);

  // Seed logs from the persisted job once. Subsequent updates come from
  // SignalR — re-seeding on every job refetch would clobber a user's
  // manual Clear with whatever the server still has on disk.
  const logsSeededRef = useRef(false);
  useEffect(() => {
    if (job.data && !logsSeededRef.current) {
      setLogs(job.data.logs.map((l) => ({
        ts: l.timestamp, level: l.level, message: l.message,
        detail: l.detail ?? null, groupId: l.groupId ?? null,
      })));
      logsSeededRef.current = true;
    }
    if (job.data) setStatus(job.data.status);
  }, [job.data]);

  useEffect(() => {
    if (status === "Running" || status === "Queued") setLogsOpen(true);
  }, [status]);

  useEffect(() => {
    if (!id) return;
    const hub = createBookHub();
    let mounted = true;

    const offLog = hub.onLog((e: BookLogEvent) => {
      if (e.bookId !== id) return;
      setLogs((cur) => [...cur, {
        ts: e.timestamp, level: e.level, message: e.message,
        detail: e.detail ?? null, groupId: e.groupId ?? null,
      }]);
    });
    const offStatus = hub.onStatus((e) => {
      if (e.bookId !== id) return;
      const next = e.status as BookStatus;
      if (typeof e.progress === "number") setProgress(e.progress);
      if (typeof e.done === "number") setDone(e.done);
      if (typeof e.total === "number") setTotal(e.total);
      // The SignalR feed forwards the raw worker status (Completed / Failed
      // / Canceled). The display status is derived from the repo on read,
      // so for terminal states we skip the optimistic setStatus and let
      // the invalidate-then-refetch pull in Idle/AwaitingReview directly.
      if (next === "Completed" || next === "Failed" || next === "Canceled") {
        qc.invalidateQueries({ queryKey: ["book", id] });
        qc.invalidateQueries({ queryKey: ["books"] });
        qc.invalidateQueries({ queryKey: ["pages", id] });
        // Per-path page query is keyed `["page", id, path]` — prefix
        // invalidation refetches every open page so AI edits show up
        // without forcing a manual refresh.
        qc.invalidateQueries({ queryKey: ["page", id] });
      } else {
        setStatus(next);
      }
    });

    async function ensureLiveAndRefresh() {
      if (!mounted) return;
      try {
        if (hub.conn.state === HubConnectionState.Disconnected) await hub.start();
        await hub.subscribe(id);
      } catch { /* visibility handler will retry */ }
      qc.invalidateQueries({ queryKey: ["book", id] });
      qc.invalidateQueries({ queryKey: ["books"] });
    }

    hub.conn.onreconnected(() => { void ensureLiveAndRefresh(); });

    function onVisibilityChange() {
      if (document.visibilityState === "visible") void ensureLiveAndRefresh();
    }
    document.addEventListener("visibilitychange", onVisibilityChange);

    (async () => {
      try {
        await hub.start();
        if (mounted) await hub.subscribe(id);
      } catch { /* retry on next visibility */ }
    })();

    return () => {
      mounted = false;
      offLog();
      offStatus();
      document.removeEventListener("visibilitychange", onVisibilityChange);
      hub.stop().catch(() => {});
    };
  }, [id, qc]);

  // ---- Resizable file-tree column (desktop only) ----------------------
  const [treeWidth, setTreeWidth] = useState<number>(() => {
    if (typeof window === "undefined") return TREE_WIDTH_DEFAULT;
    const v = Number(localStorage.getItem(TREE_WIDTH_KEY));
    return Number.isFinite(v) && v >= TREE_WIDTH_MIN && v <= TREE_WIDTH_MAX ? v : TREE_WIDTH_DEFAULT;
  });
  const treeWidthRef = useRef(treeWidth);
  treeWidthRef.current = treeWidth;
  useEffect(() => { localStorage.setItem(TREE_WIDTH_KEY, String(treeWidth)); }, [treeWidth]);

  const splitRef = useRef<HTMLDivElement>(null);
  const onResizeMouseDown = useCallback((startEv: React.MouseEvent) => {
    startEv.preventDefault();
    const startX = startEv.clientX;
    const startW = treeWidthRef.current;
    document.body.style.cursor = "col-resize";
    document.body.style.userSelect = "none";
    function onMove(e: MouseEvent) {
      const next = Math.max(TREE_WIDTH_MIN, Math.min(TREE_WIDTH_MAX, startW + (e.clientX - startX)));
      setTreeWidth(next);
    }
    function onUp() {
      document.removeEventListener("mousemove", onMove);
      document.removeEventListener("mouseup", onUp);
      document.body.style.cursor = "";
      document.body.style.userSelect = "";
    }
    document.addEventListener("mousemove", onMove);
    document.addEventListener("mouseup", onUp);
  }, []);

  // ---- Page history drawer ---------------------------------------------
  // Per-page git-backed version history. Opens on demand via the History
  // button in the page header.
  const [historyOpen, setHistoryOpen] = useState(false);

  // ---- Per-book AI prompt editor --------------------------------------
  const [promptOpen, setPromptOpen] = useState(false);
  const [metadataOpen, setMetadataOpen] = useState(false);

  // ---- Mobile drawer ---------------------------------------------------
  const [drawerOpen, setDrawerOpen] = useState(false);
  // Closing the drawer when the user picks a page so the editor takes over
  // immediately on small screens — no "you picked, now close it" friction.
  function pickFromTree(path: string) {
    setSelected(path);
    setDrawerOpen(false);
  }

  // ---- Derived flags --------------------------------------------------
  const dirtyCount = useMemo(
    () => (pages.data ?? []).filter((p) => p.status === "Modified").length,
    [pages.data],
  );
  // Pages that the user has both checked AND that have pending changes —
  // batch accept/reject only acts on these, so the buttons surface this
  // number rather than the raw selection count.
  const pickedDirtyPaths = useMemo(
    () => (pages.data ?? [])
      .filter((p) => picked.has(p.path) && p.status === "Modified")
      .map((p) => p.path),
    [pages.data, picked],
  );
  // Worker log lines carry the EPUB document name in their `groupId`. Build
  // a name → page-path lookup so the user can click a doc name in the log
  // and jump straight to that page in the editor.
  const docToPage = useMemo(() => {
    const m = new Map<string, string>();
    for (const p of pages.data ?? []) if (p.docName) m.set(p.docName, p.path);
    return m;
  }, [pages.data]);

  function jumpToDoc(docName: string) {
    const path = docToPage.get(docName);
    if (path) setSelected(path);
  }

  // ---- Partial-run flagging -------------------------------------------
  // Pages whose last AI pass produced unmatched items (level="partial") —
  // the LLM proposed removals but the verbatim match failed for some.
  // Surface this as an amber badge in the file tree + a banner inside the
  // page editor so the user knows to double-check manually.
  const partialByPage = useMemo(() => {
    const out = new Map<string, number>();
    for (const l of logs) {
      if (l.level !== "partial" || !l.groupId) continue;
      const path = docToPage.get(l.groupId);
      if (!path) continue;
      // Pull the "{n} unmatched" number out of the worker's message format.
      // Falls back to 0 when format drifts — the warning still shows.
      const m = /(\d+)\s+unmatched/.exec(l.message);
      out.set(path, m ? parseInt(m[1], 10) : 0);
    }
    return out;
  }, [logs, docToPage]);
  // ---- Low-confidence flagging ----------------------------------------
  // Pages whose last AI pass landed at least one item the LLM marked
  // `watermark: false` (level="suspicious"). Drives the cyan dot in the
  // file tree so the user knows which pages need a closer look.
  const suspiciousByPage = useMemo(() => {
    const out = new Map<string, number>();
    for (const l of logs) {
      if (l.level !== "suspicious" || !l.groupId) continue;
      const path = docToPage.get(l.groupId);
      if (!path) continue;
      const m = /(\d+)\s+suspicious/.exec(l.message);
      out.set(path, m ? parseInt(m[1], 10) : 0);
    }
    return out;
  }, [logs, docToPage]);
  const isInflight = status === "Running" || status === "Queued" || status === "Paused";
  const showProgress = (status === "Running" || status === "Paused") && total !== null && total > 0;

  if (job.isLoading || !job.data) {
    return (
      <div className="grid h-full place-items-center">
        <Loader2 className="h-5 w-5 animate-spin text-stone-400" />
      </div>
    );
  }

  const treeContent = (
    <FileTree
      pages={pages.data ?? []}
      selected={selected}
      picked={picked}
      onSelect={pickFromTree}
      onTogglePick={togglePick}
      onPickAll={pickAll}
      onPickNone={pickNone}
      loading={pages.isLoading}
      partialByPage={partialByPage}
      suspiciousByPage={suspiciousByPage}
      aiEnabled={aiEnabled}
      onRunAi={() => runAiMut.mutate(Array.from(picked))}
      runAiPending={runAiMut.isPending}
      runAiDisabled={runAiMut.isPending || isInflight}
      onCommit={() => commit.mutate()}
      commitPending={commit.isPending}
      dirtyCount={dirtyCount}
      pickedDirtyCount={pickedDirtyPaths.length}
      onAcceptSelected={() => acceptSelected.mutate(pickedDirtyPaths)}
      acceptSelectedPending={acceptSelected.isPending}
      onRejectSelected={async () => {
        const ok = await confirm({
          title: t("editor.rejectSelected", { n: pickedDirtyPaths.length }),
          body: t("editor.rejectSelectedConfirm", { n: pickedDirtyPaths.length }),
          confirmLabel: t("editor.rejectSelectedConfirmBtn"),
          cancelLabel: t("editor.rejectSelectedCancelBtn"),
          danger: true,
        });
        if (ok) rejectSelected.mutate(pickedDirtyPaths);
      }}
      rejectSelectedPending={rejectSelected.isPending}
    />
  );

  return (
    <div className="flex h-[calc(100dvh-8rem)] flex-col gap-3 sm:h-[calc(100dvh-9rem)] sm:gap-4">
      {/* ── Header bar — three logical groups (back/drawer, title+status,
            actions). On mobile the title gets its own row so a long
            filename never gets shrunk to "book-w…" by sibling buttons.
            Desktop folds everything onto a single line. ──────────── */}
      <div className="flex flex-col gap-3 sm:flex-row sm:flex-wrap sm:items-center sm:gap-2.5">
        {/* Row 1 (mobile only): back + drawer toggle. Folds inline at sm+. */}
        <div className="flex shrink-0 items-center gap-2.5">
          <Link
            to="/books"
            className="btn-ghost shrink-0 px-2 py-1.5 sm:px-3 sm:py-2"
            aria-label={t("editor.backToLibrary")}
          >
            <ArrowLeft className="h-4 w-4" />
            <span className="hidden sm:inline">{t("editor.backToLibrary")}</span>
          </Link>

          <button
            type="button"
            onClick={() => setDrawerOpen(true)}
            className="btn-secondary shrink-0 px-2 py-1.5 md:hidden"
            aria-label={t("editor.pages", { n: pages.data?.length ?? 0 })}
          >
            <PanelLeft className="h-4 w-4" />
            <span className="text-xs">{pages.data?.length ?? 0}</span>
          </button>

          {/* Status pill stays grouped with the back/drawer cluster on
              mobile so the title is the only thing on its row. On
              desktop it ends up next to the title naturally. */}
          {status && <span className="shrink-0 sm:hidden"><BookStatusPill status={status} /></span>}
        </div>

        {/* Title — its own row on mobile (full width, may wrap). On
            desktop it shares the line and truncates if needed. Prefer the
            EPUB's dc:title (editable from the metadata modal); fall back to
            the original filename when the EPUB has no title or the row
            predates the metadata feature. */}
        <h1
          className="min-w-0 break-words text-base font-semibold leading-tight tracking-tight sm:flex-1 sm:truncate sm:text-lg"
          title={job.data.fileName}
        >
          {bookDisplayName(job.data)}
        </h1>

        {/* Status pill (desktop placement). Hidden on mobile — already
            shown next to the back arrow in row 1. */}
        {status && <span className="hidden sm:inline-flex"><BookStatusPill status={status} /></span>}

        {/* Actions row — wraps on mobile so the ring/shadow on each button
            isn't clipped by an `overflow-x-auto` scroll container (the
            previous layout cropped the top edge of bordered buttons).
            Folds onto the title row at sm+. */}
        <div className="flex shrink-0 flex-wrap items-center gap-2.5 sm:ml-auto sm:flex-nowrap">
          {status === "Running" && (
            <button className="btn-ghost shrink-0 px-2 py-1.5" onClick={() => pause.mutate()}
                    disabled={pause.isPending} title={t("bookDetail.pause")}>
              {pause.isPending ? <Loader2 className="h-4 w-4 animate-spin" /> : <Pause className="h-4 w-4" />}
            </button>
          )}
          {status === "Paused" && (
            <button className="btn-ghost shrink-0 px-2 py-1.5" onClick={() => resume.mutate()}
                    disabled={resume.isPending} title={t("bookDetail.resume")}>
              {resume.isPending ? <Loader2 className="h-4 w-4 animate-spin" /> : <Play className="h-4 w-4" />}
            </button>
          )}
          {/* Cancel — works for queued (drops it from the queue) and for
              running/paused (signals the worker's cancellation token so any
              in-flight LLM call interrupts). Confirmation dialog because
              there's no "undo" once requests are dropped. */}
          {isInflight && (
            <button
              className="btn-ghost shrink-0 px-2 py-1.5 text-rose-600 hover:bg-rose-50 dark:hover:bg-rose-500/10"
              onClick={async () => {
                const ok = await confirm({
                  title: t("bookDetail.cancelTitle"),
                  body: t("bookDetail.cancelConfirm"),
                  confirmLabel: t("bookDetail.cancelConfirmBtn"),
                  cancelLabel: t("bookDetail.cancelCancelBtn"),
                  danger: true,
                });
                if (ok) cancel.mutate();
              }}
              disabled={cancel.isPending}
              title={t("bookDetail.cancel")}
            >
              {cancel.isPending ? <Loader2 className="h-4 w-4 animate-spin" /> : <Square className="h-4 w-4" />}
            </button>
          )}

          {/* Single Download button — server lazy-finalizes from the editor's
              HEAD, so the user gets the current state of the book regardless
              of whether the AI has run. Hidden only while a run is actively
              in flight (Queued/Running/Paused) since the file would change
              under their feet. */}
          {!isInflight && (
            <a
              className="btn-secondary shrink-0 px-3 py-1.5 text-sm"
              href={downloadUrl(id)}
              title={t("bookDetail.download")}
            >
              <Download className="h-4 w-4" />
              <span className="hidden sm:inline">{t("bookDetail.download")}</span>
            </a>
          )}

          {!isInflight && job.data.dropConfigured && (
            <button
              className="btn-secondary shrink-0 px-3 py-1.5 text-sm"
              onClick={() => drop.mutate()}
              disabled={drop.isPending}
              title={t("bookDetail.dropHint")}
            >
              {drop.isPending
                ? <Loader2 className="h-4 w-4 animate-spin" />
                : <FolderInput className="h-4 w-4" />}
              <span className="hidden sm:inline">{t("bookDetail.copyToDrop")}</span>
            </button>
          )}

          <ActionMenu
            onReset={async () => {
              const ok = await confirm({
                title: t("editor.resetTitle"),
                body: t("editor.resetConfirm"),
                confirmLabel: t("editor.resetConfirmBtn"),
                cancelLabel: t("editor.resetCancelBtn"),
                danger: true,
              });
              if (ok) reset.mutate();
            }}
            resetPending={reset.isPending}
            aiEnabled={aiEnabled}
            onEditPrompt={() => setPromptOpen(true)}
            onEditMetadata={() => setMetadataOpen(true)}
          />
        </div>
      </div>

      {/* ── Progress bar ───────────────────────────────────────── */}
      {showProgress && (
        <div className="space-y-1">
          <div className="flex items-center justify-between text-[11px] text-stone-500 dark:text-stone-400">
            <span className="truncate">
              {t("bookDetail.progressOf", {
                done: done ?? 0,
                total,
                unit: t("bookDetail.unitPages"),
              })}
            </span>
            <span className="ml-2 shrink-0 tabular-nums">{progress ?? 0}%</span>
          </div>
          <div className="h-1 overflow-hidden rounded-full bg-stone-200 dark:bg-stone-700">
            <div className="h-full bg-stone-900 transition-all dark:bg-stone-100"
                 style={{ width: `${progress ?? 0}%` }} />
          </div>
        </div>
      )}

      {/* ── Main: tree | resize | editor ───────────────────────── */}
      <div ref={splitRef} className="flex min-h-0 flex-1 gap-0">
        {/* Desktop tree. flex-col on the wrapper so the inner card actually
            stretches to fill the column instead of collapsing to its
            content's intrinsic height (which left the page list dangling
            and the log panel "floating in the air" below it). */}
        <div
          className="hidden md:flex md:flex-col md:shrink-0"
          style={{ width: treeWidth, minWidth: TREE_WIDTH_MIN }}
        >
          {treeContent}
        </div>
        {/* Drag handle (desktop only). 16px hit zone with a 1px line
            visible at the centre — wide enough to grab without being a
            visual eyesore, and gives the layout breathing room between
            the two panes. */}
        <div
          onMouseDown={onResizeMouseDown}
          className="group hidden md:flex w-4 shrink-0 cursor-col-resize items-stretch"
          aria-label="resize"
          role="separator"
        >
          <div className="mx-auto h-full w-px bg-stone-200 transition-colors group-hover:bg-stone-400 dark:bg-stone-700 dark:group-hover:bg-stone-500" />
        </div>

        {/* Editor pane */}
        <div className="card flex min-h-0 min-w-0 flex-1 flex-col">
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
              bookId={id}
              path={page.data.path}
              status={page.data.status}
              diff={page.data.diff}
              draft={draft}
              onChange={setDraft}
              onDiscard={() => discard.mutate()}
              discarding={discard.isPending}
              onAcceptPage={() => acceptPage.mutate()}
              acceptPending={acceptPage.isPending}
              saving={save.isPending}
              dirty={draft !== lastSavedRef.current}
              partialUnmatched={partialByPage.get(page.data.path)}
              onOpenHistory={() => setHistoryOpen(true)}
              previewTick={previewTick}
              editorTab={editorTab}
              onEditorTabChange={setEditorTab}
              mobilePane={mobilePane}
              onMobilePaneChange={setMobilePane}
            />
          )}
        </div>
      </div>

      {job.data.error && (
        <div className="card border-rose-200 bg-rose-50 p-3 text-sm text-rose-700 dark:bg-rose-500/10 dark:text-rose-300">
          <div className="font-medium">{t("bookDetail.cleanupFailed")}</div>
          <div className="break-words">{job.data.error}</div>
        </div>
      )}

      {/* ── Logs panel (collapsible, anchored to bottom) ──────── */}
      <LogsPanel
        open={logsOpen}
        onToggle={() => setLogsOpen((o) => !o)}
        onClear={() => clearLogs.mutate()}
        lines={logs}
        canJumpTo={(doc) => docToPage.has(doc)}
        onJumpTo={jumpToDoc}
      />

      {/* ── Page history drawer (right side, all viewport sizes) ── */}
      {selected && (
        <HistoryDrawer
          open={historyOpen}
          onClose={() => setHistoryOpen(false)}
          bookId={id}
          path={selected}
        />
      )}

      {/* ── Per-book AI prompt editor ──────────────────────────── */}
      <BookPromptModal
        open={promptOpen}
        onClose={() => setPromptOpen(false)}
        bookId={id}
        initialValue={job.data.systemPrompt}
        onSaved={() => qc.invalidateQueries({ queryKey: ["book", id] })}
      />

      {/* ── EPUB metadata editor (title / author / language / …) ── */}
      <BookMetadataModal
        open={metadataOpen}
        onClose={() => setMetadataOpen(false)}
        bookId={id}
        initial={{
          title:       job.data.title,
          author:      job.data.author,
          language:    job.data.language,
          publisher:   job.data.publisher,
          description: job.data.description,
        }}
        onSaved={() => qc.invalidateQueries({ queryKey: ["book", id] })}
      />

      {/* ── Mobile drawer (file tree) ─────────────────────────── */}
      <Transition show={drawerOpen} as={Fragment}>
        <div className="fixed inset-0 z-40 md:hidden">
          <TransitionChild as={Fragment}
            enter="transition-opacity duration-150" enterFrom="opacity-0" enterTo="opacity-100"
            leave="transition-opacity duration-100" leaveFrom="opacity-100" leaveTo="opacity-0">
            <div className="absolute inset-0 bg-stone-950/40" onClick={() => setDrawerOpen(false)} />
          </TransitionChild>
          <TransitionChild as={Fragment}
            enter="transition-transform duration-200" enterFrom="-translate-x-full" enterTo="translate-x-0"
            leave="transition-transform duration-150" leaveFrom="translate-x-0" leaveTo="-translate-x-full">
            <aside className="absolute left-0 top-0 flex h-full w-[85vw] max-w-sm flex-col p-2">
              <div className="mb-2 flex shrink-0 items-center justify-end">
                <button
                  onClick={() => setDrawerOpen(false)}
                  className="rounded-md bg-paper p-2 text-stone-500 shadow ring-1 ring-stone-200 hover:bg-cream dark:bg-stone-800 dark:text-stone-300 dark:ring-stone-700 dark:hover:bg-stone-700"
                  aria-label="Close"
                >
                  <X className="h-4 w-4" />
                </button>
              </div>
              <div className="flex min-h-0 flex-1 flex-col">
                {treeContent}
              </div>
            </aside>
          </TransitionChild>
        </div>
      </Transition>
    </div>
  );
}

interface FileTreeProps {
  pages: PageEntry[];
  selected: string | null;
  picked: Set<string>;
  onSelect: (p: string) => void;
  onTogglePick: (p: string) => void;
  onPickAll: () => void;
  onPickNone: () => void;
  loading: boolean;
  /** Pages whose last AI pass produced unmatched items, keyed by path → unmatched count. */
  partialByPage: Map<string, number>;
  /** Pages whose last AI pass landed at least one low-confidence item, keyed
   *  by path → suspicious count. Drives the cyan tree dot. */
  suspiciousByPage: Map<string, number>;
  /** Admin master switch — hides the Run-AI button when off. */
  aiEnabled: boolean;
  onRunAi: () => void;
  runAiPending: boolean;
  runAiDisabled: boolean;
  onCommit: () => void;
  commitPending: boolean;
  dirtyCount: number;
  pickedDirtyCount: number;
  onAcceptSelected: () => void;
  acceptSelectedPending: boolean;
  onRejectSelected: () => void;
  rejectSelectedPending: boolean;
}

function FileTree({
  pages, selected, picked, onSelect, onTogglePick, onPickAll, onPickNone, loading,
  partialByPage, suspiciousByPage, aiEnabled,
  onRunAi, runAiPending, runAiDisabled, onCommit, commitPending, dirtyCount,
  pickedDirtyCount, onAcceptSelected, acceptSelectedPending,
  onRejectSelected, rejectSelectedPending,
}: FileTreeProps) {
  const { t } = useTranslation();
  const allChecked = pages.length > 0 && picked.size === pages.length;

  return (
    <div className="card flex min-h-0 flex-col">
      <div className="flex shrink-0 items-center justify-between gap-2 border-b border-stone-200 px-3 py-2 dark:border-stone-700">
        <span className="text-xs font-medium uppercase tracking-wide text-stone-500">
          {t("editor.pages", { n: pages.length })}
        </span>
        {pages.length > 0 && (
          <button
            type="button"
            onClick={() => (allChecked ? onPickNone() : onPickAll())}
            className="rounded px-1.5 py-0.5 text-[11px] font-medium text-stone-500 hover:bg-stone-100 hover:text-stone-900 dark:hover:bg-stone-700 dark:hover:text-stone-100"
          >
            {allChecked ? t("editor.selectNone") : t("editor.selectAll")}
          </button>
        )}
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
              const name = stripPagesPrefix(p.path);
              const isSel = p.path === selected;
              const isDirty = p.status === "Modified";
              const isPicked = picked.has(p.path);
              const partialUnmatched = partialByPage.get(p.path);
              const isPartial = partialUnmatched !== undefined;
              const suspiciousCount = suspiciousByPage.get(p.path);
              const isSuspicious = suspiciousCount !== undefined;
              return (
                <li key={p.path}>
                  <div
                    className={clsx(
                      "group flex w-full items-center gap-2 px-2.5 py-1.5 text-left text-xs transition-colors",
                      isSel
                        ? "bg-stone-100 dark:bg-stone-700/60"
                        : "hover:bg-stone-50 dark:hover:bg-stone-700/40",
                    )}
                  >
                    <input
                      type="checkbox"
                      checked={isPicked}
                      onChange={() => onTogglePick(p.path)}
                      onClick={(e) => e.stopPropagation()}
                      className="h-3.5 w-3.5 shrink-0 cursor-pointer accent-stone-700 dark:accent-stone-300"
                      aria-label={`select ${name}`}
                    />
                    {/* Suspicious wins over plain "modified" because it
                        carries strictly more information — the page is dirty
                        AND the LLM marked at least one item low-confidence,
                        which is what the user actually needs to triage. */}
                    <span
                      className={clsx(
                        "h-1.5 w-1.5 shrink-0 rounded-full",
                        isSuspicious ? "bg-cyan-500"
                        : isDirty    ? "bg-amber-500"
                        : "bg-transparent",
                      )}
                      aria-label={
                        isSuspicious ? t("editor.suspiciousBadge", { n: suspiciousCount })
                        : isDirty    ? "modified"
                        : "clean"
                      }
                    />
                    {isPartial && (
                      <AlertTriangle
                        className="h-3 w-3 shrink-0 text-amber-600 dark:text-amber-400"
                        aria-label={t("editor.partialBadge", { n: partialUnmatched })}
                      />
                    )}
                    <button
                      type="button"
                      onClick={() => onSelect(p.path)}
                      title={name}
                      className={clsx(
                        "min-w-0 flex-1 truncate text-left",
                        isSel ? "font-medium" : "",
                      )}
                    >
                      {name}
                    </button>
                  </div>
                </li>
              );
            })}
          </ul>
        )}
      </div>

      {/* Foot: primary actions tied to the file selection. Putting Run-AI
          here (not in the page header) ties it visually to the checkboxes
          that drive its filter — and keeps the header clean. */}
      <div className="shrink-0 space-y-1.5 border-t border-stone-200 p-2 dark:border-stone-700">
        {aiEnabled && (
          <button
            type="button"
            onClick={onRunAi}
            disabled={runAiDisabled}
            className="btn-primary w-full justify-start text-sm"
            title={t("editor.runAiHint")}
          >
            {runAiPending
              ? <Loader2 className="h-4 w-4 animate-spin" />
              : <Sparkles className="h-4 w-4" />}
            <span className="truncate">
              {picked.size === 0
                ? t("editor.runAiAll")
                : t("editor.runAiSelected", { n: picked.size })}
            </span>
          </button>
        )}

        {/* Batch accept / reject for the current selection. Only surfaces
            when the user has actually checked at least one page that has
            pending changes — keeps the footer quiet otherwise. */}
        {picked.size > 0 && (
          <div className="flex gap-1.5">
            <button
              type="button"
              onClick={onAcceptSelected}
              disabled={acceptSelectedPending || pickedDirtyCount === 0}
              className="inline-flex h-8 flex-1 items-center justify-center gap-1 rounded-md text-xs font-medium text-emerald-700 ring-1 ring-emerald-300 hover:bg-emerald-50 disabled:opacity-40 dark:text-emerald-300 dark:ring-emerald-500/40 dark:hover:bg-emerald-500/10"
              title={t("editor.acceptSelectedHint")}
            >
              {acceptSelectedPending
                ? <Loader2 className="h-3.5 w-3.5 animate-spin" />
                : <ThumbsUp className="h-3.5 w-3.5" />}
              {t("editor.acceptSelected", { n: pickedDirtyCount })}
            </button>
            <button
              type="button"
              onClick={onRejectSelected}
              disabled={rejectSelectedPending || pickedDirtyCount === 0}
              className="inline-flex h-8 flex-1 items-center justify-center gap-1 rounded-md text-xs font-medium text-rose-700 ring-1 ring-rose-300 hover:bg-rose-50 disabled:opacity-40 dark:text-rose-300 dark:ring-rose-500/40 dark:hover:bg-rose-500/10"
              title={t("editor.rejectSelectedHint")}
            >
              {rejectSelectedPending
                ? <Loader2 className="h-3.5 w-3.5 animate-spin" />
                : <ThumbsDown className="h-3.5 w-3.5" />}
              {t("editor.rejectSelected", { n: pickedDirtyCount })}
            </button>
          </div>
        )}

        <button
          type="button"
          onClick={onCommit}
          disabled={commitPending || dirtyCount === 0}
          className="btn-secondary w-full justify-start text-sm"
          title={t("editor.commitHint")}
        >
          {commitPending
            ? <Loader2 className="h-4 w-4 animate-spin" />
            : <Check className="h-4 w-4" />}
          <span className="truncate">
            {dirtyCount === 0
              ? t("editor.commit")
              : `${t("editor.commit")} (${dirtyCount})`}
          </span>
        </button>
      </div>
    </div>
  );
}

interface ActionMenuProps {
  onReset: () => void;
  resetPending: boolean;
  aiEnabled: boolean;
  onEditPrompt: () => void;
  onEditMetadata: () => void;
}

function ActionMenu({ onReset, resetPending, aiEnabled, onEditPrompt, onEditMetadata }: ActionMenuProps) {
  const { t } = useTranslation();
  const itemBase =
    "flex w-full items-center gap-2 rounded-md px-2.5 py-1.5 text-sm text-stone-700 dark:text-stone-200";
  return (
    <Menu as="div" className="relative shrink-0">
      <MenuButton className="btn-ghost px-2 py-1.5" aria-label="more">
        <MoreHorizontal className="h-4 w-4" />
      </MenuButton>
      <Transition as={Fragment}
        enter="transition ease-out duration-100" enterFrom="opacity-0 scale-95" enterTo="opacity-100 scale-100"
        leave="transition ease-in duration-75" leaveFrom="opacity-100 scale-100" leaveTo="opacity-0 scale-95">
        <MenuItems
          anchor={{ to: "bottom end", gap: 4, padding: 8 }}
          className="z-30 w-56 rounded-xl bg-paper p-1.5 shadow-lg ring-1 ring-stone-200 dark:bg-stone-800 dark:ring-stone-700"
        >
          <MenuItem>
            {({ focus }) => (
              <button
                onClick={onEditMetadata}
                className={clsx(itemBase, focus && "bg-stone-100 dark:bg-stone-700")}
                title={t("editor.metadataHint")}
              >
                <Info className="h-4 w-4" />
                {t("editor.metadata")}
              </button>
            )}
          </MenuItem>
          {aiEnabled && (
            <MenuItem>
              {({ focus }) => (
                <button
                  onClick={onEditPrompt}
                  className={clsx(itemBase, focus && "bg-stone-100 dark:bg-stone-700")}
                  title={t("editor.bookPromptHint")}
                >
                  <BookOpen className="h-4 w-4" />
                  {t("editor.bookPrompt")}
                </button>
              )}
            </MenuItem>
          )}
          <MenuItem>
            {({ focus }) => (
              <button
                onClick={onReset}
                disabled={resetPending}
                className={clsx(itemBase, focus && "bg-stone-100 dark:bg-stone-700")}
                title={t("editor.resetHint")}
              >
                {resetPending ? <Loader2 className="h-4 w-4 animate-spin" /> : <RefreshCcw className="h-4 w-4" />}
                {t("editor.reset")}
              </button>
            )}
          </MenuItem>
        </MenuItems>
      </Transition>
    </Menu>
  );
}

interface PageViewProps {
  bookId: string;
  path: string;
  status: string;
  diff: string;
  draft: string;
  onChange: (s: string) => void;
  onDiscard: () => void;
  discarding: boolean;
  onAcceptPage: () => void;
  acceptPending: boolean;
  saving: boolean;
  dirty: boolean;
  /** Items the LLM proposed but couldn't match verbatim on the most
   *  recent AI pass. Undefined when this chapter is fine. */
  partialUnmatched?: number;
  onOpenHistory: () => void;
  /** Bumped on every successful working-tree save — used as the preview
   *  iframe's cache key so it reloads when disk content changes. */
  previewTick: number;
  /** Diff/Preview tab choice, lifted to the parent so chapter switches
   *  (which unmount PageView during the loading state) don't reset it. */
  editorTab: "diff" | "preview";
  onEditorTabChange: (t: "diff" | "preview") => void;
  /** Mobile-only segmented control: which of the two stacked panes is on
   *  screen. Lifted for the same reason as editorTab. Ignored on lg+ where
   *  both panes render side-by-side. */
  mobilePane: "edit" | "view";
  onMobilePaneChange: (p: "edit" | "view") => void;
}

function PageView({
  bookId, path, status, diff, draft, onChange, onDiscard, discarding,
  onAcceptPage, acceptPending, saving, dirty, partialUnmatched, onOpenHistory,
  previewTick, editorTab, onEditorTabChange, mobilePane, onMobilePaneChange,
}: PageViewProps) {
  const { t } = useTranslation();
  const name = stripPagesPrefix(path);
  const isModified = status === "Modified";

  return (
    <>
      <div
        className={clsx(
          // Mobile: horizontal scroll so a long filename + action buttons
          // never wrap onto a second row that pushes the diff off-screen.
          // Desktop (sm+): wraps as before — there's enough room and a
          // hidden scrollbar would be jarring next to the resize handle.
          "flex shrink-0 items-center gap-2.5 overflow-x-auto whitespace-nowrap border-b border-stone-200 px-4 py-2.5 text-xs",
          "sm:flex-wrap sm:overflow-x-visible sm:whitespace-normal",
          "dark:border-stone-700",
        )}
      >
        <span
          className="min-w-0 flex-1 truncate font-mono text-stone-700 dark:text-stone-200"
          title={name}
        >
          {name}
        </span>
        {isModified && (
          <span className="shrink-0 rounded bg-amber-100 px-1.5 py-0.5 font-medium text-amber-800 dark:bg-amber-500/20 dark:text-amber-300">
            {t("editor.modified")}
          </span>
        )}
        <span className="flex shrink-0 items-center gap-2 text-stone-500">
          {saving && <Loader2 className="h-3 w-3 animate-spin" aria-label="saving" />}
          {!saving && dirty && <Save className="h-3 w-3" aria-label="unsaved" />}
          {!saving && !dirty && isModified && (
            <span className="hidden sm:inline">{t("editor.savedDirtyHint")}</span>
          )}
          {!saving && !dirty && !isModified && (
            <span className="hidden sm:inline">{t("editor.clean")}</span>
          )}
        </span>
        <button
          type="button"
          onClick={onOpenHistory}
          className="inline-flex h-7 shrink-0 items-center gap-1 rounded-md px-2 text-xs font-medium text-stone-600 hover:bg-cream hover:text-stone-900 dark:text-stone-300 dark:hover:bg-stone-700 dark:hover:text-stone-100"
          title={t("editor.historyHint")}
        >
          <History className="h-3 w-3" />
          <span className="hidden sm:inline">{t("editor.history")}</span>
        </button>
        {/* Page-level accept / reject. Only visible when the page diverges
            from HEAD — broad-strokes counterpart to the per-block buttons
            inside the diff. Ghost-with-accent so they're discoverable
            without dominating the toolbar. */}
        {isModified && (
          <>
            <button
              onClick={onAcceptPage}
              disabled={acceptPending}
              className="inline-flex h-7 shrink-0 items-center gap-1 rounded-md px-2 text-xs font-medium text-emerald-700 hover:bg-emerald-50 disabled:opacity-50 dark:text-emerald-300 dark:hover:bg-emerald-500/10"
              title={t("editor.acceptPageHint")}
            >
              {acceptPending ? <Loader2 className="h-3 w-3 animate-spin" /> : <ThumbsUp className="h-3 w-3" />}
              <span className="hidden sm:inline">{t("editor.acceptPage")}</span>
            </button>
            <button
              onClick={onDiscard}
              disabled={discarding}
              className="inline-flex h-7 shrink-0 items-center gap-1 rounded-md px-2 text-xs font-medium text-rose-700 hover:bg-rose-50 disabled:opacity-50 dark:text-rose-300 dark:hover:bg-rose-500/10"
              title={t("editor.discardHint")}
            >
              {discarding ? <Loader2 className="h-3 w-3 animate-spin" /> : <ThumbsDown className="h-3 w-3" />}
              <span className="hidden sm:inline">{t("editor.discard")}</span>
            </button>
          </>
        )}
      </div>

      {/* Partial-run banner — fires when the AI proposed removals on this
          chapter that didn't match the source verbatim. The user should
          eyeball the diff manually because some intended cleanups silently
          failed. */}
      {partialUnmatched !== undefined && (
        <div className="flex items-start gap-2 border-b border-amber-300/60 bg-amber-50 px-4 py-2 text-xs text-amber-800 dark:border-amber-500/30 dark:bg-amber-500/10 dark:text-amber-200">
          <AlertTriangle className="mt-0.5 h-3.5 w-3.5 shrink-0" />
          <span>{t("editor.partialBanner", { n: partialUnmatched })}</span>
        </div>
      )}

      {/* Mobile-only Edit/View segmented control. Phone screens can't fit
          both panes legibly stacked; instead we show one at a time and let
          the user toggle. Hidden on lg+ where both panes render side-by-
          side anyway. */}
      <div className="flex shrink-0 items-center gap-1 border-b border-stone-200 px-2 py-1.5 lg:hidden dark:border-stone-700">
        {(["edit", "view"] as const).map((p) => (
          <button
            key={p}
            type="button"
            onClick={() => onMobilePaneChange(p)}
            className={clsx(
              "flex-1 rounded px-2 py-1 text-xs font-medium transition-colors",
              mobilePane === p
                ? "bg-stone-100 text-stone-900 dark:bg-stone-700 dark:text-stone-100"
                : "text-stone-500 hover:text-stone-900 dark:text-stone-400 dark:hover:text-stone-100",
            )}
          >
            {t(p === "edit" ? "editor.mobilePaneEdit" : "editor.mobilePaneView")}
          </button>
        ))}
      </div>

      {/* Two panes: editable text on the left, diff or rendered preview on
          the right. On mobile only one is visible at a time (controlled by
          mobilePane); on lg+ they share the row. */}
      <div className="grid min-h-0 flex-1 grid-cols-1 grid-rows-1 gap-0 lg:grid-cols-2">
        <textarea
          value={draft}
          onChange={(e) => onChange(e.target.value)}
          spellCheck={false}
          wrap="soft"
          // Monospace + smaller line-height because the editor holds raw
          // chapter HTML — easier to scan tag boundaries with a fixed grid.
          // Use the Preview tab on the right for prose-rendered view.
          className={clsx(
            "min-h-0 w-full resize-none whitespace-pre-wrap break-words border-0 bg-transparent p-4 font-mono text-[13px] leading-6 outline-none lg:block",
            mobilePane === "edit" ? "block" : "hidden",
          )}
          placeholder={t("editor.emptyPlaceholder") ?? ""}
        />
        <RightPane
          bookId={bookId}
          path={path}
          diff={diff}
          draft={draft}
          previewTick={previewTick}
          tab={editorTab}
          onTabChange={onEditorTabChange}
          className={mobilePane === "view" ? "flex" : "hidden lg:flex"}
        />
      </div>
    </>
  );
}

/**
 * Right-side pane in the editor. Tab-switches between the diff view (raw
 * working-tree changes vs HEAD, with per-hunk accept/reject) and a rendered
 * HTML preview that mirrors what the EPUB exporter would write — useful for
 * verifying drop-caps, italics, and chapter structure survive the editor
 * pipeline.
 */
type RightTab = "diff" | "preview";

function RightPane({
  bookId, path, diff, draft, previewTick, tab, onTabChange, className,
}: {
  bookId: string;
  path: string;
  diff: string;
  draft: string;
  previewTick: number;
  tab: RightTab;
  onTabChange: (t: RightTab) => void;
  /** Display utility classes from the parent (used to gate visibility on
   *  mobile, where edit/view are toggled). The base `display` is set here
   *  via `className`; `flex-col`, sizing, and theme classes stay on the
   *  root below. */
  className?: string;
}) {
  const { t } = useTranslation();
  const { effective } = useTheme();
  const [previewSource, setPreviewSource] = useState<PreviewSource>("working");

  // Cache key bumps on every successful working-tree save and on theme
  // / source changes — iframe reloads only when something the render
  // depends on actually changed.
  const previewSrc = `${pagePreviewUrl(bookId, path, previewSource)}&v=${previewTick}&theme=${effective}`;

  return (
    // Right column sits on the same `bg-paper` / `dark:bg-stone-800` tone
    // as the left textarea so the two panes read as one surface.
    <div className={clsx(
      "min-h-0 flex-col border-t border-stone-200 bg-paper dark:border-stone-700 dark:bg-stone-800 lg:border-l lg:border-t-0",
      className ?? "flex",
    )}>
      <div className="flex shrink-0 items-center justify-between gap-2 border-b border-stone-200 px-2 py-1 dark:border-stone-700">
        <div className="flex items-center gap-1">
          {(["diff", "preview"] as const).map((k) => (
            <button
              key={k}
              type="button"
              onClick={() => onTabChange(k)}
              className={clsx(
                "rounded px-2 py-1 text-xs font-medium transition-colors",
                tab === k
                  ? "bg-stone-100 text-stone-900 dark:bg-stone-700 dark:text-stone-100"
                  : "text-stone-500 hover:text-stone-900 dark:text-stone-400 dark:hover:text-stone-100",
              )}
            >
              {t(k === "diff" ? "editor.viewDiff" : "editor.viewPreview")}
            </button>
          ))}
        </div>
        {/* Sub-toggle inside Preview: Current = working tree rendered with
            publisher CSS; Diff = synthetic side-by-side text diff with
            ins/del highlights (typography simplified, but unambiguous
            about what changed). */}
        {tab === "preview" && (
          <div className="flex items-center gap-1 text-[11px]">
            {(["working", "diff"] as const).map((s) => (
              <button
                key={s}
                type="button"
                onClick={() => setPreviewSource(s)}
                className={clsx(
                  "rounded px-1.5 py-0.5 font-medium transition-colors",
                  previewSource === s
                    ? "bg-stone-200 text-stone-900 dark:bg-stone-600 dark:text-stone-100"
                    : "text-stone-500 hover:text-stone-900 dark:text-stone-400 dark:hover:text-stone-100",
                )}
                title={t(s === "working" ? "editor.previewCurrentHint" : "editor.previewDiffHint")}
              >
                {t(s === "working" ? "editor.previewCurrent" : "editor.previewDiff")}
              </button>
            ))}
          </div>
        )}
      </div>
      {tab === "diff" ? (
        <DiffView
          bookId={bookId}
          path={path}
          diff={diff}
          content={draft}
        />
      ) : (
        <iframe
          key={previewSrc}
          src={previewSrc}
          title="Chapter preview"
          sandbox="allow-same-origin"
          className="min-h-0 flex-1 w-full bg-transparent"
        />
      )}
    </div>
  );
}

interface ParsedHunk { header: string; body: string[] }

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

type LineKind = "context" | "del" | "add";
interface CharSpan { kind: "same" | "diff"; text: string }
interface AnnotatedLine { kind: LineKind; text: string; spans?: CharSpan[] }

/**
 * Largest-common-subsequence between two strings, returned as paired
 * character ops on each side. O(m*n) time/space — fine for typical diff
 * lines (a few hundred chars). Caller bails out for pathologically long
 * lines so we don't burn the UI thread on a 50KB minified blob.
 */
function lcsChars(a: string, b: string): { aOps: CharSpan[]; bOps: CharSpan[] } {
  const m = a.length, n = b.length;
  // dp[i][j] = LCS length of a[..i] and b[..j]
  const dp: Uint32Array[] = Array.from({ length: m + 1 }, () => new Uint32Array(n + 1));
  for (let i = 1; i <= m; i++) {
    for (let j = 1; j <= n; j++) {
      dp[i][j] = a.charCodeAt(i - 1) === b.charCodeAt(j - 1)
        ? dp[i - 1][j - 1] + 1
        : Math.max(dp[i - 1][j], dp[i][j - 1]);
    }
  }
  const aRaw: { kind: "same" | "diff"; ch: string }[] = [];
  const bRaw: { kind: "same" | "diff"; ch: string }[] = [];
  let i = m, j = n;
  while (i > 0 || j > 0) {
    if (i > 0 && j > 0 && a.charCodeAt(i - 1) === b.charCodeAt(j - 1)) {
      aRaw.unshift({ kind: "same", ch: a[i - 1] });
      bRaw.unshift({ kind: "same", ch: b[j - 1] });
      i--; j--;
    } else if (j > 0 && (i === 0 || dp[i][j - 1] >= dp[i - 1][j])) {
      bRaw.unshift({ kind: "diff", ch: b[j - 1] });
      j--;
    } else {
      aRaw.unshift({ kind: "diff", ch: a[i - 1] });
      i--;
    }
  }
  return { aOps: compact(aRaw), bOps: compact(bRaw) };
}

/** Coalesce adjacent same-kind chars into one span — keeps the DOM small. */
function compact(raw: { kind: "same" | "diff"; ch: string }[]): CharSpan[] {
  const out: CharSpan[] = [];
  for (const c of raw) {
    const last = out[out.length - 1];
    if (last && last.kind === c.kind) last.text += c.ch;
    else out.push({ kind: c.kind, text: c.ch });
  }
  return out;
}

const MAX_LCS_LEN = 2000; // skip char-diff for lines longer than this

/**
 * Walk a hunk body, pairing consecutive {@code -} / {@code +} lines and
 * annotating them with character-level diff spans. Unpaired removed/added
 * lines (from a 2:3 change, etc.) and lines too long for LCS fall back to
 * whole-line highlighting.
 */
function annotateBody(body: string[]): AnnotatedLine[] {
  const out: AnnotatedLine[] = [];
  let i = 0;
  while (i < body.length) {
    const line = body[i];
    const c = line[0];
    if (c === " ") {
      out.push({ kind: "context", text: line.slice(1) });
      i++;
      continue;
    }
    const dels: string[] = [];
    const adds: string[] = [];
    while (i < body.length && body[i][0] === "-") { dels.push(body[i].slice(1)); i++; }
    while (i < body.length && body[i][0] === "+") { adds.push(body[i].slice(1)); i++; }

    const pairs = Math.min(dels.length, adds.length);
    for (let k = 0; k < pairs; k++) {
      const a = dels[k], b = adds[k];
      if (a.length <= MAX_LCS_LEN && b.length <= MAX_LCS_LEN) {
        const { aOps, bOps } = lcsChars(a, b);
        out.push({ kind: "del", text: a, spans: aOps });
        out.push({ kind: "add", text: b, spans: bOps });
      } else {
        out.push({ kind: "del", text: a });
        out.push({ kind: "add", text: b });
      }
    }
    for (let k = pairs; k < dels.length; k++) out.push({ kind: "del", text: dels[k] });
    for (let k = pairs; k < adds.length; k++) out.push({ kind: "add", text: adds[k] });
  }
  return out;
}

interface DiffViewProps {
  bookId: string;
  path: string;
  diff: string;
  /** Working-tree content for the page — interleaved with the diff hunks
   *  so the user sees the full prose, not just the changed slices. */
  content: string;
}

function DiffView({ bookId, path, diff, content }: DiffViewProps) {
  const { t } = useTranslation();
  const toast = useToast();
  const qc = useQueryClient();

  const hunks = useMemo(() => parseDiffHunks(diff), [diff]);
  const blocks = useMemo(() => buildMergedBlocks(content, hunks), [content, hunks]);

  // Awaited invalidate: keeps the mutation in `isPending` until the
  // page+pages queries have refetched, so the user never sees the
  // already-validated diff lingering on screen between commit-completion
  // and refresh.
  const reject = useMutation({
    mutationFn: (index: number) => rejectHunk(bookId, path, index),
    onSuccess: async () => {
      await Promise.all([
        qc.invalidateQueries({ queryKey: ["page", bookId, path] }),
        qc.invalidateQueries({ queryKey: ["pages", bookId] }),
      ]);
    },
    onError: (e) => toast.error(t("editor.rejectFailed"), e instanceof Error ? e.message : ""),
  });

  const accept = useMutation({
    mutationFn: (index: number) => acceptHunk(bookId, path, index),
    onSuccess: async () => {
      await Promise.all([
        qc.invalidateQueries({ queryKey: ["page", bookId, path] }),
        qc.invalidateQueries({ queryKey: ["pages", bookId] }),
      ]);
    },
    onError: (e) => toast.error(t("editor.acceptFailed"), e instanceof Error ? e.message : ""),
  });

  // Page is clean — render the full WT content read-only.
  if (hunks.length === 0) {
    return (
      <div className="min-h-0 flex-1 overflow-y-auto p-3">
        <pre className="whitespace-pre-wrap break-words font-mono text-xs leading-snug text-stone-700 dark:text-stone-300">
          {content || <span className="italic text-stone-500">{t("editor.noDiff")}</span>}
        </pre>
      </div>
    );
  }

  const busy = reject.isPending
    ? { idx: reject.variables, kind: "reject" as const }
    : accept.isPending
    ? { idx: accept.variables, kind: "accept" as const }
    : null;

  return (
    <div className="min-h-0 flex-1 overflow-y-auto p-3">
      <pre className="whitespace-pre-wrap break-words font-mono text-xs leading-snug">
        {blocks.map((block, i) => {
          if (block.kind === "context") {
            return (
              <span key={i} className="text-stone-700 dark:text-stone-300">
                {block.lines.map((ln, j) => (
                  <div key={j}>{ln || " "}</div>
                ))}
              </span>
            );
          }

          const isBusy = busy?.idx === block.hunkIndex;
          const annotated = annotatePair(block.dels, block.adds);
          return (
            <div key={i} className="my-1.5">
              <div className="flex items-center gap-2 pb-0.5 text-[11px]">
                <button
                  onClick={() => accept.mutate(block.hunkIndex)}
                  disabled={isBusy}
                  className="inline-flex h-5 items-center gap-1 rounded px-1.5 font-medium text-emerald-700 hover:bg-emerald-50 disabled:opacity-50 dark:text-emerald-300 dark:hover:bg-emerald-500/10"
                  title={t("editor.acceptHunkHint")}
                >
                  {isBusy && busy?.kind === "accept"
                    ? <Loader2 className="h-3 w-3 animate-spin" />
                    : <ThumbsUp className="h-3 w-3" />}
                  {t("editor.acceptHunk")}
                </button>
                <button
                  onClick={() => reject.mutate(block.hunkIndex)}
                  disabled={isBusy}
                  className="inline-flex h-5 items-center gap-1 rounded px-1.5 font-medium text-rose-700 hover:bg-rose-50 disabled:opacity-50 dark:text-rose-300 dark:hover:bg-rose-500/10"
                  title={t("editor.rejectHunkHint")}
                >
                  {isBusy && busy?.kind === "reject"
                    ? <Loader2 className="h-3 w-3 animate-spin" />
                    : <ThumbsDown className="h-3 w-3" />}
                  {t("editor.rejectHunk")}
                </button>
              </div>
              {annotated.map((row, j) => <DiffLine key={j} row={row} />)}
            </div>
          );
        })}
      </pre>
    </div>
  );
}

/** Block in the merged view — either a run of unchanged context lines or
 *  one change region tied to a hunk index. */
type MergedBlock =
  | { kind: "context"; lines: string[] }
  | { kind: "change"; hunkIndex: number; dels: string[]; adds: string[] };

interface ParsedHeader { newStart: number; newCount: number }

function parseHunkHeader(header: string): ParsedHeader {
  const m = /^@@ -\d+(?:,\d+)? \+(\d+)(?:,(\d+))? @@/.exec(header);
  if (!m) return { newStart: 0, newCount: 0 };
  return {
    newStart: parseInt(m[1], 10),
    newCount: m[2] ? parseInt(m[2], 10) : 1,
  };
}

/**
 * Walk the WT content top-to-bottom and weave the diff hunks back in: any
 * line that the diff doesn't touch is emitted verbatim, and at each hunk
 * position the {@code -} and {@code +} bodies are pulled in (along with
 * the hunk index so accept/reject can target it). Result is a single
 * stream of blocks the renderer can scan straight through.
 */
function buildMergedBlocks(content: string, hunks: ParsedHunk[]): MergedBlock[] {
  const lines = content.split("\n");
  const out: MergedBlock[] = [];
  let cursor = 0;

  // Sort by position in the new file so cursor advances monotonically.
  const ordered = hunks
    .map((h, idx) => ({ h, idx, info: parseHunkHeader(h.header) }))
    .sort((a, b) => a.info.newStart - b.info.newStart);

  function pushContext(from: number, to: number) {
    if (to <= from) return;
    out.push({ kind: "context", lines: lines.slice(from, to) });
  }

  for (const { h, idx, info } of ordered) {
    const newStart0 = Math.max(0, info.newStart - 1);
    pushContext(cursor, Math.min(newStart0, lines.length));

    const dels: string[] = [];
    const adds: string[] = [];
    for (const body of h.body) {
      if (body[0] === "-") dels.push(body.slice(1));
      else if (body[0] === "+") adds.push(body.slice(1));
    }
    out.push({ kind: "change", hunkIndex: idx, dels, adds });
    cursor = newStart0 + info.newCount;
  }
  pushContext(cursor, lines.length);
  return out;
}

/** Adapter: feeds annotateBody with synthetic body lines so the existing
 *  pairing + LCS logic works on raw del/add string arrays. */
function annotatePair(dels: string[], adds: string[]): AnnotatedLine[] {
  const synthetic: string[] = [
    ...dels.map((s) => "-" + s),
    ...adds.map((s) => "+" + s),
  ];
  return annotateBody(synthetic);
}

/**
 * One line of a diff hunk. The line itself stays on the regular background
 * — only the gutter (a colored left bar) carries the line-type signal, so
 * the eye reads the prose normally. The actual changed characters get a
 * strong inline highlight (and a strikethrough on removed text), which is
 * what the user came here to see.
 *
 * Lines without char-level spans (unpaired, or too long for LCS) fall back
 * to a whole-line tint so we still show <em>something</em>.
 */
function DiffLine({ row }: { row: AnnotatedLine }) {
  if (row.kind === "context") {
    return <div className="pl-3 text-stone-500">{row.text || " "}</div>;
  }

  // Gutter (left border) signals the line type without flooding the line
  // with colour. Saturated highlight is reserved for the changed chars.
  const gutter = row.kind === "del"
    ? "border-l-2 border-rose-400 dark:border-rose-500/80"
    : "border-l-2 border-emerald-400 dark:border-emerald-500/80";
  const diffCls = row.kind === "del"
    ? "bg-rose-200 text-rose-950 line-through decoration-rose-700/50 dark:bg-rose-500/40 dark:text-rose-50"
    : "bg-emerald-200 text-emerald-950 dark:bg-emerald-500/40 dark:text-emerald-50";

  if (!row.spans) {
    // Fallback for unpaired or pathologically-long lines: tint the whole
    // line so we still convey "this line changed" even without char-level.
    const fullCls = row.kind === "del"
      ? "bg-rose-100/60 text-rose-900 dark:bg-rose-500/15 dark:text-rose-200"
      : "bg-emerald-100/60 text-emerald-900 dark:bg-emerald-500/15 dark:text-emerald-200";
    return <div className={`${gutter} pl-2 ${fullCls}`}>{row.text || " "}</div>;
  }

  return (
    <div className={`${gutter} pl-2`}>
      {row.spans.length === 0
        ? " "
        : row.spans.map((s, k) =>
            s.kind === "same"
              ? <span key={k}>{s.text}</span>
              : <span key={k} className={diffCls}>{s.text}</span>,
          )}
    </div>
  );
}

interface LogsPanelProps {
  open: boolean;
  onToggle: () => void;
  onClear: () => void;
  lines: LogLine[];
  canJumpTo: (docName: string) => boolean;
  onJumpTo: (docName: string) => void;
}

function LogsPanel({ open, onToggle, onClear, lines, canJumpTo, onJumpTo }: LogsPanelProps) {
  const { t } = useTranslation();
  const scrollRef = useRef<HTMLDivElement>(null);
  const stickyRef = useRef(true);

  function onScroll() {
    const el = scrollRef.current;
    if (!el) return;
    stickyRef.current = el.scrollHeight - el.scrollTop - el.clientHeight < 32;
  }
  useLayoutEffect(() => {
    const el = scrollRef.current;
    if (el && stickyRef.current) el.scrollTop = el.scrollHeight;
  }, [lines.length, open]);

  return (
    <div className="card overflow-hidden">
      <div className="flex items-center justify-between gap-2 px-3 py-2 text-xs font-medium text-stone-700 dark:text-stone-200">
        <button
          type="button"
          onClick={onToggle}
          className="flex flex-1 items-center gap-2 text-left"
        >
          {open ? <ChevronDown className="h-3 w-3" /> : <ChevronRight className="h-3 w-3" />}
          {t("editor.logs")} ({lines.length})
        </button>
        {lines.length > 0 && (
          <button
            type="button"
            onClick={(e) => { e.stopPropagation(); onClear(); }}
            className="inline-flex h-6 items-center gap-1 rounded px-1.5 text-[11px] font-medium text-stone-500 hover:bg-cream hover:text-stone-900 dark:text-stone-400 dark:hover:bg-stone-700 dark:hover:text-stone-100"
            title={t("editor.logsClearHint")}
          >
            <Trash2 className="h-3 w-3" />
            {t("editor.logsClear")}
          </button>
        )}
      </div>
      {open && (
        <div ref={scrollRef} onScroll={onScroll}
             className="max-h-48 overflow-y-auto border-t border-stone-200 p-2 sm:max-h-64 sm:p-3 dark:border-stone-700">
          {lines.length === 0
            ? <div className="px-2 py-6 text-center text-xs text-stone-500">{t("editor.noLogs")}</div>
            : renderLogs(lines, canJumpTo, onJumpTo)}
        </div>
      )}
    </div>
  );
}

function renderLogs(
  lines: LogLine[],
  canJumpTo: (docName: string) => boolean,
  onJumpTo: (docName: string) => void,
): JSX.Element[] {
  type Item =
    | { kind: "line"; line: LogLine; key: number }
    | { kind: "group"; id: string; lines: LogLine[]; key: number };

  const items: Item[] = [];
  const indexById = new Map<string, number>();

  lines.forEach((line, i) => {
    if (!line.groupId) {
      items.push({ kind: "line", line, key: i });
      return;
    }
    const existing = indexById.get(line.groupId);
    if (existing != null) {
      const g = items[existing] as Extract<Item, { kind: "group" }>;
      g.lines.push(line);
    } else {
      indexById.set(line.groupId, items.length);
      items.push({ kind: "group", id: line.groupId, lines: [line], key: i });
    }
  });

  return items.map((it) => {
    if (it.kind === "line") {
      return <LogRow key={it.key} line={it.line} canJumpTo={canJumpTo} onJumpTo={onJumpTo} />;
    }
    if (it.lines.length === 1) {
      return <LogRow key={it.key} line={it.lines[0]} canJumpTo={canJumpTo} onJumpTo={onJumpTo} />;
    }
    return (
      <LogGroup
        key={it.key}
        groupId={it.id}
        lines={it.lines}
        canJumpTo={canJumpTo}
        onJumpTo={onJumpTo}
      />
    );
  });
}

interface LogGroupProps {
  groupId: string;
  lines: LogLine[];
  canJumpTo: (docName: string) => boolean;
  onJumpTo: (docName: string) => void;
}

function LogGroup({ groupId, lines, canJumpTo, onJumpTo }: LogGroupProps) {
  const final = lines[lines.length - 1];
  const finalLevel = final.level;
  const stripeClass =
    finalLevel === "removed"    ? "border-fuchsia-400 dark:border-fuchsia-500/60"
    : finalLevel === "partial"  ? "border-amber-500   dark:border-amber-500/70"
    : finalLevel === "suspicious" ? "border-cyan-400 dark:border-cyan-500/60"
    : finalLevel === "clean"    ? "border-emerald-400 dark:border-emerald-500/60"
    : finalLevel === "warn"     ? "border-amber-400   dark:border-amber-500/60"
    : finalLevel === "error"    ? "border-rose-400    dark:border-rose-500/60"
    : "border-stone-300 dark:border-stone-600";

  const jumpable = canJumpTo(groupId);
  return (
    <div className={`my-2 ml-1 border-l-2 pl-2 ${stripeClass}`}>
      {jumpable ? (
        <button
          type="button"
          onClick={() => onJumpTo(groupId)}
          title={groupId}
          className="block max-w-full truncate px-2 py-0.5 text-left text-[10px] font-semibold uppercase tracking-wider text-stone-600 hover:text-stone-900 hover:underline dark:text-stone-300 dark:hover:text-stone-100"
        >
          {groupId}
        </button>
      ) : (
        <div className="px-2 py-0.5 text-[10px] font-semibold uppercase tracking-wider text-stone-500 dark:text-stone-400">
          {groupId}
        </div>
      )}
      {lines.map((l, i) => (
        <LogRow key={i} line={l} canJumpTo={canJumpTo} onJumpTo={onJumpTo} />
      ))}
    </div>
  );
}

interface LogRowProps {
  line: LogLine;
  canJumpTo: (docName: string) => boolean;
  onJumpTo: (docName: string) => void;
}

function LogRow({ line, canJumpTo, onJumpTo }: LogRowProps) {
  const { t } = useTranslation();
  const [open, setOpen] = useState(false);
  const hasDetail = !!line.detail && line.detail.trim().length > 0;

  // The worker prefixes most lines with `{docName}: …` and also stamps the
  // groupId — split so the doc name renders as a tappable chip the user
  // can click to jump straight to that page in the editor.
  const docMatch = line.groupId && canJumpTo(line.groupId)
    ? splitLeadingDoc(line.message, line.groupId)
    : null;

  return (
    <div>
      <div className="log-line" data-level={line.level}>
        <span className="ts">{timeOnly(line.ts)}</span>
        <span className="lvl">{line.level}</span>
        <span className="msg">
          {docMatch ? (
            <>
              <button
                type="button"
                onClick={() => onJumpTo(line.groupId!)}
                title={line.groupId ?? ""}
                className="mr-1 inline-flex max-w-[16rem] items-center truncate rounded bg-stone-100 px-1 py-0 align-baseline font-mono text-[11px] text-stone-700 hover:bg-stone-200 hover:text-stone-900 hover:underline dark:bg-stone-700 dark:text-stone-200 dark:hover:bg-stone-600"
              >
                {docShortName(line.groupId!)}
              </button>
              {docMatch.rest}
            </>
          ) : (
            line.message
          )}
          {hasDetail && (
            <button
              type="button"
              onClick={() => setOpen((o) => !o)}
              className="ml-2 inline-flex items-center gap-0.5 rounded px-1 py-0 text-[10px] font-semibold uppercase tracking-wide text-stone-400 hover:bg-stone-100 hover:text-stone-700 dark:hover:bg-stone-700 dark:hover:text-stone-200"
            >
              {open ? <ChevronDown className="h-3 w-3" /> : <ChevronRight className="h-3 w-3" />}
              {open ? t("bookDetail.hideRaw") : t("bookDetail.showRaw")}
            </button>
          )}
        </span>
      </div>
      {open && hasDetail && (
        <pre className="my-1 ml-[164px] mr-2 max-h-72 overflow-auto whitespace-pre-wrap rounded-md bg-stone-100 p-2 font-mono text-[11.5px] leading-relaxed text-stone-700 ring-1 ring-stone-200 dark:bg-stone-700 dark:text-stone-200 dark:ring-stone-600">
          {line.detail}
        </pre>
      )}
    </div>
  );
}

/**
 * If a log message starts with "{docName}: …", split off that prefix so
 * the doc name can be rendered as a clickable chip and the rest of the
 * message follows normally. Returns null when the prefix isn't present —
 * we fall back to rendering the message verbatim.
 */
function splitLeadingDoc(msg: string, doc: string): { rest: string } | null {
  // Worker emits "{doc}: …", "{doc} – …", "{doc}: attempt 1/6 …", etc.
  if (msg.startsWith(doc)) {
    let i = doc.length;
    while (i < msg.length && (msg[i] === ":" || msg[i] === " " || msg[i] === "—" || msg[i] === "-")) i++;
    return { rest: msg.slice(i) };
  }
  return null;
}

/** Display shorter form of a doc name for the chip ("OPS/Text/ch01.xhtml" → "ch01.xhtml"). */
function docShortName(doc: string): string {
  const slash = doc.lastIndexOf("/");
  return slash >= 0 ? doc.slice(slash + 1) : doc;
}

/** "pages/0042_chapter_05.txt" → "0042_chapter_05.txt" */
function stripPagesPrefix(p: string) {
  return p.replace(/^pages\//, "");
}

interface BookPromptModalProps {
  open: boolean;
  onClose: () => void;
  bookId: string;
  initialValue: string;
  onSaved: () => void;
}

/**
 * Lightweight inline modal for editing this book's AI instructions.
 * Sits in the prompt stack as: admin → user → book. Anything typed here
 * is appended last so it can override the broader directives at the LLM
 * call site (which reads instructions in order).
 */
function BookPromptModal({ open, onClose, bookId, initialValue, onSaved }: BookPromptModalProps) {
  const { t } = useTranslation();
  const toast = useToast();
  const [value, setValue] = useState(initialValue);

  // Sync when the modal opens against a different book / fresh data.
  useEffect(() => { if (open) setValue(initialValue); }, [open, initialValue]);

  const save = useMutation({
    mutationFn: () => saveBookPrompt(bookId, value),
    onSuccess: () => {
      toast.success(t("editor.bookPromptSaved"));
      onSaved();
      onClose();
    },
    onError: (e) => toast.error(t("editor.bookPromptFailed"), e instanceof Error ? e.message : ""),
  });

  if (!open) return null;
  return (
    <div className="fixed inset-0 z-50 grid place-items-center bg-stone-950/40 p-4" onClick={onClose}>
      <div
        className="w-full max-w-lg rounded-xl bg-paper p-5 shadow-xl ring-1 ring-stone-200 dark:bg-stone-800 dark:ring-stone-700"
        onClick={(e) => e.stopPropagation()}
      >
        <div className="mb-2 flex items-start justify-between gap-3">
          <div>
            <div className="text-sm font-semibold text-stone-900 dark:text-stone-100">
              {t("editor.bookPrompt")}
            </div>
            <p className="mt-1 text-xs text-stone-500 dark:text-stone-400">
              {t("editor.bookPromptHint")}
            </p>
          </div>
          <button
            onClick={onClose}
            className="rounded-md p-1.5 text-stone-500 hover:bg-cream dark:text-stone-300 dark:hover:bg-stone-700"
            aria-label="Close"
          >
            <X className="h-4 w-4" />
          </button>
        </div>
        <textarea
          className="input mt-2 font-mono text-xs"
          rows={6}
          placeholder={t("editor.bookPromptPlaceholder")}
          value={value}
          onChange={(e) => setValue(e.target.value)}
        />
        <div className="mt-3 flex justify-end gap-2">
          <button onClick={onClose} className="btn-secondary">{t("editor.bookPromptCancel")}</button>
          <button
            onClick={() => save.mutate()}
            disabled={save.isPending}
            className="btn-primary"
          >
            {save.isPending ? <Loader2 className="h-4 w-4 animate-spin" /> : <Save className="h-4 w-4" />}
            {t("editor.bookPromptSave")}
          </button>
        </div>
      </div>
    </div>
  );
}

interface BookMetadataModalProps {
  open: boolean;
  onClose: () => void;
  bookId: string;
  initial: BookMetadata;
  onSaved: () => void;
}

/**
 * Edits the EPUB metadata (title / author / language / publisher / description).
 * Persists to the DB (drives the displayed title everywhere) AND rewrites the
 * OPF inside the input EPUB so the next download / push-to-folder ships with
 * the updated metadata. Empty fields land as null and remove the corresponding
 * OPF element.
 */
function BookMetadataModal({ open, onClose, bookId, initial, onSaved }: BookMetadataModalProps) {
  const { t } = useTranslation();
  const toast = useToast();
  const [draft, setDraft] = useState<BookMetadata>(initial);

  // Reset only on the closed→open transition — `initial` is a freshly
  // constructed object on every parent render (the editor's job query
  // refetches every 4s), so a naive `[open, initial]` dep would clobber
  // whatever the user is typing each time the parent re-renders.
  const wasOpen = useRef(false);
  useEffect(() => {
    if (open && !wasOpen.current) setDraft(initial);
    wasOpen.current = open;
  }, [open, initial]);

  const save = useMutation({
    mutationFn: () => saveBookMetadata(bookId, draft),
    onSuccess: () => {
      toast.success(t("editor.metadataSaved"));
      onSaved();
      onClose();
    },
    onError: (e) => toast.error(t("editor.metadataFailed"), e instanceof Error ? e.message : ""),
  });

  if (!open) return null;
  const setField = (key: keyof BookMetadata) =>
    (e: React.ChangeEvent<HTMLInputElement | HTMLTextAreaElement>) =>
      setDraft((d) => ({ ...d, [key]: e.target.value }));

  return (
    <div className="fixed inset-0 z-50 grid place-items-center bg-stone-950/40 p-4" onClick={onClose}>
      <div
        className="w-full max-w-lg rounded-xl bg-paper p-5 shadow-xl ring-1 ring-stone-200 dark:bg-stone-800 dark:ring-stone-700"
        onClick={(e) => e.stopPropagation()}
      >
        <div className="mb-4 flex items-start justify-between gap-3">
          <div>
            <div className="text-sm font-semibold text-stone-900 dark:text-stone-100">
              {t("editor.metadata")}
            </div>
            <p className="mt-1 text-xs text-stone-500 dark:text-stone-400">
              {t("editor.metadataHint")}
            </p>
          </div>
          <button
            onClick={onClose}
            className="rounded-md p-1.5 text-stone-500 hover:bg-cream dark:text-stone-300 dark:hover:bg-stone-700"
            aria-label="Close"
          >
            <X className="h-4 w-4" />
          </button>
        </div>

        <div className="space-y-3">
          <div>
            <label className="label">{t("editor.metadataTitle")}</label>
            <input className="input" value={draft.title ?? ""} onChange={setField("title")} />
          </div>
          <div className="grid grid-cols-1 gap-3 sm:grid-cols-2">
            <div>
              <label className="label">{t("editor.metadataAuthor")}</label>
              <input className="input" value={draft.author ?? ""} onChange={setField("author")} />
            </div>
            <div>
              <label className="label">{t("editor.metadataLanguage")}</label>
              <input
                className="input"
                value={draft.language ?? ""}
                onChange={setField("language")}
                placeholder="en"
              />
            </div>
          </div>
          <div>
            <label className="label">{t("editor.metadataPublisher")}</label>
            <input className="input" value={draft.publisher ?? ""} onChange={setField("publisher")} />
          </div>
          <div>
            <label className="label">{t("editor.metadataDescription")}</label>
            <textarea
              className="input"
              rows={4}
              value={draft.description ?? ""}
              onChange={setField("description")}
            />
          </div>
        </div>

        <div className="mt-4 flex justify-end gap-2">
          <button onClick={onClose} className="btn-secondary">{t("editor.metadataCancel")}</button>
          <button
            onClick={() => save.mutate()}
            disabled={save.isPending}
            className="btn-primary"
          >
            {save.isPending ? <Loader2 className="h-4 w-4 animate-spin" /> : <Save className="h-4 w-4" />}
            {t("editor.metadataSave")}
          </button>
        </div>
      </div>
    </div>
  );
}

interface HistoryDrawerProps {
  open: boolean;
  onClose: () => void;
  bookId: string;
  path: string;
}

/**
 * Right-side slide-out panel listing the page's git history. Each entry
 * shows the commit's short SHA, message, and date; clicking "Restore"
 * drops that revision's content into the working tree as a pending diff
 * the user can then accept/reject like any other change.
 */
function HistoryDrawer({ open, onClose, bookId, path }: HistoryDrawerProps) {
  const { t } = useTranslation();
  const toast = useToast();
  const qc = useQueryClient();
  const confirm = useConfirm();

  const history = useQuery({
    queryKey: ["page-history", bookId, path],
    queryFn: () => listPageHistory(bookId, path),
    // Only fire when the drawer is actually open.
    enabled: open && !!bookId && !!path,
    refetchInterval: open ? 8000 : false,
  });

  const restore = useMutation({
    mutationFn: (sha: string) => restorePageToCommit(bookId, path, sha),
    onSuccess: async () => {
      toast.success(t("editor.historyRestored"));
      await Promise.all([
        qc.invalidateQueries({ queryKey: ["page", bookId, path] }),
        qc.invalidateQueries({ queryKey: ["pages", bookId] }),
      ]);
      onClose();
    },
    onError: (e) => toast.error(t("editor.historyRestoreFailed"), e instanceof Error ? e.message : ""),
  });

  return (
    <Transition show={open} as={Fragment}>
      <div className="fixed inset-0 z-40">
        <TransitionChild as={Fragment}
          enter="transition-opacity duration-150" enterFrom="opacity-0" enterTo="opacity-100"
          leave="transition-opacity duration-100" leaveFrom="opacity-100" leaveTo="opacity-0">
          <div className="absolute inset-0 bg-stone-950/40" onClick={onClose} />
        </TransitionChild>
        <TransitionChild as={Fragment}
          enter="transition-transform duration-200" enterFrom="translate-x-full" enterTo="translate-x-0"
          leave="transition-transform duration-150" leaveFrom="translate-x-0" leaveTo="translate-x-full">
          <aside className="absolute right-0 top-0 flex h-full w-[90vw] max-w-md flex-col bg-paper shadow-xl dark:bg-stone-800">
            <div className="flex shrink-0 items-center justify-between border-b border-stone-200 px-4 py-3 dark:border-stone-700">
              <div className="min-w-0">
                <div className="text-sm font-semibold text-stone-900 dark:text-stone-100">
                  {t("editor.historyTitle")}
                </div>
                <div className="truncate text-[11px] font-mono text-stone-500 dark:text-stone-400" title={path}>
                  {stripPagesPrefix(path)}
                </div>
              </div>
              <button
                onClick={onClose}
                className="rounded-md p-1.5 text-stone-500 hover:bg-cream dark:text-stone-300 dark:hover:bg-stone-700"
                aria-label="Close"
              >
                <X className="h-4 w-4" />
              </button>
            </div>
            <div className="min-h-0 flex-1 overflow-y-auto p-2">
              {history.isLoading ? (
                <div className="grid place-items-center py-10">
                  <Loader2 className="h-4 w-4 animate-spin text-stone-400" />
                </div>
              ) : (history.data ?? []).length === 0 ? (
                <div className="px-3 py-10 text-center text-xs text-stone-500">
                  {t("editor.historyEmpty")}
                </div>
              ) : (
                <ul className="space-y-1.5">
                  {(history.data ?? []).map((rev) => (
                    <HistoryEntry
                      key={rev.sha}
                      bookId={bookId}
                      path={path}
                      rev={rev}
                      restoring={restore.isPending && restore.variables === rev.sha}
                      onRestore={async () => {
                        const ok = await confirm({
                          title: t("editor.historyRestoreTitle"),
                          body: t("editor.historyRestoreConfirm"),
                          confirmLabel: t("editor.historyRestoreConfirmBtn"),
                          cancelLabel: t("editor.historyRestoreCancelBtn"),
                        });
                        if (ok) restore.mutate(rev.sha);
                      }}
                    />
                  ))}
                </ul>
              )}
            </div>
          </aside>
        </TransitionChild>
      </div>
    </Transition>
  );
}

interface HistoryEntryProps {
  bookId: string;
  path: string;
  rev: PageRevision;
  restoring: boolean;
  onRestore: () => void;
}

function HistoryEntry({ bookId, path, rev, restoring, onRestore }: HistoryEntryProps) {
  const { t } = useTranslation();
  const [open, setOpen] = useState(false);

  // Lazy: only fetch the page-at-commit content when the user expands
  // the preview, so a long history doesn't fan out 50 GETs at once.
  const preview = useQuery({
    queryKey: ["page-at", bookId, path, rev.sha],
    queryFn: () => getPageAtCommit(bookId, path, rev.sha),
    enabled: open,
    staleTime: 60_000,
  });

  return (
    <li className="overflow-hidden rounded-md border border-stone-200/70 dark:border-stone-700">
      <div className="flex items-start gap-2 px-3 py-2">
        {/* SHA is the visual anchor — every commit message tends to look
            the same (e.g. "User edits"), so the hex chunk is what makes
            entries distinguishable at a glance. */}
        <span className="shrink-0 rounded bg-stone-100 px-1.5 py-0.5 font-mono text-xs font-semibold text-violet-700 dark:bg-stone-700 dark:text-violet-300">
          {rev.shortSha}
        </span>
        <div className="min-w-0 flex-1">
          <div className="line-clamp-2 text-xs text-stone-700 dark:text-stone-200">
            {rev.message || <span className="italic text-stone-400">(no message)</span>}
          </div>
          <div className="mt-1 text-[10px] text-stone-500 dark:text-stone-400">
            {new Date(rev.timestamp).toLocaleString()} · {rev.author}
          </div>
        </div>
        <button
          type="button"
          onClick={() => setOpen((o) => !o)}
          className="inline-flex h-6 shrink-0 items-center gap-1 rounded px-1.5 text-[11px] font-medium text-stone-700 ring-1 ring-stone-300 hover:bg-cream dark:text-stone-200 dark:ring-stone-600 dark:hover:bg-stone-700"
          title={t("editor.historyPreviewHint")}
        >
          {open ? <ChevronDown className="h-3 w-3" /> : <ChevronRight className="h-3 w-3" />}
          {t("editor.historyPreview")}
        </button>
      </div>
      {open && (
        <div className="border-t border-stone-200/70 dark:border-stone-700">
          <pre className="max-h-48 overflow-auto whitespace-pre-wrap break-words bg-stone-50 p-3 font-serif text-[12.5px] leading-relaxed text-stone-700 dark:bg-stone-900/40 dark:text-stone-300">
            {preview.isLoading
              ? t("app.loading")
              : preview.data?.content || <span className="italic text-stone-400">(empty)</span>}
          </pre>
          <div className="flex justify-end px-3 py-2">
            <button
              type="button"
              onClick={onRestore}
              disabled={restoring || preview.isLoading}
              className="inline-flex h-7 items-center gap-1 rounded px-2 text-xs font-medium text-stone-700 ring-1 ring-stone-300 hover:bg-cream disabled:opacity-50 dark:text-stone-200 dark:ring-stone-600 dark:hover:bg-stone-700"
              title={t("editor.historyRestoreHint")}
            >
              {restoring ? <Loader2 className="h-3 w-3 animate-spin" /> : <RefreshCcw className="h-3 w-3" />}
              {t("editor.historyRestore")}
            </button>
          </div>
        </div>
      )}
    </li>
  );
}
