import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { Eye, Lock, Save, ShieldCheck } from "lucide-react";
import { useEffect, useState } from "react";
import { useTranslation } from "react-i18next";
import { Navigate } from "react-router-dom";
import { AppSettings, fetchSettings, saveAppSettings } from "../api/settings";
import { useAuth } from "../contexts/AuthContext";
import { useToast } from "../contexts/ToastContext";

/**
 * Global / admin-scope settings: LLM credentials, model, prompt overrides,
 * concurrency, drop folder. Non-admins are bounced to the personal
 * Settings page rather than seeing a read-only view.
 */
export function AdminSettingsPage() {
  const { t } = useTranslation();
  const toast = useToast();
  const qc = useQueryClient();
  const { isAdmin } = useAuth();
  const q = useQuery({ queryKey: ["settings"], queryFn: fetchSettings });

  const [form, setForm] = useState<AppSettings | null>(null);
  const [apiKey, setApiKey] = useState("");

  useEffect(() => {
    if (q.data && !form) {
      // Defensive default — older server builds didn't expose aiEnabled,
      // and we'd rather a missing field default to "on" than silently
      // disable the feature for everyone after a server upgrade.
      setForm({ ...q.data.app, aiEnabled: q.data.app.aiEnabled ?? true });
    }
  }, [q.data, form]);

  const save = useMutation({
    mutationFn: () => saveAppSettings({
      apiKey: apiKey || undefined,
      baseUrl: form?.baseUrl ?? "",
      model: form?.model ?? "",
      maxWorkers: form?.maxWorkers ?? 3,
      systemPrompt: form?.systemPrompt ?? "",
      dropFolder: form?.dropFolder ?? "",
      aiEnabled: form?.aiEnabled ?? true,
    }),
    onSuccess: () => {
      toast.success(t("settings.saved"));
      setApiKey("");
      qc.invalidateQueries({ queryKey: ["settings"] });
    },
    onError: (e) => toast.error("Save failed", e instanceof Error ? e.message : ""),
  });

  if (!isAdmin) return <Navigate to="/settings" replace />;
  if (!form) return <div className="py-10 text-center text-sm text-stone-500">{t("app.loading")}</div>;

  function update<K extends keyof AppSettings>(k: K, v: AppSettings[K]) {
    setForm((f) => f ? { ...f, [k]: v } : f);
  }

  const readOnly = !form.canEdit;
  const env = form.managedByEnv ?? {
    apiKey: false, baseUrl: false, model: false, maxWorkers: false,
    systemPrompt: false, dropFolder: false, aiEnabled: false,
  };
  // Per-field disabled = global read-only OR pinned by env.
  const lock = (field: keyof typeof env) => readOnly || env[field];
  const envHint = (field: keyof typeof env) =>
    env[field] ? t("settings.envManaged") : undefined;

  return (
    <div className="mx-auto max-w-3xl space-y-8">
      <div className="flex items-start justify-between gap-3">
        <div>
          <h1 className="page-title">{t("settings.adminTitle")}</h1>
          <p className="page-subtitle">{t("settings.scopes.appHint")}</p>
        </div>
        <span className="pill bg-stone-100 text-stone-600 ring-stone-200 shrink-0 dark:bg-stone-700 dark:text-stone-300 dark:ring-stone-600">
          {readOnly
            ? <><Eye className="h-3 w-3" /> {t("settings.readOnly")}</>
            : <><ShieldCheck className="h-3 w-3" /> {t("settings.scopes.adminBadge")}</>}
        </span>
      </div>

      {readOnly && (
        <div className="rounded-md bg-stone-100 p-3 text-sm text-stone-600 ring-1 ring-stone-200 dark:bg-stone-700 dark:text-stone-300 dark:ring-stone-600">
          {t("settings.readOnlyHint")}
        </div>
      )}

      <Section title={t("settings.aiFeature.title")} subtitle={t("settings.aiFeature.subtitle")}>
        <label className="flex cursor-pointer items-start gap-3 rounded-md border border-stone-200 px-3 py-2 hover:bg-cream dark:border-stone-700 dark:hover:bg-stone-700/40">
          <input
            type="checkbox"
            className="mt-0.5 h-3.5 w-3.5 cursor-pointer accent-stone-700 dark:accent-stone-300"
            disabled={lock("aiEnabled")}
            checked={form.aiEnabled}
            onChange={(e) => update("aiEnabled", e.target.checked)}
          />
          <div className="min-w-0">
            <div className="text-sm font-medium">{t("settings.aiFeature.toggle")}</div>
            <div className="text-xs text-stone-500 dark:text-stone-400">{t("settings.aiFeature.toggleHint")}</div>
            {env.aiEnabled && <EnvBadge label={t("settings.envManaged")} />}
          </div>
        </label>
      </Section>

      <Section title={t("settings.llm.title")} subtitle={t("settings.llm.subtitle")}>
        <div className="grid grid-cols-1 gap-4 sm:grid-cols-2">
          <Field
            label={t("settings.llm.apiKey")}
            hint={envHint("apiKey")
              ?? (form.hasApiKey ? t("settings.llm.apiKeySet") : t("settings.llm.apiKeyRequired"))}
          >
            <input className="input" type="password" autoComplete="off"
              placeholder={form.hasApiKey ? "•••••••••••••" : "sk-…"}
              disabled={lock("apiKey")}
              value={apiKey} onChange={(e) => setApiKey(e.target.value)} />
          </Field>
          <Field label={t("settings.llm.baseUrl")} hint={envHint("baseUrl") ?? t("settings.llm.baseUrlHint")}>
            <input className="input" placeholder="https://api.openai.com/v1"
              disabled={lock("baseUrl")}
              value={form.baseUrl} onChange={(e) => update("baseUrl", e.target.value)} />
          </Field>
          <Field label={t("settings.llm.model")} hint={envHint("model") ?? t("settings.llm.modelHint")}>
            <input className="input" placeholder="gpt-4o-mini"
              disabled={lock("model")}
              value={form.model} onChange={(e) => update("model", e.target.value)} />
          </Field>
          <Field label={t("settings.scanning.parallelRequests")} hint={envHint("maxWorkers")}>
            <input className="input" type="number" min={1} max={10} step={1}
              disabled={lock("maxWorkers")}
              value={form.maxWorkers}
              onChange={(e) => update("maxWorkers", Math.min(10, Math.max(1, +e.target.value || 1)))} />
          </Field>
        </div>
      </Section>

      <Section title={t("settings.prompt.title")} subtitle={t("settings.prompt.subtitle")}>
        <div className="mb-4 overflow-hidden rounded-md border border-stone-200 dark:border-stone-700">
          <div className="flex items-center gap-2 border-b border-stone-200 bg-stone-50 px-3 py-2 dark:border-stone-700 dark:bg-stone-700/40">
            <Lock className="h-3.5 w-3.5 text-stone-400" />
            <span className="text-xs font-medium text-stone-600 dark:text-stone-400">
              {t("settings.prompt.formatTitle")}
            </span>
          </div>
          <pre className="max-h-48 overflow-y-auto p-3 font-mono text-xs leading-relaxed text-stone-500 dark:text-stone-400 whitespace-pre-wrap">
            {form.defaultSystemPrompt}
          </pre>
        </div>
        <div className="space-y-2">
          <label className="label">{t("settings.prompt.additionalTitle")}</label>
          <textarea
            className="input font-mono text-xs"
            rows={5}
            placeholder={t("settings.prompt.additionalPlaceholder")}
            disabled={lock("systemPrompt")}
            value={form.systemPrompt}
            onChange={(e) => update("systemPrompt", e.target.value)}
          />
          <p className="text-xs text-stone-500 dark:text-stone-400">
            {env.systemPrompt
              ? t("settings.envManaged")
              : (form.systemPrompt.trim()
                  ? t("settings.prompt.hasAdditions")
                  : t("settings.prompt.noAdditions"))}
          </p>
        </div>
      </Section>

      <Section title={t("settings.dropFolder.title")} subtitle={t("settings.dropFolder.subtitle")}>
        <Field label={t("settings.dropFolder.label")} hint={envHint("dropFolder") ?? t("settings.dropFolder.hint")}>
          <input
            className="input font-mono"
            placeholder={t("settings.dropFolder.placeholder")}
            disabled={lock("dropFolder")}
            value={form.dropFolder}
            onChange={(e) => update("dropFolder", e.target.value)}
          />
        </Field>
      </Section>

      {!readOnly && (
        <div className="flex justify-end">
          <button className="btn-primary" onClick={() => save.mutate()} disabled={save.isPending}>
            <Save className="h-4 w-4" /> {t("settings.saveApp")}
          </button>
        </div>
      )}
    </div>
  );
}

function Section({ title, subtitle, children }: { title: string; subtitle?: string; children: React.ReactNode }) {
  return (
    <section className="card p-5 sm:p-6">
      <div className="mb-5 border-b border-stone-200/70 pb-4 dark:border-stone-700">
        <h3 className="text-sm font-semibold text-stone-900 dark:text-stone-50">{title}</h3>
        {subtitle && <p className="mt-1 text-sm text-stone-500 dark:text-stone-400">{subtitle}</p>}
      </div>
      {children}
    </section>
  );
}

function Field({ label, hint, children }: { label: string; hint?: string; children: React.ReactNode }) {
  return (
    <div>
      <label className="label">{label}</label>
      {children}
      {hint && <p className="mt-1 text-xs text-stone-500 dark:text-stone-400">{hint}</p>}
    </div>
  );
}

/** Small inline pill that flags an env-pinned setting next to the input. */
function EnvBadge({ label }: { label: string }) {
  return (
    <span className="mt-1 inline-flex items-center gap-1 rounded bg-stone-100 px-1.5 py-0.5 text-[10px] font-medium uppercase tracking-wide text-stone-600 dark:bg-stone-700 dark:text-stone-300">
      <Lock className="h-3 w-3" /> {label}
    </span>
  );
}
