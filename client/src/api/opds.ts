import { api, apiJson } from "./client";

export interface OpdsSourceSummary {
  id: string;
  name: string;
  url: string;
  hasCredentials: boolean;
  autoClean: boolean;
  createdAt: string;
  lastUsedAt: string | null;
}

export interface OpdsLink {
  href: string;
  rel: string | null;
  type: string | null;
  title: string | null;
}

export interface OpdsEntry {
  title: string;
  author: string | null;
  summary: string | null;
  coverHref: string | null;
  acquisitionLinks: OpdsLink[];
}

export interface OpdsFeed {
  title: string | null;
  navigationLinks: OpdsLink[];
  entries: OpdsEntry[];
}

export const listSources = () => api<OpdsSourceSummary[]>("/api/opds/sources");

export const createSource = (req: {
  name: string; url: string; username?: string; password?: string; autoClean: boolean;
}) => apiJson<{ id: string }>("/api/opds/sources", "POST", req);

export const updateSource = (id: string, req: {
  name: string; url: string; username?: string; password?: string; autoClean: boolean;
}) => apiJson<null>(`/api/opds/sources/${id}`, "PUT", req);

export const deleteSource = (id: string) =>
  api(`/api/opds/sources/${id}`, { method: "DELETE" });

export const browseSource = (id: string, url?: string) => {
  const qs = url ? `?url=${encodeURIComponent(url)}` : "";
  return api<OpdsFeed>(`/api/opds/sources/${id}/browse${qs}`);
};

export const importEntry = (id: string, href: string, title?: string) =>
  apiJson<{ jobId: string }>(`/api/opds/sources/${id}/import`, "POST", { href, title });
