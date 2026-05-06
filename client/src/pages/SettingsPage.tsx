import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { Eye, Lock, Plus, Save, ShieldCheck, User as UserIcon, X } from "lucide-react";
import { useEffect, useState } from "react";
import { useTranslation } from "react-i18next";
import {
  AppSettings, fetchSettings, saveAppSettings, saveUserSettings, UserSettings,
} from "../api/settings";
import { Select } from "../components/Select";
import { useToast } from "../contexts/ToastContext";

export function SettingsPage() {
  const { t } = useTranslation();
  const toast = useToast();
  const qc = useQueryClient();
  const q = useQuery({ queryKey: ["settings"], queryFn: fetchSettings });

  const [appForm, setAppForm] = useState<AppSettings | null>(null);
  const [userForm, setUserForm] = useState<UserSettings | null>(null);
  const [apiKey, setApiKey] = useState("");
  const [pattern, setPattern] = useState("");

  useEffect(() => {
    if (q.data) {
      if (!appForm)  setAppForm({ ...q.data.app });
      if (!userForm) setUserForm({ ...q.data.user });
    }
  }, [q.data, appForm, userForm]);

  const saveApp = useMutation({
    mutationFn: () => saveAppSettings({
      apiKey: apiKey || undefined,
      baseUrl: appForm?.baseUrl ?? "",
      model: appForm?.model ?? "",
      maxWorkers: appForm?.maxWorkers ?? 3,
      systemPrompt: appForm?.systemPrompt ?? "",
      dropFolder: appForm?.dropFolder ?? "",
    }),
    onSuccess: () => { toast.success(t("settings.saved")); setApiKey(""); qc.invalidateQueries({ queryKey: ["settings"] }); },
    onError: (e) => toast.error("Save failed", e instanceof Error ? e.message : ""),
  });

  const saveUser = useMutation({
    mutationFn: () => saveUserSettings({
      patterns: userForm?.patterns ?? [],
      scanAll: userForm?.scanAll ?? false,
      contextWindow: userForm?.contextWindow ?? 1000,
    }),
    onSuccess: () => { toast.success(t("settings.saved")); qc.invalidateQueries({ queryKey: ["settings"] }); },
    onError: (e) => toast.error("Save failed", e instanceof Error ? e.message : ""),
  });

  if (!appForm || !userForm) return <div className="py-10 text-center text-sm text-stone-500">{t("app.loading")}</div>;

  const appReadOnly = !appForm.canEdit;

  function updateApp<K extends keyof AppSettings>(k: K, v: AppSettings[K]) {
    if (appReadOnly) return;
    setAppForm((f) => f ? { ...f, [k]: v } : f);
  }
  function updateUser<K extends keyof UserSettings>(k: K, v: UserSettings[K]) {
    setUserForm((f) => f ? { ...f, [k]: v } : f);
  }

  const scanOptions = [
    { value: "patterns", label: t("settings.scanning.patternDetection") },
    { value: "all",      label: t("settings.scanning.fullPage") },
  ] as const;

  return (
    <div className="mx-auto max-w-3xl space-y-10">
      <h1 className="page-title">{t("settings.title")}</h1>

      {/* ============================ Global / admin section ============================ */}
      <div className="space-y-6">
        <ScopeHeader
          icon={ShieldCheck}
          title={t("settings.scopes.app")}
          subtitle={t("settings.scopes.appHint")}
          badge={appReadOnly
            ? { Icon: Eye, label: t("settings.readOnly") }
            : { Icon: ShieldCheck, label: t("settings.scopes.adminBadge") }
          }
        />
        {appReadOnly && (
          <div className="rounded-md bg-stone-100 p-3 text-sm text-stone-600 ring-1 ring-stone-200 dark:bg-stone-800 dark:text-stone-300 dark:ring-stone-700">
            {t("settings.readOnlyHint")}
          </div>
        )}

        <Section title={t("settings.llm.title")} subtitle={t("settings.llm.subtitle")}>
          <div className="grid grid-cols-1 gap-4 sm:grid-cols-2">
            <Field label={t("settings.llm.apiKey")} hint={appForm.hasApiKey ? t("settings.llm.apiKeySet") : t("settings.llm.apiKeyRequired")}>
              <input className="input" type="password" autoComplete="off"
                placeholder={appForm.hasApiKey ? "•••••••••••••" : "sk-…"}
                disabled={appReadOnly}
                value={apiKey} onChange={(e) => setApiKey(e.target.value)} />
            </Field>
            <Field label={t("settings.llm.baseUrl")} hint={t("settings.llm.baseUrlHint")}>
              <input className="input" placeholder="https://api.openai.com/v1"
                disabled={appReadOnly}
                value={appForm.baseUrl} onChange={(e) => updateApp("baseUrl", e.target.value)} />
            </Field>
            <Field label={t("settings.llm.model")} hint={t("settings.llm.modelHint")}>
              <input className="input" placeholder="gpt-4o-mini"
                disabled={appReadOnly}
                value={appForm.model} onChange={(e) => updateApp("model", e.target.value)} />
            </Field>
            <Field label={t("settings.scanning.parallelRequests")}>
              <input className="input" type="number" min={1} max={10} step={1}
                disabled={appReadOnly}
                value={appForm.maxWorkers}
                onChange={(e) => updateApp("maxWorkers", Math.min(10, Math.max(1, +e.target.value || 1)))} />
            </Field>
          </div>
        </Section>

        <Section title={t("settings.prompt.title")} subtitle={t("settings.prompt.subtitle")}>
          <div className="mb-4 overflow-hidden rounded-md border border-stone-200 dark:border-stone-700">
            <div className="flex items-center gap-2 border-b border-stone-200 bg-stone-50 px-3 py-2 dark:border-stone-700 dark:bg-stone-800/60">
              <Lock className="h-3.5 w-3.5 text-stone-400" />
              <span className="text-xs font-medium text-stone-600 dark:text-stone-400">
                {t("settings.prompt.formatTitle")}
              </span>
            </div>
            <pre className="max-h-48 overflow-y-auto p-3 font-mono text-xs leading-relaxed text-stone-500 dark:text-stone-400 whitespace-pre-wrap">
              {appForm.defaultSystemPrompt}
            </pre>
          </div>
          <div className="space-y-2">
            <label className="label">{t("settings.prompt.additionalTitle")}</label>
            <textarea
              className="input font-mono text-xs"
              rows={5}
              placeholder={t("settings.prompt.additionalPlaceholder")}
              disabled={appReadOnly}
              value={appForm.systemPrompt}
              onChange={(e) => updateApp("systemPrompt", e.target.value)}
            />
            <p className="text-xs text-stone-500 dark:text-stone-400">
              {appForm.systemPrompt.trim()
                ? t("settings.prompt.hasAdditions")
                : t("settings.prompt.noAdditions")}
            </p>
          </div>
        </Section>

        <Section title={t("settings.dropFolder.title")} subtitle={t("settings.dropFolder.subtitle")}>
          <Field label={t("settings.dropFolder.label")} hint={t("settings.dropFolder.hint")}>
            <input
              className="input font-mono"
              placeholder={t("settings.dropFolder.placeholder")}
              disabled={appReadOnly}
              value={appForm.dropFolder}
              onChange={(e) => updateApp("dropFolder", e.target.value)}
            />
          </Field>
        </Section>

        {!appReadOnly && (
          <div className="flex justify-end">
            <button className="btn-primary" onClick={() => saveApp.mutate()} disabled={saveApp.isPending}>
              <Save className="h-4 w-4" /> {t("settings.saveApp")}
            </button>
          </div>
        )}
      </div>

      {/* ============================ Personal section ============================ */}
      <div className="space-y-6 border-t border-stone-200 pt-10 dark:border-stone-800">
        <ScopeHeader
          icon={UserIcon}
          title={t("settings.scopes.user")}
          subtitle={t("settings.scopes.userHint")}
          badge={{ Icon: UserIcon, label: t("settings.scopes.userBadge") }}
        />

        <Section title={t("settings.scanning.title")} subtitle={t("settings.scanning.subtitle")}>
          <div className="grid grid-cols-1 gap-4 sm:grid-cols-2">
            <Field label={t("settings.scanning.defaultMode")}>
              <Select
                value={userForm.scanAll ? "all" : "patterns"}
                onChange={(v) => updateUser("scanAll", v === "all")}
                options={scanOptions as unknown as { value: string; label: string }[]}
              />
            </Field>
            <Field
              label={t("settings.scanning.contextWindow")}
              hint={t("settings.scanning.contextWindowHint")}
            >
              <input className="input" type="number" min={100} max={5000} step={100}
                value={userForm.contextWindow}
                onChange={(e) => updateUser("contextWindow", Math.max(100, +e.target.value || 1000))} />
            </Field>
          </div>
        </Section>

        <Section title={t("settings.patterns.title")} subtitle={t("settings.patterns.subtitle")}>
          <div className="space-y-2">
            {userForm.patterns.length === 0 && (
              <div className="text-xs text-stone-500 dark:text-stone-400">{t("settings.patterns.none")}</div>
            )}
            {userForm.patterns.map((p, i) => (
              <div key={i} className="flex items-center justify-between rounded-md bg-stone-50 px-3 py-1.5 text-sm font-mono dark:bg-stone-800">
                <span className="truncate">{p}</span>
                <button className="btn-ghost" onClick={() => updateUser("patterns", userForm.patterns.filter((_, j) => j !== i))}>
                  <X className="h-4 w-4" />
                </button>
              </div>
            ))}
            <div className="flex gap-2">
              <input className="input font-mono" placeholder="\\b[0-9a-f]{8}-[0-9a-f]{4}-…"
                value={pattern} onChange={(e) => setPattern(e.target.value)}
                onKeyDown={(e) => { if (e.key === "Enter" && pattern) { updateUser("patterns", [...userForm.patterns, pattern]); setPattern(""); }}} />
              <button className="btn-secondary shrink-0" disabled={!pattern}
                onClick={() => { updateUser("patterns", [...userForm.patterns, pattern]); setPattern(""); }}>
                <Plus className="h-4 w-4" /> {t("settings.patterns.add")}
              </button>
            </div>
          </div>
        </Section>

        <div className="flex justify-end">
          <button className="btn-primary" onClick={() => saveUser.mutate()} disabled={saveUser.isPending}>
            <Save className="h-4 w-4" /> {t("settings.saveUser")}
          </button>
        </div>
      </div>
    </div>
  );
}

