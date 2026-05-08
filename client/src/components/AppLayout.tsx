import { Listbox, ListboxButton, ListboxOption, ListboxOptions, Menu, MenuButton, MenuItem, MenuItems, Transition, TransitionChild } from "@headlessui/react";
import {
  Activity, BookOpen, Check, ChevronDown, LayoutDashboard, Library, ListChecks,
  LogOut, Menu as MenuIcon, Monitor, Moon, Settings, Shield, Sun, Users, X,
} from "lucide-react";
import { Fragment, useState } from "react";
import { useTranslation } from "react-i18next";
import { Link, NavLink, Outlet, useLocation, useNavigate } from "react-router-dom";
import { useAuth } from "../contexts/AuthContext";
import { useTheme } from "../contexts/ThemeContext";
import { clsx } from "../utils/clsx";
import i18n from "../i18n";

const LANGS = [
  { value: "en", label: "English" },
  { value: "fr", label: "Français" },
];

export function AppLayout() {
  const { t } = useTranslation();
  const { user, isAdmin, logout } = useAuth();
  const { mode, setMode } = useTheme();
  const nav = useNavigate();
  const location = useLocation();
  const [mobileOpen, setMobileOpen] = useState(false);

  const navItems = [
    { to: "/",          label: t("nav.dashboard"), icon: LayoutDashboard },
    { to: "/books",    label: t("nav.books"),    icon: ListChecks },
    { to: "/sources",   label: t("nav.sources"),   icon: Library },
  ];

  const initials = (user?.displayName ?? user?.email ?? "?").slice(0, 1).toUpperCase();

  const themes = [
    { key: "light"  as const, icon: Sun,     label: t("theme.light") },
    { key: "system" as const, icon: Monitor, label: t("theme.system") },
    { key: "dark"   as const, icon: Moon,    label: t("theme.dark") },
  ];

  const currentTheme = themes.find((th) => th.key === mode) ?? themes[1];

  return (
    <div className="flex min-h-full flex-col">
      <header className="sticky top-0 z-30 border-b border-stone-200/80 bg-paper/95 backdrop-blur dark:border-stone-700/60 dark:bg-stone-900/90">
        <div className="mx-auto flex h-16 w-full max-w-7xl items-center gap-3 px-4 sm:px-6">
          {/* Brand */}
          <Link to="/" className="flex shrink-0 items-center gap-2.5 rounded-md focus-visible:ring-2 focus-visible:ring-stone-900/30 dark:focus-visible:ring-stone-100/30 focus-visible:ring-offset-2 dark:focus-visible:ring-offset-stone-950">
            <BookOpen className="h-5 w-5 text-stone-700 dark:text-stone-300" strokeWidth={1.75} />
            <span className="text-[15px] font-semibold leading-none tracking-tight text-stone-900 dark:text-stone-50">
              {t("app.name")}
            </span>
          </Link>

          {/* Desktop nav */}
          <nav className="ml-6 hidden items-center gap-6 md:flex">
            {navItems.map(({ to, label }) => (
              <NavLink
                key={to}
                to={to}
                end={to === "/"}
                className={({ isActive }) =>
                  clsx(
                    "top-nav-link",
                    isActive
                      ? "text-stone-900 after:bg-stone-900 dark:text-stone-50 dark:after:bg-stone-100"
                      : "text-stone-500 hover:text-stone-900 after:bg-transparent dark:text-stone-400 dark:hover:text-stone-100"
                  )
                }
              >
                {label}
              </NavLink>
            ))}
          </nav>

          {/* Mobile hamburger */}
          <button
            onClick={() => setMobileOpen(true)}
            className="ml-auto rounded-md p-2 text-stone-600 hover:bg-stone-100 md:hidden dark:text-stone-300 dark:hover:bg-stone-800"
            aria-label="Open menu"
          >
            <MenuIcon className="h-5 w-5" />
          </button>

          {/* Right controls (desktop) */}
          <div className="ml-auto hidden items-center gap-1.5 md:flex">
            <LanguageDropdown />
            <ThemeDropdown themes={themes} currentTheme={currentTheme} mode={mode} setMode={setMode} />

            <Menu as="div" className="relative">
              <MenuButton className="flex items-center gap-2 rounded-md px-1.5 py-1.5 hover:bg-stone-100 dark:hover:bg-stone-800">
                <div className="grid h-7 w-7 place-items-center rounded-full bg-stone-200 text-[12px] font-semibold text-stone-700 dark:bg-stone-700 dark:text-stone-200">
                  {initials}
                </div>
                <ChevronDown className="h-3.5 w-3.5 text-stone-400" />
              </MenuButton>
              <Transition as={Fragment}
                enter="transition ease-out duration-100" enterFrom="opacity-0 scale-95" enterTo="opacity-100 scale-100"
                leave="transition ease-in duration-75" leaveFrom="opacity-100 scale-100" leaveTo="opacity-0 scale-95">
                <MenuItems className="absolute right-0 z-50 mt-1.5 w-60 origin-top-right rounded-xl bg-paper p-1.5 shadow-lg ring-1 ring-stone-200 dark:bg-stone-800 dark:ring-stone-700">
                  <div className="px-2 py-2">
                    <div className="truncate text-sm font-medium text-stone-900 dark:text-stone-100">
                      {user?.displayName ?? user?.email}
                    </div>
                    <div className="truncate text-xs text-stone-500 dark:text-stone-400">{user?.email}</div>
                  </div>
                  <div className="my-1 border-t border-stone-200 dark:border-stone-700" />
                  <MenuItem>
                    {({ focus }) => (
                      <Link
                        to="/settings"
                        className={clsx(
                          "flex w-full items-center gap-2 rounded-md px-2 py-2 text-sm text-stone-700 dark:text-stone-200",
                          focus && "bg-stone-100 dark:bg-stone-700",
                        )}
                      >
                        <Settings className="h-4 w-4" /> {t("nav.settings")}
                      </Link>
                    )}
                  </MenuItem>
                  {isAdmin && (
                    <>
                      <div className="px-2 pb-1 pt-2 text-[10px] font-semibold uppercase tracking-wider text-stone-400 dark:text-stone-500">
                        {t("app.admin")}
                      </div>
                      <MenuItem>
                        {({ focus }) => (
                          <Link
                            to="/admin/settings"
                            className={clsx(
                              "flex w-full items-center gap-2 rounded-md px-2 py-2 text-sm text-stone-700 dark:text-stone-200",
                              focus && "bg-stone-100 dark:bg-stone-700",
                            )}
                          >
                            <Shield className="h-4 w-4" /> {t("nav.adminSettings")}
                          </Link>
                        )}
                      </MenuItem>
                      <MenuItem>
                        {({ focus }) => (
                          <Link
                            to="/admin/users"
                            className={clsx(
                              "flex w-full items-center gap-2 rounded-md px-2 py-2 text-sm text-stone-700 dark:text-stone-200",
                              focus && "bg-stone-100 dark:bg-stone-700",
                            )}
                          >
                            <Users className="h-4 w-4" /> {t("nav.users")}
                          </Link>
                        )}
                      </MenuItem>
                      <MenuItem>
                        {({ focus }) => (
                          <Link
                            to="/admin/usage"
                            className={clsx(
                              "flex w-full items-center gap-2 rounded-md px-2 py-2 text-sm text-stone-700 dark:text-stone-200",
                              focus && "bg-stone-100 dark:bg-stone-700",
                            )}
                          >
                            <Activity className="h-4 w-4" /> {t("nav.usage")}
                          </Link>
                        )}
                      </MenuItem>
                      <div className="my-1 border-t border-stone-200 dark:border-stone-700" />
                    </>
                  )}
                  <MenuItem>
                    {({ focus }) => (
                      <button
                        className={clsx(
                          "flex w-full items-center gap-2 rounded-md px-2 py-2 text-sm text-stone-700 dark:text-stone-200",
                          focus && "bg-stone-100 dark:bg-stone-700"
                        )}
                        onClick={async () => { await logout(); nav("/login"); }}
                      >
                        <LogOut className="h-4 w-4" /> {t("app.signOut")}
                      </button>
                    )}
                  </MenuItem>
                </MenuItems>
              </Transition>
            </Menu>
          </div>
        </div>
      </header>

      {/* Mobile drawer */}
      <Transition show={mobileOpen} as={Fragment}>
        <div className="fixed inset-0 z-40 md:hidden">
          <TransitionChild as={Fragment}
            enter="transition-opacity duration-150" enterFrom="opacity-0" enterTo="opacity-100"
            leave="transition-opacity duration-100" leaveFrom="opacity-100" leaveTo="opacity-0">
            <div className="absolute inset-0 bg-stone-950/40" onClick={() => setMobileOpen(false)} />
          </TransitionChild>
          <TransitionChild as={Fragment}
            enter="transition-transform duration-200" enterFrom="-translate-x-full" enterTo="translate-x-0"
            leave="transition-transform duration-150" leaveFrom="translate-x-0" leaveTo="-translate-x-full">
            <aside className="absolute left-0 top-0 flex h-full w-72 flex-col bg-paper shadow-xl dark:bg-stone-900">
              <div className="flex h-16 items-center justify-between border-b border-stone-200 px-4 dark:border-stone-800">
                <Link to="/" onClick={() => setMobileOpen(false)} className="flex items-center gap-2.5">
                  <BookOpen className="h-5 w-5 text-stone-700 dark:text-stone-300" strokeWidth={1.75} />
                  <span className="text-base font-semibold tracking-tight">{t("app.name")}</span>
                </Link>
                <button
                  onClick={() => setMobileOpen(false)}
                  className="rounded-md p-1.5 text-stone-500 hover:bg-stone-100 dark:hover:bg-stone-800"
                  aria-label="Close menu"
                >
                  <X className="h-5 w-5" />
                </button>
              </div>
              <nav className="flex-1 space-y-1.5 overflow-y-auto p-3">
                {navItems.map(({ to, label, icon: Icon }) => (
                  <NavLink
                    key={to}
                    to={to}
                    end={to === "/"}
                    onClick={() => setMobileOpen(false)}
                    className={({ isActive }) =>
                      clsx(
                        "flex items-center gap-3 rounded-md px-3 py-2.5 text-sm font-medium transition-colors",
                        isActive
                          ? "bg-stone-100 text-stone-900 dark:bg-stone-800 dark:text-stone-100"
                          : "text-stone-600 hover:bg-stone-100 hover:text-stone-900 dark:text-stone-300 dark:hover:bg-stone-800 dark:hover:text-stone-100"
                      )
                    }
                  >
                    <Icon className="h-4 w-4 shrink-0" />
                    {label}
                  </NavLink>
                ))}
                <NavLink
                  to="/settings"
                  onClick={() => setMobileOpen(false)}
                  className={({ isActive }) =>
                    clsx(
                      "flex items-center gap-3 rounded-md px-3 py-2.5 text-sm font-medium transition-colors",
                      isActive
                        ? "bg-stone-100 text-stone-900 dark:bg-stone-700 dark:text-stone-100"
                        : "text-stone-600 hover:bg-stone-100 hover:text-stone-900 dark:text-stone-300 dark:hover:bg-stone-700 dark:hover:text-stone-100"
                    )
                  }
                >
                  <Settings className="h-4 w-4 shrink-0" />
                  {t("nav.settings")}
                </NavLink>

                {isAdmin && (
                  <>
                    <div className="mb-4 mt-8 border-t border-stone-200 dark:border-stone-700" />
                    <div className="px-3 pb-2 text-[10px] font-semibold uppercase tracking-wider text-stone-400 dark:text-stone-500">
                      {t("app.admin")}
                    </div>
                    {[
                      { to: "/admin/settings", label: t("nav.adminSettings"), icon: Shield },
                      { to: "/admin/users",    label: t("nav.users"),         icon: Users },
                      { to: "/admin/usage",    label: t("nav.usage"),         icon: Activity },
                    ].map(({ to, label, icon: Icon }) => (
                      <NavLink
                        key={to}
                        to={to}
                        onClick={() => setMobileOpen(false)}
                        className={({ isActive }) =>
                          clsx(
                            "flex items-center gap-3 rounded-md px-3 py-2.5 text-sm font-medium transition-colors",
                            isActive
                              ? "bg-stone-100 text-stone-900 dark:bg-stone-700 dark:text-stone-100"
                              : "text-stone-600 hover:bg-stone-100 hover:text-stone-900 dark:text-stone-300 dark:hover:bg-stone-700 dark:hover:text-stone-100"
                          )
                        }
                      >
                        <Icon className="h-4 w-4 shrink-0" />
                        {label}
                      </NavLink>
                    ))}
                  </>
                )}
              </nav>
              <div className="border-t border-stone-200 p-3 dark:border-stone-700">
                <div className="mb-3 flex items-center gap-2">
                  <LanguageDropdown />
                  <ThemeDropdown themes={themes} currentTheme={currentTheme} mode={mode} setMode={setMode} />
                </div>
                <div className="flex items-center justify-between gap-2">
                  <div className="min-w-0">
                    <div className="truncate text-sm font-medium">{user?.displayName ?? user?.email}</div>
                    <div className="truncate text-xs text-stone-500 dark:text-stone-400">{user?.email}</div>
                  </div>
                  <button
                    className="btn-ghost shrink-0"
                    onClick={async () => { setMobileOpen(false); await logout(); nav("/login"); }}
                    aria-label={t("app.signOut")}
                  >
                    <LogOut className="h-4 w-4" />
                  </button>
                </div>
              </div>
            </aside>
          </TransitionChild>
        </div>
      </Transition>

      <main key={location.pathname} className="mx-auto w-full max-w-7xl flex-1 px-4 py-8 sm:px-6 sm:py-10">
        <Outlet />
      </main>
    </div>
  );
}

