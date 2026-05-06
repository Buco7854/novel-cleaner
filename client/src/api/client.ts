export class HttpError extends Error {
  constructor(public status: number, message: string, public body?: unknown) { super(message); }
}

async function parse(resp: Response) {
  const text = await resp.text();
  if (!text) return null;
  try { return JSON.parse(text); } catch { return text; }
}

export async function api<T = unknown>(
  path: string,
  init: RequestInit = {}
): Promise<T> {
  const resp = await fetch(path, {
    ...init,
    credentials: "include",
    headers: {
      "Accept": "application/json",
      // Sent on every request so the server can reject any cross-origin
      // form post that lacks it (browsers turn requests with this header
      // into preflighted ones, blocking same-site CSRF too).
      "X-Requested-With": "novel-cleaner",
      ...(init.headers ?? {}),
    },
  });
  if (!resp.ok) {
    const body = await parse(resp);
    throw new HttpError(resp.status, `${resp.status} ${resp.statusText}`, body);
  }
  if (resp.status === 204) return null as T;
  return (await parse(resp)) as T;
}

export function apiJson<T>(path: string, method: string, body: unknown): Promise<T> {
  return api<T>(path, {
    method,
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(body),
  });
}
