import { createContext, useCallback, useContext, useState } from "react";
import { Loader2 } from "lucide-react";
import { Modal } from "../components/Modal";
import { clsx } from "../utils/clsx";

interface ConfirmOptions {
  /** Heading shown in the modal header. */
  title: string;
  /** Body content — string or rich JSX. */
  body: React.ReactNode;
  /** Label for the confirm button. Defaults to "Confirm". */
  confirmLabel?: string;
  /** Label for the cancel button. Defaults to "Cancel". */
  cancelLabel?: string;
  /** Render the confirm button with the danger style (red). */
  danger?: boolean;
}

type Confirm = (opts: ConfirmOptions) => Promise<boolean>;

const Ctx = createContext<Confirm | null>(null);

interface PendingConfirm extends ConfirmOptions {
  resolve: (v: boolean) => void;
}

/**
 * Promise-based confirmation modal — replaces every <code>window.confirm()</code>
 * call with a styled in-app dialog. Resolves to <code>true</code> when the user
 * confirms, <code>false</code> on cancel / dismiss.
 */
export function ConfirmProvider({ children }: { children: React.ReactNode }) {
  const [pending, setPending] = useState<PendingConfirm | null>(null);

  const confirm = useCallback<Confirm>((opts) => {
    return new Promise<boolean>((resolve) => {
      setPending({ ...opts, resolve });
    });
  }, []);

  function close(result: boolean) {
    if (!pending) return;
    pending.resolve(result);
    setPending(null);
  }

  return (
    <Ctx.Provider value={confirm}>
      {children}
      <Modal open={pending !== null} onClose={() => close(false)} title={pending?.title} size="sm">
        <div className="text-sm text-stone-700 dark:text-stone-200">
          {pending?.body}
        </div>
        <div className="mt-5 flex justify-end gap-2">
          <button onClick={() => close(false)} className="btn-secondary">
            {pending?.cancelLabel ?? "Cancel"}
          </button>
          <button
            onClick={() => close(true)}
            className={clsx(pending?.danger ? "btn-danger" : "btn-primary")}
            autoFocus
          >
            {pending?.confirmLabel ?? "Confirm"}
          </button>
        </div>
      </Modal>
    </Ctx.Provider>
  );
}

export function useConfirm(): Confirm {
  const c = useContext(Ctx);
  if (!c) {
    // Fallback to native confirm if the provider isn't mounted (test
    // harnesses, isolated stories, etc.). Production wraps the app so
    // this branch shouldn't fire.
    return async ({ title, body }: ConfirmOptions) => {
      const text = typeof body === "string" ? `${title}\n\n${body}` : title;
      return Promise.resolve(window.confirm(text));
    };
  }
  return c;
}

/** Loader for inline use inside a custom confirm body. */
export const ConfirmSpinner = Loader2;
