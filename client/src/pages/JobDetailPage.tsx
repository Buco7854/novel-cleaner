import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { HubConnectionState } from "@microsoft/signalr";
import { ArrowLeft, Check, ChevronDown, ChevronRight, Download, FileText, FolderInput, Loader2, Pause, Play, RotateCcw } from "lucide-react";
import { useEffect, useLayoutEffect, useRef, useState } from "react";
import { useTranslation } from "react-i18next";
import { Link, useNavigate, useParams } from "react-router-dom";
import { copyToDrop, downloadUrl, getJob, JobStatus, pauseJob, resumeJob } from "../api/jobs";
import { createJobHub, JobLogEvent } from "../api/jobsHub";
import { finalizeJob, reprocessJob } from "../api/reviews";
import { JobStatusPill } from "../components/JobStatusPill";
import { ReviewPanel } from "../components/ReviewPanel";
import { useToast } from "../contexts/ToastContext";
import { format, timeOnly } from "../utils/date";

interface LineProps { ts: string; level: string; message: string; detail?: string | null; groupId?: string | null }

export function JobDetailPage() {
  const { t } = useTranslation();
  const toast = useToast();
  const { id = "" } = useParams();
  const [logs, setLogs] = useState<LineProps[]>([]);
  const [status, setStatus] = useState<JobStatus | null>(null);
  const [progress, setProgress] = useState<number | null>(null);
  const [done, setDone] = useState<number | null>(null);
  const [total, setTotal] = useState<number | null>(null);
  const scrollRef = useRef<HTMLDivElement>(null);
  const stickyRef = useRef(true);

  const job = useQuery({ queryKey: ["job", id], queryFn: () => getJob(id), enabled: !!id });

  const qc = useQueryClient();

  const drop = useMutation({
    mutationFn: () => copyToDrop(id),
    onSuccess: (r) => toast.success(t("jobDetail.dropSuccess"), r.destination),
    onError: (e) => toast.error(t("jobDetail.dropFailed"), e instanceof Error ? e.message : ""),
  });

  const pause = useMutation({
    mutationFn: () => pauseJob(id),
    onSuccess: () => { setStatus("Paused"); qc.invalidateQueries({ queryKey: ["jobs"] }); },
    onError: (e) => toast.error(t("jobDetail.pauseFailed"), e instanceof Error ? e.message : ""),
  });

  const resume = useMutation({
    mutationFn: () => resumeJob(id),
    onSuccess: () => { setStatus("Running"); qc.invalidateQueries({ queryKey: ["jobs"] }); },
    onError: (e) => toast.error(t("jobDetail.resumeFailed"), e instanceof Error ? e.message : ""),
  });

  const finalize = useMutation({
    mutationFn: () => finalizeJob(id),
    onSuccess: () => {
      toast.success(t("review.finalizeQueued"));
      qc.invalidateQueries({ queryKey: ["job", id] });
      qc.invalidateQueries({ queryKey: ["jobs"] });
    },
    onError: (e) => toast.error(t("review.actionFailed"), e instanceof Error ? e.message : ""),
  });

  const nav = useNavigate();
  const reprocess = useMutation({
    mutationFn: () => reprocessJob(id),
    onSuccess: ({ id: newId }) => {
      toast.success(t("review.reprocessQueued"));
      nav(`/jobs/${newId}`);
    },
    onError: (e) => toast.error(t("review.actionFailed"), e instanceof Error ? e.message : ""),
  });

  useEffect(() => {
    if (job.data) {
      setLogs(job.data.logs.map((l) => ({
        ts: l.timestamp, level: l.level, message: l.message,
        detail: l.detail ?? null, groupId: l.groupId ?? null,
      })));
      setStatus(job.data.status);
    }
  }, [job.data]);

  useEffect(() => {
    if (!id) return;
    const hub = createJobHub();
    let mounted = true;

    const offLog = hub.onLog((e: JobLogEvent) => {
      if (e.jobId !== id) return;
      setLogs((cur) => [...cur, {
        ts: e.timestamp, level: e.level, message: e.message,
        detail: e.detail ?? null, groupId: e.groupId ?? null,
      }]);
    });
    const offStatus = hub.onStatus((e) => {
      if (e.jobId !== id) return;
      const next = e.status as JobStatus;
      setStatus(next);
      if (typeof e.progress === "number") setProgress(e.progress);
      if (typeof e.done === "number") setDone(e.done);
      if (typeof e.total === "number") setTotal(e.total);

      // Refetch the job detail when entering a terminal state so the UI
      // picks up the freshly-computed flags (hasOutput, canDrop) without a
      // page reload — this is what makes the Download / Drop folder buttons
      // appear automatically when the job completes.
      if (next === "Completed" || next === "Failed" || next === "Canceled") {
        qc.invalidateQueries({ queryKey: ["job", id] });
        qc.invalidateQueries({ queryKey: ["jobs"] });
      }
    });

    async function ensureLiveAndRefresh() {
      if (!mounted) return;
      try {
        if (hub.conn.state === HubConnectionState.Disconnected) {
          await hub.start();
        }
        // Group membership is server-side state — re-subscribe whenever we
        // come back from a disconnect (SignalR auto-reconnect doesn't restore
        // group joins automatically).
        await hub.subscribe(id);
      } catch { /* ignore — visibility handler will retry next time */ }
      qc.invalidateQueries({ queryKey: ["job", id] });
      qc.invalidateQueries({ queryKey: ["jobs"] });
    }

    // After SignalR's auto-reconnect kicks in, our group join is gone.
    hub.conn.onreconnected(() => { void ensureLiveAndRefresh(); });

    // On Android the WebView is suspended while backgrounded; SignalR's
    // auto-reconnect window often expires before the user returns. Hook
    // visibility so we resync the moment they come back.
    function onVisibilityChange() {
      if (document.visibilityState === "visible") void ensureLiveAndRefresh();
    }
    document.addEventListener("visibilitychange", onVisibilityChange);

    (async () => {
      try {
        await hub.start();
        if (mounted) await hub.subscribe(id);
      } catch { /* ignore — visibility handler will retry */ }
    })();

    return () => {
      mounted = false;
      offLog();
      offStatus();
      document.removeEventListener("visibilitychange", onVisibilityChange);
      hub.stop().catch(() => {});
    };
  }, [id, qc]);

  // Auto-scroll only when the user is already pinned to the bottom.
  function onScroll() {
    const el = scrollRef.current;
    if (!el) return;
    stickyRef.current = el.scrollHeight - el.scrollTop - el.clientHeight < 32;
  }
  useLayoutEffect(() => {
    const el = scrollRef.current;
    if (el && stickyRef.current) el.scrollTop = el.scrollHeight;
  }, [logs.length]);

  if (job.isLoading) return <div className="py-10 text-center text-sm text-stone-500">{t("app.loading")}</div>;
  if (!job.data) return <div className="py-10 text-center text-sm">Not found.</div>;

  const showProgress = (status === "Running" || status === "Paused") && total !== null && total > 0;

  return (
    <div className="space-y-6">
      <div className="flex flex-wrap items-center gap-3">
        <Link to="/jobs" className="btn-ghost shrink-0">
          <ArrowLeft className="h-4 w-4" /> {t("nav.jobs")}
        </Link>
        <FileText className="h-5 w-5 shrink-0 text-stone-400" />
        <h1 className="min-w-0 flex-1 truncate text-xl font-semibold tracking-tight sm:text-2xl">{job.data.fileName}</h1>
        {status && <JobStatusPill status={status} />}
        {status === "Running" && (
          <button
            className="btn-secondary shrink-0"
            onClick={() => pause.mutate()}
            disabled={pause.isPending}
          >
            {pause.isPending ? <Loader2 className="h-4 w-4 animate-spin" /> : <Pause className="h-4 w-4" />}
            {t("jobDetail.pause")}
          </button>
        )}
        {status === "Paused" && (
          <button
            className="btn-secondary shrink-0"
            onClick={() => resume.mutate()}
            disabled={resume.isPending}
          >
            {resume.isPending ? <Loader2 className="h-4 w-4 animate-spin" /> : <Play className="h-4 w-4" />}
            {t("jobDetail.resume")}
          </button>
        )}
        {status === "AwaitingReview" && (
          <button
            className="btn-primary shrink-0"
            onClick={() => finalize.mutate()}
            disabled={finalize.isPending}
            title={t("review.finalizeHint")}
          >
            {finalize.isPending ? <Loader2 className="h-4 w-4 animate-spin" /> : <Check className="h-4 w-4" />}
            {t("review.finalize")}
          </button>
        )}
        {status === "Completed" && job.data.hasOutput && (
          <button
            className="btn-secondary shrink-0"
            onClick={() => reprocess.mutate()}
            disabled={reprocess.isPending}
            title={t("review.reprocessHint")}
          >
            {reprocess.isPending ? <Loader2 className="h-4 w-4 animate-spin" /> : <RotateCcw className="h-4 w-4" />}
            {t("review.reprocess")}
          </button>
        )}
        {job.data.hasOutput && job.data.canDrop && (
          <button
            className="btn-secondary shrink-0"
            onClick={() => drop.mutate()}
            disabled={drop.isPending}
            title={t("jobDetail.dropHint")}
          >
            {drop.isPending
              ? <Loader2 className="h-4 w-4 animate-spin" />
              : <FolderInput className="h-4 w-4" />}
            {t("jobDetail.copyToDrop")}
          </button>
        )}
        {job.data.hasOutput && (
          <a className="btn-primary shrink-0" href={downloadUrl(id)}>
            <Download className="h-4 w-4" /> {t("jobDetail.download")}
          </a>
        )}
      </div>

      <div className="card grid grid-cols-2 gap-4 p-4 text-sm sm:grid-cols-3 md:grid-cols-5">
        <Stat label={t("jobDetail.mode")}    value={job.data.scanAll ? t("jobDetail.fullPageScan") : t("jobDetail.patternDetection")} />
        <Stat label={t("jobDetail.model")}   value={job.data.model || "—"} />
        <Stat
          label={t("jobDetail.parallel")}
          value={String(job.data.maxWorkers)}
          hint={t("jobDetail.parallelHint")}
        />
        {!job.data.scanAll && (
          <>
            <Stat label={t("jobDetail.patterns")} value={String(job.data.patternCount)} />
            <Stat label={t("jobDetail.contextWindow")} value={`±${job.data.contextWindow}`} />
          </>
        )}
        {job.data.scanAll && <Stat label={t("jobDetail.contextWindow")} value="—" />}
        <Stat label={t("jobDetail.removed")} value={String(job.data.removed)} />
        <Stat label={t("jobDetail.created")} value={format(job.data.createdAt)} />
      </div>

      {showProgress && (
        <div className="space-y-2">
          <div className="flex items-center justify-between text-xs text-stone-500 dark:text-stone-400">
            <span>
              {t("jobDetail.progressOf", {
                done: done ?? 0,
                total,
                unit: t(job.data.scanAll ? "jobDetail.unitPages" : "jobDetail.unitChunks"),
              })}
            </span>
            <span className="tabular-nums">{progress ?? 0}%</span>
          </div>
          <div className="h-1.5 overflow-hidden rounded-full bg-stone-200 dark:bg-stone-800">
            <div className="h-full bg-stone-900 transition-all dark:bg-stone-100" style={{ width: `${progress ?? 0}%` }} />
          </div>
        </div>
      )}

      {status === "AwaitingReview" && (
        <div className="space-y-2">
          <div className="rounded border border-violet-200 bg-violet-50 px-3 py-2 text-sm text-violet-800 dark:border-violet-500/30 dark:bg-violet-500/10 dark:text-violet-200">
            {t("review.awaitingBanner")}
          </div>
          <ReviewPanel jobId={id} />
        </div>
      )}

      <div className="card">
        <div className="border-b border-stone-200 px-4 py-3 text-sm font-medium dark:border-stone-800">
          {t("jobDetail.liveLog")}
        </div>
        <div ref={scrollRef} onScroll={onScroll} className="max-h-[60vh] overflow-y-auto p-3">
          {logs.length === 0
            ? <div className="px-2 py-6 text-center text-xs text-stone-500">{t("jobDetail.noLogs")}</div>
            : renderLogs(logs)}
        </div>
      </div>

      {job.data.error && (
        <div className="card border-rose-200 bg-rose-50 p-4 text-sm text-rose-700 dark:bg-rose-500/10 dark:text-rose-300">
          <div className="font-medium">{t("jobDetail.jobFailed")}</div>
          <div>{job.data.error}</div>
        </div>
      )}
    </div>
  );
}