function LanguageDropdown() {
  const currentLang = i18n.resolvedLanguage ?? "en";
  return (
    <Listbox value={currentLang} onChange={(lng) => i18n.changeLanguage(lng)}>
      <div className="relative">
        <ListboxButton className="flex items-center gap-1 rounded-md px-2.5 py-1.5 text-xs font-medium uppercase text-stone-600 hover:bg-stone-100 dark:text-stone-300 dark:hover:bg-stone-800">
          {currentLang}
          <ChevronDown className="h-3 w-3 text-stone-400" />
        </ListboxButton>
        <Transition as={Fragment}
          enter="transition ease-out duration-100" enterFrom="opacity-0 scale-95" enterTo="opacity-100 scale-100"
          leave="transition ease-in duration-75" leaveFrom="opacity-100 scale-100" leaveTo="opacity-0 scale-95">
          <ListboxOptions className="absolute right-0 z-50 mt-1.5 w-40 origin-top-right rounded-xl bg-paper p-1.5 shadow-lg ring-1 ring-stone-200 dark:bg-stone-800 dark:ring-stone-700">
            {LANGS.map((l) => (
              <ListboxOption
                key={l.value}
                value={l.value}
                className={({ focus }) =>
                  clsx(
                    "flex cursor-pointer items-center justify-between rounded-md px-2.5 py-1.5 text-sm",
                    focus && "bg-stone-100 dark:bg-stone-800",
                    currentLang === l.value
                      ? "font-medium text-stone-900 dark:text-stone-100"
                      : "text-stone-600 dark:text-stone-300"
                  )
                }
              >
                {l.label}
                {currentLang === l.value && <Check className="h-3.5 w-3.5 text-stone-500 dark:text-stone-400" />}
              </ListboxOption>
            ))}
          </ListboxOptions>
        </Transition>
      </div>
    </Listbox>
  );
}

