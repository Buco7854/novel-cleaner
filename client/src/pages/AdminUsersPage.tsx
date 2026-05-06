import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { format } from "../utils/date";
import { KeyRound, Plus, ShieldCheck, Trash2, UserX } from "lucide-react";
import { useState } from "react";
import { useTranslation } from "react-i18next";
import {
  AdminUser, createAdminUser, deleteAdminUser, listAdminUsers, setUserPassword, updateAdminUser,
} from "../api/users";
import { Modal } from "../components/Modal";
import { useAuth } from "../contexts/AuthContext";
import { useToast } from "../contexts/ToastContext";

export function AdminUsersPage() {
  const { t } = useTranslation();
  const { user: me } = useAuth();
  const toast = useToast();
  const qc = useQueryClient();
  const list = useQuery({ queryKey: ["admin-users"], queryFn: listAdminUsers });
  const [createOpen, setCreateOpen] = useState(false);
  const [pwdFor, setPwdFor] = useState<AdminUser | null>(null);

  const refresh = () => qc.invalidateQueries({ queryKey: ["admin-users"] });

  const remove = useMutation({
    mutationFn: deleteAdminUser, onSuccess: refresh,
    onError: (e) => toast.error("Delete failed", e instanceof Error ? e.message : ""),
  });
  const toggleAdmin = useMutation({
    mutationFn: ({ id, isAdmin }: { id: string; isAdmin: boolean }) => updateAdminUser(id, { isAdmin }),
    onSuccess: refresh,
  });
  const toggleDisabled = useMutation({
    mutationFn: ({ id, isDisabled }: { id: string; isDisabled: boolean }) => updateAdminUser(id, { isDisabled }),
    onSuccess: refresh,
  });

  return (
    <div className="space-y-8">
      <div className="flex flex-wrap items-end justify-between gap-4">
        <div className="min-w-0">
          <h1 className="page-title">{t("users.title")}</h1>
          <p className="page-subtitle">{t("users.subtitle")}</p>
        </div>
        <button className="btn-primary shrink-0" onClick={() => setCreateOpen(true)}>
          <Plus className="h-4 w-4" /> {t("users.new")}
        </button>
      </div>

      <div className="card overflow-hidden">
        {/* Desktop header */}
        <div className="hidden md:grid grid-cols-[1.4fr_140px_140px_140px_120px_140px] items-center gap-4 border-b border-stone-200 bg-stone-50/70 px-5 py-2.5 text-[11px] font-medium uppercase tracking-wider text-stone-500 dark:border-stone-800 dark:bg-stone-900/50 dark:text-stone-400">
          <div>{t("users.col.user")}</div>
          <div>{t("users.col.roles")}</div>
          <div>{t("users.col.provider")}</div>
          <div>{t("users.col.lastLogin")}</div>
          <div>{t("users.col.status")}</div>
          <div className="text-right">{t("users.col.actions")}</div>
        </div>

        {list.data?.length === 0 && (
          <div className="grid place-items-center px-6 py-12 text-sm text-stone-500 dark:text-stone-400">—</div>
        )}

        {list.data?.map((u) => {
          const isSelf = u.id === me?.id;
          const isAdmin = u.roles.includes("Admin");
          const statusPill = (
            <span className={u.isDisabled
              ? "pill bg-rose-50 text-rose-700 ring-rose-200 dark:bg-rose-500/10 dark:text-rose-300 dark:ring-rose-500/30"
              : "pill bg-emerald-50 text-emerald-700 ring-emerald-200 dark:bg-emerald-500/10 dark:text-emerald-300 dark:ring-emerald-500/30"
            }>{u.isDisabled ? t("users.disabled") : t("users.active")}</span>
          );
          const rolePills = u.roles.map((r) => (
            <span key={r} className="pill bg-stone-100 text-stone-700 ring-stone-200 dark:bg-stone-800 dark:text-stone-300 dark:ring-stone-700">
              {r}
            </span>
          ));
          const actions = (
            <>
              <button
                className="btn-ghost px-2 py-1.5"
                title={isAdmin ? t("users.demote") : t("users.promote")}
                disabled={isSelf && isAdmin}
                onClick={() => toggleAdmin.mutate({ id: u.id, isAdmin: !isAdmin })}
              >
                <ShieldCheck className="h-4 w-4" />
              </button>
              <button
                className="btn-ghost px-2 py-1.5"
                title={t("users.resetPwd")}
                disabled={u.provider !== "local"}
                onClick={() => setPwdFor(u)}
              >
                <KeyRound className="h-4 w-4" />
              </button>
              <button
                className="btn-ghost px-2 py-1.5"
                title={u.isDisabled ? t("users.enable") : t("users.disable")}
                disabled={isSelf}
                onClick={() => toggleDisabled.mutate({ id: u.id, isDisabled: !u.isDisabled })}
              >
                <UserX className="h-4 w-4" />
              </button>
              <button
                className="btn-ghost px-2 py-1.5 text-rose-600"
                title={t("users.delete")}
                disabled={isSelf}
                onClick={() => confirm(t("users.deleteConfirm", { email: u.email })) && remove.mutate(u.id)}
              >
                <Trash2 className="h-4 w-4" />
              </button>
            </>
          );

          return (
            <div key={u.id} className="border-b border-stone-200 last:border-0 dark:border-stone-800">
              {/* Mobile card */}
              <div className="flex flex-col gap-3 p-4 md:hidden">
                <div className="flex items-start justify-between gap-3">
                  <div className="min-w-0 flex-1">
                    <div className="truncate text-sm font-medium">{u.displayName ?? u.email}</div>
                    <div className="truncate text-xs text-stone-500 dark:text-stone-400">{u.email}</div>
                  </div>
                  {statusPill}
                </div>
                <div className="flex flex-wrap items-center gap-1.5">
                  {rolePills}
                  <span className="pill bg-stone-100 text-stone-600 ring-stone-200 dark:bg-stone-800 dark:text-stone-400 dark:ring-stone-700">
                    {u.provider}
                  </span>
                </div>
                <div className="flex items-center justify-between border-t border-stone-100 pt-2 dark:border-stone-800">
                  <span className="text-xs text-stone-500 dark:text-stone-400">{format(u.lastLoginAt)}</span>
                  <div className="flex items-center gap-0.5">{actions}</div>
                </div>
              </div>

              {/* Desktop row */}
              <div className="hidden md:grid grid-cols-[1.4fr_140px_140px_140px_120px_140px] items-center gap-4 px-5 py-3.5">
                <div className="min-w-0">
                  <div className="truncate text-sm font-medium">{u.displayName ?? u.email}</div>
                  <div className="truncate text-xs text-stone-500 dark:text-stone-400">{u.email}</div>
                </div>
                <div className="flex flex-wrap gap-1">{rolePills}</div>
                <div className="text-xs text-stone-500 dark:text-stone-400">{u.provider}</div>
                <div className="text-xs text-stone-500 dark:text-stone-400">{format(u.lastLoginAt)}</div>
                <div>{statusPill}</div>
                <div className="flex items-center justify-end gap-0.5">{actions}</div>
              </div>
            </div>
          );
        })}
      </div>

      <CreateUserModal open={createOpen} onClose={() => setCreateOpen(false)} onCreated={refresh} />
      <PasswordModal user={pwdFor} onClose={() => setPwdFor(null)} />
    </div>
  );
}

