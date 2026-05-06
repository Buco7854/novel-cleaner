import { BookOpen, Loader2, TriangleAlert } from "lucide-react";
import { useState } from "react";
import { useTranslation } from "react-i18next";
import { useNavigate } from "react-router-dom";
import { completeSetup } from "../api/setup";
import { HttpError } from "../api/client";
import { useAuth } from "../contexts/AuthContext";
import { LangToggle } from "../components/LangToggle";

export function SetupPage() {
  const { t } = useTranslation();
  const { refresh } = useAuth();
  const nav = useNavigate();
  const [email, setEmail] = useState("");
  const [displayName, setDisplayName] = useState("");
  const [password, setPassword] = useState("");
  const [confirm, setConfirm] = useState("");
  const [submitting, setSubmitting] = useState(false);
  const [error, setError] = useState<string | null>(null);

  async function handleSubmit(e: React.FormEvent) {
    e.preventDefault();
    if (password !== confirm) { setError(t("setup.error.mismatch")); return; }
    setSubmitting(true); setError(null);
    try {
      await completeSetup(email, password, displayName || undefined);
      await refresh();
      nav("/", { replace: true });
    } catch (err) {
      const body = err instanceof HttpError ? (err.body as { error?: string } | null) : null;
      setError(body?.error ?? t("setup.error.failed"));
    } finally {
      setSubmitting(false);
    }
  }

  return (
    <div className="grid min-h-full place-items-center bg-cream p-4 dark:bg-stone-900">
      <div className="absolute top-4 right-4">
        <LangToggle />
      </div>

      <div className="w-full max-w-sm">
        <div className="mb-8">
          <div className="mb-5 flex items-center gap-2.5">
            <BookOpen className="h-5 w-5 text-stone-700 dark:text-stone-300" strokeWidth={1.75} />
            <span className="text-sm font-semibold tracking-tight">{t("app.name")}</span>
          </div>
          <h1 className="page-title">{t("setup.title")}</h1>
          <p className="page-subtitle">{t("setup.subtitle")}</p>
        </div>

        <form className="space-y-4" onSubmit={handleSubmit}>
          <div>
            <label className="label" htmlFor="setup-name">{t("setup.displayName")}</label>
            <input id="setup-name" type="text" className="input"
              placeholder={t("setup.displayNamePlaceholder")} autoFocus
              value={displayName} onChange={(e) => setDisplayName(e.target.value)} />
          </div>
          <div>
            <label className="label" htmlFor="setup-email">{t("setup.email")}</label>
            <input id="setup-email" type="email" required className="input"
              placeholder={t("setup.emailPlaceholder")}
              value={email} onChange={(e) => setEmail(e.target.value)} />
          </div>
          <div>
            <label className="label" htmlFor="setup-password">{t("setup.password")}</label>
            <input id="setup-password" type="password" required minLength={10} className="input"
              placeholder={t("setup.passwordPlaceholder")}
              value={password} onChange={(e) => setPassword(e.target.value)} />
          </div>
          <div>
            <label className="label" htmlFor="setup-confirm">{t("setup.confirmPassword")}</label>
            <input id="setup-confirm" type="password" required className="input"
              value={confirm} onChange={(e) => setConfirm(e.target.value)} />
          </div>

          {error && (
            <div className="flex items-start gap-2 rounded-md bg-rose-50 p-3 text-sm text-rose-700 ring-1 ring-rose-200 dark:bg-rose-500/10 dark:text-rose-300 dark:ring-rose-500/30">
              <TriangleAlert className="h-4 w-4 shrink-0 mt-0.5" /> {error}
            </div>
          )}

          <button type="submit" className="btn-primary w-full"
            disabled={submitting || !email || !password || !confirm}>
            {submitting && <Loader2 className="h-4 w-4 animate-spin" />}
            {t("setup.submit")}
          </button>
        </form>
      </div>
    </div>
  );
}
