import { api, apiJson } from "./client";

export interface AppSettings {
  baseUrl: string;
  model: string;
  hasApiKey: boolean;
  maxWorkers: number;
  systemPrompt: string;
  dropFolder: string;
  defaultSystemPrompt: string;
  /** True when the caller may PUT updates to the app section (admin only). */
  canEdit: boolean;
}

export interface UserSettings {
  patterns: string[];
  scanAll: boolean;
  contextWindow: number;
  reviewBeforeApplying: boolean;
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
}

export interface UserSettingsUpdate {
  patterns: string[];
  scanAll: boolean;
  contextWindow: number;
  reviewBeforeApplying: boolean;
}

export const fetchSettings    = () => api<SettingsResponse>("/api/settings/");
export const saveAppSettings  = (s: AppSettingsUpdate)  => apiJson<SettingsResponse>("/api/settings/app",  "PUT", s);
export const saveUserSettings = (s: UserSettingsUpdate) => apiJson<SettingsResponse>("/api/settings/user", "PUT", s);
