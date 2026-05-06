import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { Lock, Save } from "lucide-react";
import { useEffect, useState } from "react";
import { useTranslation } from "react-i18next";
import { fetchSettings, saveUserSettings, UserSettings } from "../api/settings";
import { useToast } from "../contexts/ToastContext";

/**
 * Personal-scope settings. Admin's system prompt is shown read-only at the
 * top so the user can see what's already running before they layer their
 * own instructions on top.
 */
export function SettingsPage() {
  const { t } = useTranslation();
  const toast = useToast();
  const qc = useQueryClient();
  const q = useQuery({ queryKey: ["settings"], queryFn: fetchSettings });

  const [form, setForm] = useState<UserSettings | null>(null);

  useEffect(() => {
    if (q.data && !form) setForm({ ...q.data.user });
  }, [q.data, form]);

  const save = useMutation({
    mutationFn: () => saveUserSettings({
      systemPrompt: form?.systemPrompt ?? "",
    }),
    onSuccess: () => {
      toast.success(t("settings.saved"));
      qc.invalidateQueries({ queryKey: ["settings"] });
    },
    onError: (e) => toast.error("Save failed", e instanceof Error ? e.message : ""),
  });

  if (!form || !q.data) return <div className="py-10 text-center text-sm text-stone-500">{t("app.loading")}</div>;

  function update<K extends keyof UserSettings>(k: K, v: UserSettings[K]) {
    setForm((f) => f ? { ...f, [k]: v } : f);
  }

  const adminPrompt = q.data.app.systemPrompt;
  const defaultPrompt = q.data.app.defaultSystemPrompt;

  return (
    <div className="mx-auto max-w-3xl space-y-8">
      <div>
        <h1 className="page-title">{t("settings.title")}</h1>
        <p className="page-subtitle">{t("settings.scopes.userHint")}</p>
      </div>

      <Section title={t("settings.prompt.title")} subtitle={t("settings.userPrompt.subtitle")}>
        {/* Locked admin chunks: the global format rules + the admin's
            extra instructions. The user sees both so they can layer
            their own on top without redundancy. */}
        <div className="mb-3 overflow-hidden rounded-md border border-stone-200 dark:border-stone-700">
          <div className="flex items-center gap-2 border-b border-stone-200 bg-stone-50 px-3 py-2 dark:border-stone-700 dark:bg-stone-700/40">
            <Lock className="h-3.5 w-3.5 text-stone-400" />
            <span className="text-xs font-medium text-stone-600 dark:text-stone-400">
              {t("settings.prompt.formatTitle")}
            </span>
          </div>
          <pre className="max-h-40 overflow-y-auto whitespace-pre-wrap p-3 font-mono text-xs leading-relaxed text-stone-500 dark:text-stone-400">
            {defaultPrompt}
          </pre>
        </div>
        {adminPrompt.trim().length > 0 && (
          <div className="mb-4 overflow-hidden rounded-md border border-stone-200 dark:border-stone-700">
            <div className="flex items-center gap-2 border-b border-stone-200 bg-stone-50 px-3 py-2 dark:border-stone-700 dark:bg-stone-700/40">
              <Lock className="h-3.5 w-3.5 text-stone-400" />
              <span className="text-xs font-medium text-stone-600 dark:text-stone-400">
                {t("settings.userPrompt.adminAdditionsTitle")}
              </span>
            </div>
            <pre className="max-h-40 overflow-y-auto whitespace-pre-wrap p-3 font-mono text-xs leading-relaxed text-stone-500 dark:text-stone-400">
              {adminPrompt}
            </pre>
          </div>
        )}

        <div className="space-y-2">
          <label className="label">{t("settings.userPrompt.title")}</label>
          <textarea
            className="input font-mono text-xs"
            rows={6}
            placeholder={t("settings.userPrompt.placeholder")}
            value={form.systemPrompt}
            onChange={(e) => update("systemPrompt", e.target.value)}
          />
          <p className="text-xs text-stone-500 dark:text-stone-400">
            {t("settings.userPrompt.hint")}
          </p>
        </div>
      </Section>

      <div className="flex justify-end">
        <button className="btn-primary" onClick={() => save.mutate()} disabled={save.isPending}>
          <Save className="h-4 w-4" /> {t("settings.saveUser")}
        </button>
      </div>
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