/**
 * Bucket consecutive lines so each per-document storyline (groupId) renders
 * as one self-contained block. Ungrouped lines stay as standalone rows.
 * The first line of a group is what fixes the group's position in time, so
 * blocks appear in the order their first line was emitted — even when the
 * underlying timeline interleaves lines from other groups in between.
 */
function renderLogs(lines: LineProps[]): JSX.Element[] {
  type Item =
    | { kind: "line"; line: LineProps; key: number }
    | { kind: "group"; id: string; lines: LineProps[]; key: number };

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
    if (it.kind === "line") return <LogRow key={it.key} line={it.line} />;
    // A "group" of one is just a regular line — only render the visual
    // group card when the doc actually had multiple lines (i.e. retries).
    if (it.lines.length === 1) return <LogRow key={it.key} line={it.lines[0]} />;
    return <LogGroup key={it.key} groupId={it.id} lines={it.lines} />;
  });
}

function LogGroup({ groupId, lines }: { groupId: string; lines: LineProps[] }) {
  // Headline = the final disposition line (removed/clean/warn skip), so the
  // group's at-a-glance status is the *last* thing that happened to it,
  // not the first.
  const final = lines[lines.length - 1];
  const finalLevel = final.level;
  const stripeClass =
    finalLevel === "removed" ? "border-fuchsia-400 dark:border-fuchsia-500/60"
    : finalLevel === "partial" ? "border-amber-500 dark:border-amber-500/70"
    : finalLevel === "clean" ? "border-emerald-400 dark:border-emerald-500/60"
    : finalLevel === "warn"  ? "border-amber-400 dark:border-amber-500/60"
    : finalLevel === "error" ? "border-rose-400 dark:border-rose-500/60"
    : "border-stone-300 dark:border-stone-600";

  return (
    <div className={`my-2 ml-1 border-l-2 pl-2 ${stripeClass}`}>
      <div className="px-2 py-0.5 text-[10px] font-semibold uppercase tracking-wider text-stone-500 dark:text-stone-400">
        {groupId}
      </div>
      {lines.map((l, i) => <LogRow key={i} line={l} />)}
    </div>
  );
}