function ScopeHeader({
  icon: Icon, title, subtitle, badge,
}: {
  icon: typeof ShieldCheck;
  title: string;
  subtitle: string;
  badge: { Icon: typeof ShieldCheck; label: string };
}) {
  const Badge = badge.Icon;
  return (
    <div className="flex items-start justify-between gap-3">
      <div className="flex min-w-0 items-start gap-3">
        <div className="grid h-8 w-8 shrink-0 place-items-center rounded-md bg-stone-100 text-stone-600 dark:bg-stone-800 dark:text-stone-300">
          <Icon className="h-4 w-4" />
        </div>
        <div className="min-w-0">
          <h2 className="section-title">{title}</h2>
          <p className="mt-0.5 text-sm text-stone-500 dark:text-stone-400">{subtitle}</p>
        </div>
      </div>
      <span className="pill bg-stone-100 text-stone-600 ring-stone-200 shrink-0 dark:bg-stone-800 dark:text-stone-300 dark:ring-stone-700">
        <Badge className="h-3 w-3" /> {badge.label}
      </span>
    </div>
  );
}

function Section({ title, subtitle, children }: { title: string; subtitle?: string; children: React.ReactNode }) {
  return (
    <section className="card p-5 sm:p-6">
      <div className="mb-5 border-b border-stone-200/70 pb-4 dark:border-stone-800">
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
