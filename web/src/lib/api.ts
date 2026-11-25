import { publicApiUrl } from "./config";

/** An RFC 9457 problem response from the API, with WalletPK's stable error `code`. */
export class ApiError extends Error {
  constructor(
    readonly status: number,
    readonly code: string | undefined,
    message: string,
    readonly fieldErrors: Record<string, string[]> = {},
    readonly retryAfterSeconds?: number,
  ) {
    super(message);
    this.name = "ApiError";
  }
}

type ProblemBody = { title?: string; detail?: string; code?: string; errors?: Record<string, string[]> };

export async function toApiError(response: Response): Promise<ApiError> {
  let problem: ProblemBody = {};
  try {
    problem = (await response.json()) as ProblemBody;
  } catch {
    // Not JSON (a proxy error page, say): fall back to the status.
  }
  const retryAfter = Number(response.headers.get("Retry-After"));
  const message =
    problem.detail ??
    problem.title ??
    (response.status === 429 ? "Too many attempts. Please wait a moment." : `Request failed (${response.status}).`);
  return new ApiError(response.status, problem.code, message, problem.errors ?? {}, Number.isFinite(retryAfter) && retryAfter > 0 ? retryAfter : undefined);
}

/** How the API client gets a token; set by the session once someone signs in. */
export type TokenSource = {
  /** A valid access token (refreshed first if it is about to expire), or null when signed out. */
  getAccessToken(): Promise<string | null>;
  /** Forces a refresh after a 401; null when the session is over. */
  refreshAccessToken(): Promise<string | null>;
};

let tokens: TokenSource | null = null;

export function setTokenSource(source: TokenSource | null): void {
  tokens = source;
}

export type RequestOptions = {
  method?: "GET" | "POST" | "PUT" | "DELETE";
  body?: unknown;
  /** Sent as Idempotency-Key: the server replays the first response for a repeated key. */
  idempotencyKey?: string;
  /** Extra request headers (for example X-Mpin-Confirmation). */
  headers?: Record<string, string>;
  /** Send the access token (default true). */
  authenticated?: boolean;
  signal?: AbortSignal;
};

/** Calls the gateway. Throws ApiError for problem responses; retries once with a fresh token after a 401. */
export async function api<T>(path: string, options: RequestOptions = {}): Promise<T> {
  const { method = "GET", body, idempotencyKey, headers: extraHeaders, authenticated = true, signal } = options;

  const send = async (token: string | null) => {
    const headers: Record<string, string> = { Accept: "application/json", ...extraHeaders };
    if (body !== undefined) headers["Content-Type"] = "application/json";
    if (idempotencyKey) headers["Idempotency-Key"] = idempotencyKey;
    if (token) headers.Authorization = `Bearer ${token}`;
    return fetch(`${publicApiUrl}${path}`, {
      method,
      headers,
      body: body === undefined ? undefined : JSON.stringify(body),
      signal,
      cache: "no-store",
    });
  };

  let response = await send(authenticated ? ((await tokens?.getAccessToken()) ?? null) : null);
  if (response.status === 401 && authenticated && tokens) {
    const fresh = await tokens.refreshAccessToken();
    if (fresh) response = await send(fresh);
  }

  if (!response.ok) throw await toApiError(response);
  if (response.status === 204) return undefined as T;
  const text = await response.text();
  return (text ? JSON.parse(text) : undefined) as T;
}

/** A fresh Idempotency-Key for one user action (one tap of "Send"). */
export function newIdempotencyKey(): string {
  return crypto.randomUUID();
}
