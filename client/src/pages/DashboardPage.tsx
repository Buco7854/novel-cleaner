import { useQuery, useQueryClient } from "@tanstack/react-query";
import { ArrowRight, FileText } from "lucide-react";
import { useState } from "react";
import { Trans, useTranslation } from "react-i18next";
import { Link } from "react-router-dom";
import { listNovels, novelDisplayName, uploadNovels } from "../api/novels";
import { fetchSettings } from "../api/settings";
import { Dropzone } from "../components/Dropzone";
import { NovelStatusPill } from "../components/NovelStatusPill";
import { useToast } from "../contexts/ToastContext";

export function DashboardPage() {
  const { t } = useTranslation();
  const toast = useToast();
  const qc = useQueryClient();
  const settings = useQuery({ queryKey: ["settings"], queryFn: fetchSettings });
  const novels = useQuery({ queryKey: ["novels", "recent"], queryFn: () => listNovels(1, 6), refetchInterval: 4000 });
  const [uploadingCount, setUploadingCount] = useState(0);

  async function handleUpload(files: File[]) {
    if (!settings.data?.app.hasApiKey || !settings.data?.app.model) {
      toast.error(t("settings.title"), t("settings.llm.apiKeyRequired"));
      return;
    }
    setUploadingCount(files.length);
    try {
      await uploadNovels(files);
      toast.success(`${t("novels.title")} +${files.length}`);
      await qc.invalidateQueries({ queryKey: ["novels"] });
    } catch (e) {
      toast.error("Upload failed", e instanceof Error ? e.message : "Unknown error");
    } finally {
      setUploadingCount(0);
    }
  }

  return (
    <div className="space-y-8">
      <div>
        <h1 className="page-title">{t("dashboard.title")}</h1>
        <p className="page-subtitle">{t("dashboard.subtitle")}</p>
      </div>

      <div className="card space-y-5 p-5 sm:p-6">
        <Dropzone
          onFiles={handleUpload}
          uploading={uploadingCount > 0}
          uploadingCount={uploadingCount}
          hint={t("dashboard.dropMeta")}
        />
        {!settings.data?.app.hasApiKey && (
          <div className="rounded-md bg-amber-50 p-3 text-sm text-amber-700 ring-1 ring-amber-200 dark:bg-amber-500/10 dark:text-amber-200 dark:ring-amber-500/30">
            <Trans i18nKey="dashboard.settingsWarning"
              components={{ 1: <Link className="underline" to="/settings" /> }} />
          </div>
        )}
      </div>

      <div className="card overflow-hidden">
        <div className="flex items-center justify-between border-b border-stone-200 px-5 py-3.5 dark:border-stone-700">
          <div className="text-sm font-semibold">{t("dashboard.recentNovels")}</div>
          <Link to="/novels" className="text-xs text-stone-500 hover:text-stone-900 dark:text-stone-400 dark:hover:text-stone-100">
            {t("dashboard.viewAll")} →
          </Link>
        </div>
        <div>
          {novels.data?.items.length === 0 ? (
            <div className="grid place-items-center px-6 py-10 text-sm text-stone-500 dark:text-stone-400">
              {t("dashboard.noNovels")}
            </div>
          ) : (
            (novels.data?.items ?? []).map((n) => (
              <Link
                key={n.id}
                to={`/novels/${n.id}`}
                className="flex items-center gap-3 border-b border-stone-200 px-5 py-3.5 last:border-0 hover:bg-stone-50 dark:border-stone-700 dark:hover:bg-stone-700/40"
              >
                <FileText className="h-4 w-4 shrink-0 text-stone-400" />
                <div className="min-w-0 flex-1">
                  <div className="truncate text-sm font-medium">{novelDisplayName(n)}</div>
                  <div className="truncate text-xs text-stone-500 dark:text-stone-400">
                    {n.author ? `${n.author} · ` : ""}{n.model || "—"} · {t("novels.removed", { count: n.removed })}
                  </div>
                </div>
                <NovelStatusPill status={n.status} />
                <ArrowRight className="h-3.5 w-3.5 text-stone-400" />
              </Link>
            ))
          )}
        </div>
      </div>
    </div>
  );
}
