import { CheckCircle2, CircleDashed, Eye, Loader2, Pause, Slash, XCircle } from "lucide-react";
import { useTranslation } from "react-i18next";
import { NovelStatus } from "../api/novels";
import { clsx } from "../utils/clsx";

export function NovelStatusPill({ status }: { status: NovelStatus }) {
  const { t } = useTranslation();
  const map = {
    Queued:          { cls: "bg-stone-100 text-stone-700 ring-stone-300 dark:bg-stone-700 dark:text-stone-300 dark:ring-stone-600" },
    Running:         { cls: "bg-sky-50 text-sky-700 ring-sky-200 dark:bg-sky-500/10 dark:text-sky-300 dark:ring-sky-500/30" },
    Paused:          { cls: "bg-amber-50 text-amber-800 ring-amber-200 dark:bg-amber-500/10 dark:text-amber-200 dark:ring-amber-500/30" },
    Completed:       { cls: "bg-emerald-50 text-emerald-700 ring-emerald-200 dark:bg-emerald-500/10 dark:text-emerald-300 dark:ring-emerald-500/30" },
    Failed:          { cls: "bg-rose-50 text-rose-700 ring-rose-200 dark:bg-rose-500/10 dark:text-rose-300 dark:ring-rose-500/30" },
    Canceled:        { cls: "bg-amber-50 text-amber-700 ring-amber-200 dark:bg-amber-500/10 dark:text-amber-200 dark:ring-amber-500/30" },
    AwaitingReview:  { cls: "bg-violet-50 text-violet-700 ring-violet-200 dark:bg-violet-500/10 dark:text-violet-300 dark:ring-violet-500/30" },
    Idle:            { cls: "bg-stone-50 text-stone-600 ring-stone-200 dark:bg-stone-800/50 dark:text-stone-400 dark:ring-stone-700" },
  } as const;
  const m = map[status];
  const Icon =
    status === "Completed"      ? CheckCircle2 :
    status === "Failed"         ? XCircle :
    status === "Canceled"       ? Slash :
    status === "Paused"         ? Pause :
    status === "AwaitingReview" ? Eye :
    status === "Idle"           ? CircleDashed :
    status === "Running"        ? Loader2 : null;

  return (
    <span className={clsx("pill", m.cls)}>
      {Icon && <Icon className={clsx("h-3 w-3", status === "Running" && "animate-spin")} />}
      {t(`status.${status}`)}
    </span>
  );
}
