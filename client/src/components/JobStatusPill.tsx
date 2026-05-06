import { CheckCircle2, Loader2, Pause, Slash, XCircle } from "lucide-react";
import { useTranslation } from "react-i18next";
import { JobStatus } from "../api/jobs";
import { clsx } from "../utils/clsx";

export function JobStatusPill({ status }: { status: JobStatus }) {
  const { t } = useTranslation();
  const map = {
    Queued:    { cls: "bg-stone-100 text-stone-700 ring-stone-300 dark:bg-stone-700 dark:text-stone-300 dark:ring-stone-600" },
    Running:   { cls: "bg-sky-50 text-sky-700 ring-sky-200 dark:bg-sky-500/10 dark:text-sky-300 dark:ring-sky-500/30" },
    Paused:    { cls: "bg-amber-50 text-amber-800 ring-amber-200 dark:bg-amber-500/10 dark:text-amber-200 dark:ring-amber-500/30" },
    Completed: { cls: "bg-emerald-50 text-emerald-700 ring-emerald-200 dark:bg-emerald-500/10 dark:text-emerald-300 dark:ring-emerald-500/30" },
    Failed:    { cls: "bg-rose-50 text-rose-700 ring-rose-200 dark:bg-rose-500/10 dark:text-rose-300 dark:ring-rose-500/30" },
    Canceled:  { cls: "bg-amber-50 text-amber-700 ring-amber-200 dark:bg-amber-500/10 dark:text-amber-200 dark:ring-amber-500/30" },
  } as const;
  const m = map[status];
  const Icon =
    status === "Completed" ? CheckCircle2 :
    status === "Failed"    ? XCircle :
    status === "Canceled"  ? Slash :
    status === "Paused"    ? Pause :
    status === "Running"   ? Loader2 : null;

  return (
    <span className={clsx("pill", m.cls)}>
      {Icon && <Icon className={clsx("h-3 w-3", status === "Running" && "animate-spin")} />}
      {t(`status.${status}`)}
    </span>
  );
}
