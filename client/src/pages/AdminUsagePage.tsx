import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { Activity, ExternalLink, Loader2, Pause, Play, Square } from "lucide-react";
import { useTranslation } from "react-i18next";
import { Link, Navigate } from "react-router-dom";
import { listAdminUsage } from "../api/adminUsage";
import { cancelBook, pauseBook, resumeBook } from "../api/books";
import { BookStatusPill } from "../components/BookStatusPill";
import { useAuth } from "../contexts/AuthContext";
import { useToast } from "../contexts/ToastContext";
import { format as fmtDate } from "../utils/date";

/**
 * Operational dashboard for admins — every Book the worker is currently
 * processing (or has queued / paused), regardless of owner. Per-row
 * pause / resume / cancel buttons hit the same endpoints the per-book
 * editor uses; both are admin-aware on the backend.
 */
export function AdminUsagePage() {
  const { t } = useTranslation();
  const toast = useToast();
  const qc = useQueryClient();
  const { isAdmin } = useAuth();

  const usage = useQuery({
    queryKey: ["adminUsage"],
    queryFn: listAdminUsage,
    // Auto-refresh while the page is open so admins see status changes
    // (start/pause/finish) without needing to reload manually.
    refetchInterval: 4000,
  });

  const invalidate = () => qc.invalidateQueries({ queryKey: ["adminUsage"] });

  const pause = useMutation({
    mutationFn: (id: string) => pauseBook(id),
    onSuccess: invalidate,
    onError: (e) => toast.error(t("adminUsage.pauseFailed"), e instanceof Error ? e.message : ""),
  });
  const resume = useMutation({
    mutationFn: (id: string) => resumeBook(id),
    onSuccess: invalidate,
    onError: (e) => toast.error(t("adminUsage.resumeFailed"), e instanceof Error ? e.message : ""),
  });
  const cancel = useMutation({
    mutationFn: (id: string) => cancelBook(id),
    onSuccess: invalidate,
    onError: (e) => toast.error(t("adminUsage.cancelFailed"), e instanceof Error ? e.message : ""),
  });

  if (!isAdmin) return <Navigate to="/" replace />;

  const items = usage.data ?? [];

  return (
    <div className="mx-auto max-w-5xl space-y-6">
      <div>
        <h1 className="page-title">{t("adminUsage.title")}</h1>
        <p className="page-subtitle">{t("adminUsage.subtitle")}</p>
      </div>

      {usage.isLoading && (
        <div className="flex items-center gap-2 text-sm text-stone-500">
          <Loader2 className="h-4 w-4 animate-spin" /> {t("app.loading")}
        </div>
      )}

      {!usage.isLoading && items.length === 0 && (
        <div className="card grid place-items-center gap-2 p-10 text-center text-sm text-stone-500">
          <Activity className="h-5 w-5 text-stone-400" />
          {t("adminUsage.empty")}
        </div>
      )}

      {items.length > 0 && (
        <div className="card overflow-hidden p-0">
          <ul className="divide-y divide-stone-200 dark:divide-stone-700">
            {items.map((b) => {
              const owner = b.ownerDisplayName?.trim() || b.ownerEmail || t("adminUsage.unknownOwner");
              const display = b.title?.trim() || b.fileName;
              const busy = pause.isPending || resume.isPending || cancel.isPending;
              return (
                <li key={b.id} className="flex flex-col gap-3 p-4 sm:flex-row sm:items-center">
                  <div className="min-w-0 flex-1">
                    <div className="flex items-center gap-2">
                      <Link to={`/books/${b.id}`} className="truncate text-sm font-semibold hover:underline">
                        {display}
                      </Link>
                      <ExternalLink className="h-3 w-3 shrink-0 text-stone-400" />
                    </div>
                    <div className="mt-0.5 flex flex-wrap items-center gap-x-3 gap-y-0.5 text-xs text-stone-500 dark:text-stone-400">
                      <span>{owner}</span>
                      {b.author && <span>{b.author}</span>}
                      <span className="font-mono">{b.model}</span>
                      <span>
                        {b.startedAt
                          ? t("adminUsage.startedAt", { time: fmtDate(b.startedAt) })
                          : t("adminUsage.queuedAt", { time: fmtDate(b.createdAt) })}
                      </span>
                    </div>
                  </div>

                  <div className="flex items-center gap-2 sm:shrink-0">
                    <BookStatusPill status={b.status} />

                    {b.status === "Running" && (
                      <button
                        type="button"
                        onClick={() => pause.mutate(b.id)}
                        disabled={busy}
                        className="btn-secondary px-2.5 py-1.5"
                        title={t("adminUsage.pause")}
                      >
                        <Pause className="h-3.5 w-3.5" />
                      </button>
                    )}
                    {b.status === "Paused" && (
                      <button
                        type="button"
                        onClick={() => resume.mutate(b.id)}
                        disabled={busy}
                        className="btn-secondary px-2.5 py-1.5"
                        title={t("adminUsage.resume")}
                      >
                        <Play className="h-3.5 w-3.5" />
                      </button>
                    )}
                    <button
                      type="button"
                      onClick={() => cancel.mutate(b.id)}
                      disabled={busy}
                      className="btn-secondary px-2.5 py-1.5 text-rose-700 hover:bg-rose-50 dark:text-rose-300 dark:hover:bg-rose-500/10"
                      title={t("adminUsage.cancel")}
                    >
                      <Square className="h-3.5 w-3.5" />
                    </button>
                  </div>
                </li>
              );
            })}
          </ul>
        </div>
      )}
    </div>
  );
}