interface ThemeDropdownProps {
  themes: { key: "light" | "system" | "dark"; icon: typeof Sun; label: string }[];
  currentTheme: { key: "light" | "system" | "dark"; icon: typeof Sun; label: string };
  mode: "light" | "system" | "dark";
  setMode: (m: "light" | "system" | "dark") => void;
}

function ThemeDropdown({ themes, currentTheme, mode, setMode }: ThemeDropdownProps) {
  return (
    <Listbox value={mode} onChange={setMode}>
      <div className="relative">
        <ListboxButton className="flex items-center gap-1.5 rounded-md px-2.5 py-1.5 text-xs text-stone-600 hover:bg-stone-100 dark:text-stone-300 dark:hover:bg-stone-800">
          <currentTheme.icon className="h-3.5 w-3.5" strokeWidth={1.75} />
          <ChevronDown className="h-3 w-3 text-stone-400" />
        </ListboxButton>
        <Transition as={Fragment}
          enter="transition ease-out duration-100" enterFrom="opacity-0 scale-95" enterTo="opacity-100 scale-100"
          leave="transition ease-in duration-75" leaveFrom="opacity-100 scale-100" leaveTo="opacity-0 scale-95">
          <ListboxOptions className="absolute right-0 z-50 mt-1.5 w-40 origin-top-right rounded-xl bg-paper p-1.5 shadow-lg ring-1 ring-stone-200 dark:bg-stone-800 dark:ring-stone-700">
            {themes.map(({ key, icon: Icon, label }) => (
              <ListboxOption
                key={key}
                value={key}
                className={({ focus }) =>
                  clsx(
                    "flex cursor-pointer items-center gap-2 rounded-md px-2.5 py-1.5 text-sm",
                    focus && "bg-stone-100 dark:bg-stone-800",
                    mode === key
                      ? "font-medium text-stone-900 dark:text-stone-100"
                      : "text-stone-600 dark:text-stone-300"
                  )
                }
              >
                <Icon className="h-4 w-4" strokeWidth={1.75} />
                {label}
                {mode === key && <Check className="ml-auto h-3.5 w-3.5 text-stone-500 dark:text-stone-400" />}
              </ListboxOption>
            ))}
          </ListboxOptions>
        </Transition>
      </div>
    </Listbox>
  );
}
