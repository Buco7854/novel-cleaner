import { api, apiJson } from "./client";

export function checkSetupNeeded(): Promise<{ needed: boolean }> {
  return api<{ needed: boolean }>("/api/setup/needed");
}

export function completeSetup(email: string, password: string, displayName?: string): Promise<void> {
  return apiJson("/api/setup", "POST", { email, password, displayName });
}
