import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { format } from "../utils/date";
import { ChevronLeft, ChevronRight, Download, FileText, Trash2 } from "lucide-react";
import { useState } from "react";
import { useTranslation } from "react-i18next";
import { Link } from "react-router-dom";
import { deleteNovel, downloadUrl, listNovels, Novel, novelDisplayName, PAGE_SIZE } from "../api/novels";
import { NovelStatusPill } from "../components/NovelStatusPill";
import { useToast } from "../contexts/ToastContext";

export function NovelsPage() {
  const { t } = useTranslation();
  const toast = useToast();
  const qc = useQueryClient();
  const [page, setPage] = useState(1);

  const novels = useQuery({
    queryKey: ["novels", "page", page],
    queryFn: () => listNovels(page),
    refetchInterval: 4000,
  });

  const remove = useMutation({
    mutationFn: deleteNovel,
    onSuccess: () => qc.invalidateQueries({ queryKey: ["novels"] }),
    onError: (e) => toast.error("Could not delete", e instanceof Error ? e.message : ""),
  });

  const total = novels.data?.total ?? 0;
  const totalPages = Math.max(1, Math.ceil(total / PAGE_SIZE));
  const items = novels.data?.items ?? [];
  const isEmpty = novels.data && items.length === 0;

  return (
    <div className="space-y-8">
      <div>
        <h1 className="page-title">{t("novels.title")}</h1>
        <p className="page-subtitle">{t("novels.subtitle")}</p>
      </div>

      <div className="card overflow-hidden">
        {/* Desktop header */}
        <div className="hidden md:grid grid-cols-[1fr_140px_180px_88px] items-center gap-4 border-b border-stone-200 bg-stone-50/70 px-5 py-2.5 text-[11px] font-medium uppercase tracking-wider text-stone-500 dark:border-stone-700 dark:bg-stone-900/50 dark:text-stone-400">
          <div>{t("novels.file")}</div>
          <div>{t("novels.status")}</div>
          <div>{t("novels.created")}</div>
          <div className="text-right">{t("novels.actions")}</div>
        </div>

        {isEmpty && (
          <div className="grid place-items-center px-6 py-12 text-sm text-stone-500 dark:text-stone-400">
            {t("novels.noNovels")}
          </div>
        )}

        {items.map((n) => (
          <NovelRow key={n.id} novel={n} onDelete={() => remove.mutate(n.id)} />
        ))}

        {totalPages > 1 && (
          <div className="flex flex-col items-center justify-between gap-3 border-t border-stone-200 px-4 py-3 text-xs text-stone-500 dark:border-stone-800 dark:text-stone-400 sm:flex-row sm:px-5">
            <span>{t("novels.total_other", { count: total })}</span>
            <div className="flex items-center gap-3">
              <button
                className="btn-secondary px-2 py-1.5"
                disabled={page <= 1}
                onClick={() => setPage((p) => p - 1)}
                aria-label={t("novels.prev")}
              >
                <ChevronLeft className="h-4 w-4" />
              </button>
              <span className="font-medium text-stone-700 dark:text-stone-300">
                {t("novels.pagination", { current: page, total: totalPages })}
              </span>
              <button
                className="btn-secondary px-2 py-1.5"
                disabled={page >= totalPages}
                onClick={() => setPage((p) => p + 1)}
                aria-label={t("novels.next")}
              >
                <ChevronRight className="h-4 w-4" />
              </button>
            </div>
          </div>
        )}
      </div>
    </div>
  );
}

function NovelRow({ novel, onDelete }: { novel: Novel; onDelete: () => void }) {
  const { t } = useTranslation();
  const sizeMB = (novel.sizeBytes / 1024 / 1024).toFixed(2);

  return (
    <div className="border-b border-stone-200 last:border-0 hover:bg-stone-50 dark:border-stone-700 dark:hover:bg-stone-700/40">
      {/* Mobile card layout */}
      <div className="flex flex-col gap-3 p-4 md:hidden">
        <div className="flex items-start justify-between gap-3">
          <Link to={`/novels/${novel.id}`} className="flex min-w-0 flex-1 items-center gap-2.5">
            <FileText className="h-4 w-4 shrink-0 text-stone-400" />
            <div className="min-w-0">
              <div className="truncate text-sm font-medium">{novelDisplayName(novel)}</div>
              {novel.author && (
                <div className="truncate text-xs text-stone-500 dark:text-stone-400">{novel.author}</div>
              )}
            </div>
          </Link>
          <NovelStatusPill status={novel.status} />
        </div>
        <div className="flex flex-wrap items-center gap-x-3 gap-y-1 text-xs text-stone-500 dark:text-stone-400">
          <span>{sizeMB} MB</span>
          <span aria-hidden>·</span>
          <span>{t("novels.removed", { count: novel.removed })}</span>
        </div>
        <div className="flex items-center justify-between border-t border-stone-100 pt-2 dark:border-stone-700">
          <span className="text-xs text-stone-500 dark:text-stone-400">{format(novel.createdAt)}</span>
          <div className="flex items-center gap-1">
            {novel.hasOutput && (
              <a className="btn-ghost px-2 py-1.5" href={downloadUrl(novel.id)} aria-label="Download">
                <Download className="h-4 w-4" />
              </a>
            )}
            <button className="btn-ghost px-2 py-1.5 text-rose-600" onClick={onDelete} aria-label={t("users.delete")}>
              <Trash2 className="h-4 w-4" />
            </button>
          </div>
        </div>
      </div>

      {/* Desktop row layout */}
      <Link
        to={`/novels/${novel.id}`}
        className="hidden md:grid grid-cols-[1fr_140px_180px_88px] items-center gap-4 px-5 py-3.5"
      >
        <div className="flex min-w-0 items-center gap-3">
          <FileText className="h-4 w-4 shrink-0 text-stone-400" />
          <div className="min-w-0">
            <div className="truncate text-sm font-medium">{novelDisplayName(novel)}</div>
            <div className="truncate text-xs text-stone-500 dark:text-stone-400">
              {novel.author ? `${novel.author} · ` : ""}{sizeMB} MB · {t("novels.removed", { count: novel.removed })}
            </div>
          </div>
        </div>
        <div><NovelStatusPill status={novel.status} /></div>
        <div className="text-xs text-stone-500 dark:text-stone-400">{format(novel.createdAt)}</div>
        <div className="flex items-center justify-end gap-1" onClick={(e) => { e.preventDefault(); }}>
          {novel.hasOutput && (
            <a className="btn-ghost px-2 py-1.5" href={downloadUrl(novel.id)} onClick={(e) => e.stopPropagation()} aria-label="Download">
              <Download className="h-4 w-4" />
            </a>
          )}
          <button
            className="btn-ghost px-2 py-1.5 text-rose-600"
            onClick={(e) => { e.preventDefault(); e.stopPropagation(); onDelete(); }}
            aria-label={t("users.delete")}
          >
            <Trash2 className="h-4 w-4" />
          </button>
        </div>
      </Link>
    </div>
  );
}
