import { createContext, useCallback, useContext, useEffect, useMemo, useState } from "react";

type Mode = "light" | "dark" | "system";
interface ThemeState { mode: Mode; effective: "light" | "dark"; setMode: (m: Mode) => void; toggle: () => void }
const Ctx = createContext<ThemeState | null>(null);

function readSystem(): "light" | "dark" {
  return window.matchMedia("(prefers-color-scheme: dark)").matches ? "dark" : "light";
}

export function ThemeProvider({ children }: { children: React.ReactNode }) {
  const [mode, setMode] = useState<Mode>(() => (localStorage.getItem("theme") as Mode | null) ?? "system");
  const [systemPref, setSystem] = useState<"light" | "dark">(readSystem);

  useEffect(() => {
    const m = window.matchMedia("(prefers-color-scheme: dark)");
    const onChange = () => setSystem(readSystem());
    m.addEventListener("change", onChange);
    return () => m.removeEventListener("change", onChange);
  }, []);

  const effective = mode === "system" ? systemPref : mode;

  useEffect(() => {
    document.documentElement.classList.toggle("dark", effective === "dark");
    localStorage.setItem("theme", mode);
  }, [mode, effective]);

  const toggle = useCallback(() => setMode((m) => (m === "dark" ? "light" : "dark")), []);
  const value = useMemo<ThemeState>(() => ({ mode, effective, setMode, toggle }), [mode, effective, toggle]);
  return <Ctx.Provider value={value}>{children}</Ctx.Provider>;
}

export function useTheme() {
  const v = useContext(Ctx);
  if (!v) throw new Error("ThemeProvider missing");
  return v;
}
