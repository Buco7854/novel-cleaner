import { api, apiJson } from "./client";

export interface AppSettings {
  baseUrl: string;
  model: string;
  hasApiKey: boolean;
  maxWorkers: number;
  systemPrompt: string;
  dropFolder: string;
  defaultSystemPrompt: string;
  /** Admin master-switch for AI features. When false the editor hides
   *  every Run-AI button and the OPDS card's "Add & run AI" option. */
  aiEnabled: boolean;
  /** True when the caller may PUT updates to the app section (admin only). */
  canEdit: boolean;
}

export interface UserSettings {
  /** Per-user instructions appended after the admin's system prompt at
   *  every LLM call. */
  systemPrompt: string;
}

export interface UserSettingsUpdate {
  systemPrompt: string;
}

export interface SettingsResponse {
  app: AppSettings;
  user: UserSettings;
}

export interface AppSettingsUpdate {
  apiKey?: string | null;
  baseUrl: string;
  model: string;
  maxWorkers: number;
  systemPrompt: string;
  dropFolder: string;
  aiEnabled: boolean;
}

export const fetchSettings    = () => api<SettingsResponse>("/api/settings/");
export const saveAppSettings  = (s: AppSettingsUpdate)  => apiJson<SettingsResponse>("/api/settings/app",  "PUT", s);
export const saveUserSettings = (s: UserSettingsUpdate) => apiJson<SettingsResponse>("/api/settings/user", "PUT", s);
