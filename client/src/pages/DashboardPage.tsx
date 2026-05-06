import { useQuery, useQueryClient } from "@tanstack/react-query";
import { ArrowRight, FileText, ScanSearch } from "lucide-react";
import { useState } from "react";
import { Trans, useTranslation } from "react-i18next";
import { Link } from "react-router-dom";
import { listJobs, uploadJobs } from "../api/jobs";
import { fetchSettings } from "../api/settings";
import { Dropzone } from "../components/Dropzone";
import { JobStatusPill } from "../components/JobStatusPill";
import { useToast } from "../contexts/ToastContext";
import { clsx } from "../utils/clsx";

export function DashboardPage() {
  const { t } = useTranslation();
  const toast = useToast();
  const qc = useQueryClient();
  const settings = useQuery({ queryKey: ["settings"], queryFn: fetchSettings });
  const jobs = useQuery({ queryKey: ["jobs", "recent"], queryFn: () => listJobs(1, 6), refetchInterval: 4000 });
  const [uploading, setUploading] = useState(false);
  const [scanAllOverride, setScanAllOverride] = useState<boolean | null>(null);

  const effectiveScanAll = scanAllOverride ?? settings.data?.user.scanAll ?? false;

  async function handleUpload(files: File[]) {
    if (!settings.data?.app.hasApiKey || !settings.data?.app.model) {
      toast.error(t("settings.title"), t("settings.llm.apiKeyRequired"));
      return;
    }
    setUploading(true);
    try {
      await uploadJobs(files, { scanAll: effectiveScanAll });
      toast.success(`${t("jobs.title")} +${files.length}`);
      await qc.invalidateQueries({ queryKey: ["jobs"] });
    } catch (e) {
      toast.error("Upload failed", e instanceof Error ? e.message : "Unknown error");
    } finally {
      setUploading(false);
    }
  }

  return (
    <div className="space-y-8">
      <div>
        <h1 className="page-title">{t("dashboard.title")}</h1>
        <p className="page-subtitle">{t("dashboard.subtitle")}</p>
      </div>

      <div className="card space-y-5 p-5 sm:p-6">
        <div className="flex flex-col items-center gap-3 text-center sm:flex-row sm:items-center sm:justify-between sm:text-left">
          <div className="min-w-0 text-sm">
            <div className="font-medium text-stone-700 dark:text-stone-200">{t("dashboard.scanMode")}</div>
            <div className="text-stone-500 dark:text-stone-400">{t("dashboard.scanModeDescription")}</div>
          </div>
          <ModeToggle value={effectiveScanAll} onChange={setScanAllOverride} />
        </div>
        <Dropzone
          onFiles={handleUpload}
          disabled={uploading}
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
        <div className="flex items-center justify-between border-b border-stone-200 px-5 py-3.5 dark:border-stone-800">
          <div className="text-sm font-semibold">{t("dashboard.recentJobs")}</div>
          <Link to="/jobs" className="text-xs text-stone-500 hover:text-stone-900 dark:text-stone-400 dark:hover:text-stone-100">
            {t("dashboard.viewAll")} →
          </Link>
        </div>
        <div>
          {jobs.data?.items.length === 0 ? (
            <div className="grid place-items-center px-6 py-10 text-sm text-stone-500 dark:text-stone-400">
              {t("dashboard.noJobs")}
            </div>
          ) : (
            (jobs.data?.items ?? []).map((j) => (
              <Link
                key={j.id}
                to={`/jobs/${j.id}`}
                className="flex items-center gap-3 border-b border-stone-200 px-5 py-3.5 last:border-0 hover:bg-stone-50 dark:border-stone-800 dark:hover:bg-stone-800/60"
              >
                <FileText className="h-4 w-4 shrink-0 text-stone-400" />
                <div className="min-w-0 flex-1">
                  <div className="truncate text-sm font-medium">{j.fileName}</div>
                  <div className="truncate text-xs text-stone-500 dark:text-stone-400">
                    {j.scanAll ? t("jobs.fullScan") : t("jobs.patternScan")} · {j.model || "—"} · {t("jobs.removed", { count: j.removed })}
                  </div>
                </div>
                <JobStatusPill status={j.status} />
                <ArrowRight className="h-3.5 w-3.5 text-stone-400" />
              </Link>
            ))
          )}
        </div>
      </div>
    </div>
  );
}

function ModeToggle({ value, onChange }: { value: boolean; onChange: (v: boolean) => void }) {
  const { t } = useTranslation();
  return (
    <div className="inline-flex rounded-lg bg-stone-100 p-1 dark:bg-stone-800">
      <button
        type="button"
        onClick={() => onChange(false)}
        className={clsx(
          "flex items-center gap-2 rounded-md px-3 py-1.5 text-sm transition-all",
          !value
            ? "bg-white text-stone-900 shadow-sm dark:bg-stone-700 dark:text-stone-100"
            : "text-stone-500 hover:text-stone-900 dark:text-stone-400 dark:hover:text-stone-100"
        )}
      >
        <ScanSearch className="h-3.5 w-3.5" /> {t("dashboard.pattern")}
      </button>
      <button
        type="button"
        onClick={() => onChange(true)}
        className={clsx(
          "flex items-center gap-2 rounded-md px-3 py-1.5 text-sm transition-all",
          value
            ? "bg-white text-stone-900 shadow-sm dark:bg-stone-700 dark:text-stone-100"
            : "text-stone-500 hover:text-stone-900 dark:text-stone-400 dark:hover:text-stone-100"
        )}
      >
        <FileText className="h-3.5 w-3.5" /> {t("dashboard.fullPage")}
      </button>
    </div>
  );
}
