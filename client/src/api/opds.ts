import { api, apiJson } from "./client";

export type OpdsImportMode = "AddOnly" | "AddAndRunAi";

export interface OpdsSourceSummary {
  id: string;
  name: string;
  url: string;
  hasCredentials: boolean;
  createdAt: string;
  lastUsedAt: string | null;
}

export interface OpdsLink {
  href: string;
  rel: string | null;
  type: string | null;
  title: string | null;
}

/** A folder-like entry that drills into another OPDS feed. */
export interface OpdsCategory {
  title: string;
  summary: string | null;
  href: string;
}

/** A real book with at least one acquisition link. */
export interface OpdsBook {
  title: string;
  author: string | null;
  summary: string | null;
  /** True when {@link summary} carries HTML (atom:content type=html). */
  summaryIsHtml: boolean;
  coverHref: string | null;
  categories: string[];
  languages: string[];
  publisher: string | null;
  issued: string | null;
  acquisitionLinks: OpdsLink[];
}

export interface OpdsFeed {
  title: string | null;
  navigationLinks: OpdsLink[];
  categories: OpdsCategory[];
  books: OpdsBook[];
}

export const listSources = () => api<OpdsSourceSummary[]>("/api/opds/sources");

export const createSource = (req: {
  name: string; url: string; username?: string; password?: string;
}) => apiJson<{ id: string }>("/api/opds/sources", "POST", req);

export const updateSource = (id: string, req: {
  name: string; url: string; username?: string; password?: string;
}) => apiJson<null>(`/api/opds/sources/${id}`, "PUT", req);

export const deleteSource = (id: string) =>
  api(`/api/opds/sources/${id}`, { method: "DELETE" });

export const browseSource = (id: string, url?: string) => {
  const qs = url ? `?url=${encodeURIComponent(url)}` : "";
  return api<OpdsFeed>(`/api/opds/sources/${id}/browse${qs}`);
};

/**
 * Add a book from an OPDS feed to the user's library. <code>mode</code>
 * decides whether to also queue the AI cleanup pass.
 */
export const importEntry = (id: string, href: string, title: string | undefined, mode: OpdsImportMode) =>
  apiJson<{ novelId: string }>(`/api/opds/sources/${id}/import`, "POST", { href, title, mode });

/**
 * Builds a same-origin URL that proxies an OPDS asset (typically a cover
 * image) through our server. The browser can't attach the source's stored
 * basic-auth credentials to a direct <img src=upstream>, and an upstream
 * 401 raises a native auth prompt — proxying avoids both.
 */
export const sourceAssetUrl = (sourceId: string, href: string) =>
  `/api/opds/sources/${sourceId}/asset?url=${encodeURIComponent(href)}`;
