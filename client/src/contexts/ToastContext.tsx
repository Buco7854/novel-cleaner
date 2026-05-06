import { CheckCircle2, CircleAlert, Info, X, XCircle } from "lucide-react";
import { createContext, useCallback, useContext, useMemo, useState } from "react";
import { clsx } from "../utils/clsx";

type ToastKind = "success" | "error" | "warn" | "info";
interface Toast { id: number; kind: ToastKind; title: string; message?: string }

interface Api {
  push: (t: Omit<Toast, "id">) => void;
  success: (title: string, message?: string) => void;
  error:   (title: string, message?: string) => void;
  warn:    (title: string, message?: string) => void;
  info:    (title: string, message?: string) => void;
}

const Ctx = createContext<Api | null>(null);
let counter = 0;

export function ToastProvider({ children }: { children: React.ReactNode }) {
  const [toasts, setToasts] = useState<Toast[]>([]);
  const remove = useCallback((id: number) => setToasts((ts) => ts.filter((t) => t.id !== id)), []);
  const push = useCallback<Api["push"]>((t) => {
    const id = ++counter;
    setToasts((ts) => [...ts, { ...t, id }]);
    setTimeout(() => remove(id), 5000);
  }, [remove]);

  const api = useMemo<Api>(() => ({
    push,
    success: (title, message) => push({ kind: "success", title, message }),
    error:   (title, message) => push({ kind: "error",   title, message }),
    warn:    (title, message) => push({ kind: "warn",    title, message }),
    info:    (title, message) => push({ kind: "info",    title, message }),
  }), [push]);

  return (
    <Ctx.Provider value={api}>
      {children}
      <div className="fixed top-4 right-4 z-50 flex w-[360px] max-w-[calc(100vw-2rem)] flex-col gap-2">
        {toasts.map((t) => (
          <div
            key={t.id}
            role="status"
            className={clsx(
              "flex items-start gap-3 rounded-lg p-3 shadow-lg ring-1 ring-inset",
              "bg-paper dark:bg-stone-800",
              t.kind === "success" && "ring-emerald-500/30",
              t.kind === "error"   && "ring-rose-500/30",
              t.kind === "warn"    && "ring-amber-500/30",
              t.kind === "info"    && "ring-sky-500/30"
            )}
          >
            <span className="mt-0.5">
              {t.kind === "success" && <CheckCircle2 className="h-5 w-5 text-emerald-500" />}
              {t.kind === "error"   && <XCircle      className="h-5 w-5 text-rose-500"    />}
              {t.kind === "warn"    && <CircleAlert  className="h-5 w-5 text-amber-500"   />}
              {t.kind === "info"    && <Info         className="h-5 w-5 text-sky-500"     />}
            </span>
            <div className="flex-1 text-sm">
              <div className="font-medium text-stone-900 dark:text-stone-100">{t.title}</div>
              {t.message && <div className="mt-0.5 text-stone-600 dark:text-stone-400">{t.message}</div>}
            </div>
            <button onClick={() => remove(t.id)} className="rounded-md p-0.5 hover:bg-stone-100 dark:hover:bg-stone-800">
              <X className="h-4 w-4 text-stone-500" />
            </button>
          </div>
        ))}
      </div>
    </Ctx.Provider>
  );
}

export function useToast() {
  const v = useContext(Ctx);
  if (!v) throw new Error("ToastProvider missing");
  return v;
}
