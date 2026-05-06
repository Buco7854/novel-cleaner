export type ClassValue = string | number | boolean | null | undefined | ClassValue[] | { [key: string]: unknown };

export function clsx(...inputs: ClassValue[]): string {
  const out: string[] = [];
  for (const v of inputs) {
    if (!v) continue;
    if (typeof v === "string" || typeof v === "number") { out.push(String(v)); continue; }
    if (Array.isArray(v)) { out.push(clsx(...v)); continue; }
    if (typeof v === "object") {
      for (const k of Object.keys(v)) if ((v as Record<string, unknown>)[k]) out.push(k);
    }
  }
  return out.join(" ").trim();
}