function LogRow({ line }: { line: LineProps }) {
  const { t } = useTranslation();
  const [open, setOpen] = useState(false);
  const hasDetail = !!line.detail && line.detail.trim().length > 0;

  return (
    <div>
      <div className="log-line" data-level={line.level}>
        <span className="ts">{timeOnly(line.ts)}</span>
        <span className="lvl">{line.level}</span>
        <span className="msg">
          {line.message}
          {hasDetail && (
            <button
              type="button"
              onClick={() => setOpen((o) => !o)}
              className="ml-2 inline-flex items-center gap-0.5 rounded px-1 py-0 text-[10px] font-semibold uppercase tracking-wide text-stone-400 hover:bg-stone-100 hover:text-stone-700 dark:hover:bg-stone-800 dark:hover:text-stone-200"
            >
              {open ? <ChevronDown className="h-3 w-3" /> : <ChevronRight className="h-3 w-3" />}
              {open ? t("jobDetail.hideRaw") : t("jobDetail.showRaw")}
            </button>
          )}
        </span>
      </div>
      {open && hasDetail && (
        <pre className="my-1 ml-[164px] mr-2 max-h-72 overflow-auto whitespace-pre-wrap rounded-md bg-stone-100 p-2 font-mono text-[11.5px] leading-relaxed text-stone-700 ring-1 ring-stone-200 dark:bg-stone-800 dark:text-stone-300 dark:ring-stone-700">
          {line.detail}
        </pre>
      )}
    </div>
  );
}

function Stat({ label, value, hint }: { label: string; value: string; hint?: string }) {
  return (
    <div title={hint}>
      <div className="text-[11px] uppercase tracking-wide text-stone-500 dark:text-stone-400">{label}</div>
      <div className="mt-0.5 truncate text-sm font-medium">{value}</div>
    </div>
  );
}
