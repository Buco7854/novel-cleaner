import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { ExternalLink, Library, Pencil, Plus, Trash2 } from "lucide-react";
import { useState } from "react";
import { useTranslation } from "react-i18next";
import { Link } from "react-router-dom";
import {
  createSource, deleteSource, listSources, OpdsSourceSummary, updateSource,
} from "../api/opds";
import { Modal } from "../components/Modal";
import { useConfirm } from "../contexts/ConfirmContext";
import { useToast } from "../contexts/ToastContext";

export function SourcesPage() {
  const { t } = useTranslation();
  const toast = useToast();
  const qc = useQueryClient();
  const confirm = useConfirm();
  const list = useQuery({ queryKey: ["sources"], queryFn: listSources });
  const [editing, setEditing] = useState<OpdsSourceSummary | "new" | null>(null);

  const remove = useMutation({
    mutationFn: deleteSource,
    onSuccess: () => qc.invalidateQueries({ queryKey: ["sources"] }),
    onError: (e) => toast.error("Delete failed", e instanceof Error ? e.message : ""),
  });

  return (
    <div className="space-y-8">
      <div className="flex flex-wrap items-end justify-between gap-4">
        <div className="min-w-0">
          <h1 className="page-title">{t("sources.title")}</h1>
          <p className="page-subtitle">{t("sources.subtitle")}</p>
        </div>
        <button className="btn-primary shrink-0" onClick={() => setEditing("new")}>
          <Plus className="h-4 w-4" /> {t("sources.add")}
        </button>
      </div>

      {list.data?.length === 0 ? (
        <div className="card grid place-items-center gap-2 p-12 text-center text-sm text-stone-500 dark:text-stone-400">
          <Library className="h-7 w-7 text-stone-400" />
          <div>{t("sources.none")}</div>
          <div className="text-xs">{t("sources.noneHint", { url: "https://standardebooks.org/opds" })}</div>
        </div>
      ) : (
        <div className="grid grid-cols-1 gap-4 sm:grid-cols-2 lg:grid-cols-3">
          {list.data?.map((s) => (
            <div key={s.id} className="card flex flex-col gap-3 p-5">
              <div className="flex items-start justify-between gap-2">
                <div className="min-w-0 flex-1">
                  <div className="truncate text-sm font-semibold">{s.name}</div>
                  <div className="truncate text-xs text-stone-500 dark:text-stone-400" title={s.url}>{s.url}</div>
                </div>
                <div className="flex shrink-0 items-center gap-0.5">
                  <button className="btn-ghost px-1.5 py-1.5" onClick={() => setEditing(s)} aria-label="Edit"><Pencil className="h-4 w-4" /></button>
                  <button className="btn-ghost px-1.5 py-1.5 text-rose-600"
                    onClick={async () => {
                      const ok = await confirm({
                        title: t("sources.deleteTitle"),
                        body: t("sources.deleteConfirm", { name: s.name }),
                        confirmLabel: t("sources.deleteConfirmBtn"),
                        cancelLabel: t("sources.deleteCancelBtn"),
                        danger: true,
                      });
                      if (ok) remove.mutate(s.id);
                    }}
                    aria-label="Delete">
                    <Trash2 className="h-4 w-4" />
                  </button>
                </div>
              </div>
              <div className="flex flex-wrap items-center gap-1.5">
                {s.hasCredentials && (
                  <span className="pill bg-stone-100 text-stone-600 ring-stone-200 dark:bg-stone-800 dark:text-stone-300 dark:ring-stone-700">
                    {t("sources.authenticated")}
                  </span>
                )}
              </div>
              <Link to={`/sources/${s.id}/browse`} className="btn-secondary mt-auto w-full">
                <ExternalLink className="h-4 w-4" /> {t("sources.browse")}
              </Link>
            </div>
          ))}
        </div>
      )}

      <SourceModal target={editing} onClose={() => setEditing(null)} onSaved={() => qc.invalidateQueries({ queryKey: ["sources"] })} />
    </div>
  );
}

function SourceModal({ target, onClose, onSaved }: { target: OpdsSourceSummary | "new" | null; onClose: () => void; onSaved: () => void }) {
  const { t } = useTranslation();
  const toast = useToast();
  const isNew = target === "new";
  const data = target && target !== "new" ? target : null;

  const [name, setName] = useState(data?.name ?? "");
  const [url, setUrl] = useState(data?.url ?? "");
  const [username, setUsername] = useState("");
  const [password, setPassword] = useState("");

  const save = useMutation({
    mutationFn: () => isNew
      ? createSource({ name, url, username, password })
      : updateSource(data!.id, { name, url, username, password }),
    onSuccess: () => { toast.success(isNew ? t("sources.modal.add") : t("sources.modal.edit")); onSaved(); onClose(); },
    onError: (e) => toast.error("Save failed", e instanceof Error ? e.message : ""),
  });

  return (
    <Modal open={target !== null} onClose={onClose} title={isNew ? t("sources.modal.add") : t("sources.modal.edit")}>
      <div className="space-y-4">
        <div><label className="label">{t("sources.modal.name")}</label>
          <input className="input" value={name} onChange={(e) => setName(e.target.value)} placeholder={t("sources.modal.namePlaceholder")} /></div>
        <div><label className="label">{t("sources.modal.url")}</label>
          <input className="input" value={url} onChange={(e) => setUrl(e.target.value)} placeholder={t("sources.modal.urlPlaceholder")} /></div>
        <div className="grid grid-cols-2 gap-3">
          <div><label className="label">{t("sources.modal.username")}</label>
            <input className="input" value={username} onChange={(e) => setUsername(e.target.value)} /></div>
          <div><label className="label">{t("sources.modal.password")}</label>
            <input className="input" type="password" value={password}
              placeholder={data?.hasCredentials ? "••••• (keep current)" : ""}
              onChange={(e) => setPassword(e.target.value)} /></div>
        </div>

        <div className="flex justify-end gap-2 pt-2">
          <button className="btn-secondary" onClick={onClose}>{t("sources.modal.cancel")}</button>
          <button className="btn-primary" disabled={!name || !url || save.isPending}
            onClick={() => save.mutate()}>{isNew ? t("sources.modal.addBtn") : t("sources.modal.saveBtn")}</button>
        </div>
      </div>
    </Modal>
  );
}

