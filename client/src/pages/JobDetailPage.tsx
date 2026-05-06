import { useMutation, useQuery } from "@tanstack/react-query";
import { ArrowLeft, Download, FileText, FolderInput, Loader2 } from "lucide-react";
import { useEffect, useLayoutEffect, useRef, useState } from "react";
import { useTranslation } from "react-i18next";
import { Link, useParams } from "react-router-dom";
import { copyToDrop, downloadUrl, getJob, JobStatus } from "../api/jobs";
import { createJobHub, JobLogEvent } from "../api/jobsHub";
import { JobStatusPill } from "../components/JobStatusPill";
import { useToast } from "../contexts/ToastContext";
import { format, timeOnly } from "../utils/date";

interface LineProps { ts: string; level: string; message: string }

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

  const drop = useMutation({
    mutationFn: () => copyToDrop(id),
    onSuccess: (r) => toast.success(t("jobDetail.dropSuccess"), r.destination),
    onError: (e) => toast.error(t("jobDetail.dropFailed"), e instanceof Error ? e.message : ""),
  });

  useEffect(() => {
    if (job.data) {
      setLogs(job.data.logs.map((l) => ({ ts: l.timestamp, level: l.level, message: l.message })));
      setStatus(job.data.status);
    }
  }, [job.data]);

  useEffect(() => {
    if (!id) return;
    const hub = createJobHub();
    let mounted = true;
    const offLog = hub.onLog((e: JobLogEvent) => {
      if (e.jobId !== id) return;
      setLogs((cur) => [...cur, { ts: e.timestamp, level: e.level, message: e.message }]);
    });
    const offStatus = hub.onStatus((e) => {
      if (e.jobId !== id) return;
      setStatus(e.status as JobStatus);
      if (typeof e.progress === "number") setProgress(e.progress);
      if (typeof e.done === "number") setDone(e.done);
      if (typeof e.total === "number") setTotal(e.total);
    });
    (async () => { await hub.start(); if (mounted) await hub.subscribe(id); })().catch(() => {});
    return () => { mounted = false; offLog(); offStatus(); hub.stop().catch(() => {}); };
  }, [id]);

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

  const isRunning = status === "Running";
  const showProgress = isRunning && total !== null && total > 0;

  return (
    <div className="space-y-6">
      <div className="flex flex-wrap items-center gap-3">
        <Link to="/jobs" className="btn-ghost shrink-0">
          <ArrowLeft className="h-4 w-4" /> {t("nav.jobs")}
        </Link>
        <FileText className="h-5 w-5 shrink-0 text-stone-400" />
        <h1 className="min-w-0 flex-1 truncate text-xl font-semibold tracking-tight sm:text-2xl">{job.data.fileName}</h1>
        {status && <JobStatusPill status={status} />}
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

      <div className="card">
        <div className="border-b border-stone-200 px-4 py-3 text-sm font-medium dark:border-stone-800">
          {t("jobDetail.liveLog")}
        </div>
        <div ref={scrollRef} onScroll={onScroll} className="max-h-[60vh] overflow-y-auto p-3">
          {logs.length === 0
            ? <div className="px-2 py-6 text-center text-xs text-stone-500">{t("jobDetail.noLogs")}</div>
            : logs.map((l, i) => (
                <div key={i} className="log-line" data-level={l.level}>
                  <span className="ts">{timeOnly(l.ts)}</span>
                  <span className="lvl">{l.level}</span>
                  <span className="msg">{l.message}</span>
                </div>
              ))}
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

function Stat({ label, value, hint }: { label: string; value: string; hint?: string }) {
  return (
    <div title={hint}>
      <div className="text-[11px] uppercase tracking-wide text-stone-500 dark:text-stone-400">{label}</div>
      <div className="mt-0.5 truncate text-sm font-medium">{value}</div>
    </div>
  );
}
