import { BookOpen, KeyRound, Loader2, TriangleAlert } from "lucide-react";
import { useState } from "react";
import { useTranslation } from "react-i18next";
import { oidcLoginUrl } from "../api/auth";
import { HttpError } from "../api/client";
import { useAuth } from "../contexts/AuthContext";
import { LangToggle } from "../components/LangToggle";

export function LoginPage() {
  const { t } = useTranslation();
  const { config, login } = useAuth();
  const [email, setEmail] = useState("");
  const [password, setPassword] = useState("");
  const [submitting, setSubmitting] = useState(false);
  const [error, setError] = useState<string | null>(null);

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
          <h1 className="page-title">{t("login.title")}</h1>
          <p className="page-subtitle">{t("login.subtitle")}</p>
        </div>

        {config?.password && (
          <form
            className="space-y-4"
            onSubmit={async (e) => {
              e.preventDefault();
              setSubmitting(true); setError(null);
              try { await login(email, password); }
              catch (err) {
                if (err instanceof HttpError && err.status === 401) setError(t("login.error.invalid"));
                else setError(t("login.error.failed"));
              } finally { setSubmitting(false); }
            }}
          >
            <div>
              <label className="label" htmlFor="email">{t("login.email")}</label>
              <input id="email" type="email" required autoFocus className="input"
                value={email} onChange={(e) => setEmail(e.target.value)}
                placeholder={t("login.emailPlaceholder")} />
            </div>
            <div>
              <label className="label" htmlFor="password">{t("login.password")}</label>
              <input id="password" type="password" required className="input"
                value={password} onChange={(e) => setPassword(e.target.value)} />
            </div>
            {error && (
              <div className="flex items-start gap-2 rounded-md bg-rose-50 p-3 text-sm text-rose-700 ring-1 ring-rose-200 dark:bg-rose-500/10 dark:text-rose-300 dark:ring-rose-500/30">
                <TriangleAlert className="h-4 w-4 shrink-0 mt-0.5" /> {error}
              </div>
            )}
            <button className="btn-primary w-full" disabled={submitting}>
              {submitting && <Loader2 className="h-4 w-4 animate-spin" />}
              {t("login.submit")}
            </button>
          </form>
        )}

        {config?.password && config?.oidc && (
          <div className="my-5 flex items-center gap-3 text-xs text-stone-400">
            <span className="h-px flex-1 bg-stone-200 dark:bg-stone-700" />
            or
            <span className="h-px flex-1 bg-stone-200 dark:bg-stone-700" />
          </div>
        )}

        {config?.oidc && (
          <a href={oidcLoginUrl("/")} className="btn-secondary w-full">
            <KeyRound className="h-4 w-4" />
            {t("login.ssoWith", { provider: config.oidcDisplayName || t("login.ssoFallback") })}
          </a>
        )}

        {!config?.password && !config?.oidc && (
          <div className="rounded-md bg-amber-50 p-3 text-sm text-amber-700 ring-1 ring-amber-200 dark:bg-amber-500/10 dark:text-amber-200 dark:ring-amber-500/30">
            <TriangleAlert className="mr-2 inline h-4 w-4" />
            {t("login.error.noProviders")}
          </div>
        )}
      </div>
    </div>
  );
}