function CreateUserModal({ open, onClose, onCreated }: { open: boolean; onClose: () => void; onCreated: () => void }) {
  const { t } = useTranslation();
  const toast = useToast();
  const [email, setEmail] = useState("");
  const [displayName, setDisplayName] = useState("");
  const [password, setPassword] = useState("");
  const [isAdmin, setIsAdmin] = useState(false);
  const m = useMutation({
    mutationFn: () => createAdminUser({ email, password, displayName, isAdmin }),
    onSuccess: () => {
      onCreated(); onClose();
      setEmail(""); setDisplayName(""); setPassword(""); setIsAdmin(false);
      toast.success(t("users.create.success"));
    },
    onError: (e) => toast.error("Could not create", e instanceof Error ? e.message : ""),
  });
  return (
    <Modal open={open} onClose={onClose} title={t("users.create.title")}>
      <div className="space-y-3">
        <div><label className="label">{t("users.create.email")}</label>
          <input className="input" value={email} onChange={(e) => setEmail(e.target.value)} /></div>
        <div><label className="label">{t("users.create.displayName")}</label>
          <input className="input" value={displayName} onChange={(e) => setDisplayName(e.target.value)} /></div>
        <div><label className="label">{t("users.create.password")}</label>
          <input className="input" type="password" value={password} onChange={(e) => setPassword(e.target.value)} /></div>
        <label className="flex items-center gap-2 text-sm">
          <input type="checkbox" checked={isAdmin} onChange={(e) => setIsAdmin(e.target.checked)} />
          {t("users.create.isAdmin")}
        </label>
        <div className="flex justify-end gap-2 pt-2">
          <button className="btn-secondary" onClick={onClose}>{t("users.create.cancel")}</button>
          <button className="btn-primary" disabled={!email || !password || m.isPending}
            onClick={() => m.mutate()}>{t("users.create.submit")}</button>
        </div>
      </div>
    </Modal>
  );
}

function PasswordModal({ user, onClose }: { user: AdminUser | null; onClose: () => void }) {
  const { t } = useTranslation();
  const toast = useToast();
  const [pwd, setPwd] = useState("");
  const m = useMutation({
    mutationFn: () => setUserPassword(user!.id, pwd),
    onSuccess: () => { toast.success(t("users.password.success")); setPwd(""); onClose(); },
    onError: (e) => toast.error("Could not set password", e instanceof Error ? e.message : ""),
  });
  return (
    <Modal open={!!user} onClose={onClose} title={user ? t("users.password.title", { email: user.email }) : ""}>
      <div className="space-y-3">
        <div><label className="label">{t("users.password.label")}</label>
          <input className="input" type="password" value={pwd} onChange={(e) => setPwd(e.target.value)} /></div>
        <div className="flex justify-end gap-2 pt-2">
          <button className="btn-secondary" onClick={onClose}>{t("users.password.cancel")}</button>
          <button className="btn-primary" disabled={!pwd || m.isPending}
            onClick={() => m.mutate()}>{t("users.password.submit")}</button>
        </div>
      </div>
    </Modal>
  );
}
