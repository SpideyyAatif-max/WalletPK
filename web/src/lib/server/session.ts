import { NextResponse, type NextRequest } from "next/server";
import { serverApiUrl } from "@/lib/config";
import type { AuthTokens } from "@/lib/types";

// The session's server half (a small "backend for frontend"). The refresh token never reaches
// browser JavaScript: it lives in an httpOnly cookie scoped to /session, and these route handlers
// trade it for short-lived access tokens. The browser keeps the access token in memory only and
// sends it straight to the gateway; a reload gets a new one from /session/refresh.

export const refreshCookie = "wpk_rt";

/** What the browser gets instead of AuthTokens: no refresh token. */
export type SessionPayload = {
  accessToken: string;
  expiresIn: number;
  userId: string;
  deviceId: string;
  coolingOffUntil: string | null;
};

/** Refuses cross-site calls: only pages of this app may use the session (the cookie is SameSite=Strict too). */
export function rejectCrossSite(request: NextRequest): NextResponse | null {
  const origin = request.headers.get("origin");
  const host = request.headers.get("x-forwarded-host") ?? request.headers.get("host");
  if (origin && host && new URL(origin).host !== host) {
    return problem(403, "session.cross_site", "Cross-site session requests are not allowed.");
  }
  return null;
}

/** POSTs JSON to the Identity service through the gateway. */
export async function callIdentity(path: string, body: unknown): Promise<Response> {
  return fetch(`${serverApiUrl()}/api/identity/auth${path}`, {
    method: "POST",
    headers: { "Content-Type": "application/json", Accept: "application/json" },
    body: JSON.stringify(body),
    cache: "no-store",
  });
}

/**
 * Turns an Identity response into the browser's response: tokens become a cookie plus a
 * SessionPayload; a device challenge (202) and problems pass through unchanged.
 */
export async function relay(upstream: Response): Promise<NextResponse> {
  const text = await upstream.text();
  if (upstream.status !== 200) {
    return new NextResponse(text || null, {
      status: upstream.status,
      headers: copyHeaders(upstream, ["content-type", "retry-after"]),
    });
  }

  const tokens = JSON.parse(text) as AuthTokens;
  const payload: SessionPayload = {
    accessToken: tokens.accessToken,
    expiresIn: tokens.expiresIn,
    userId: tokens.userId,
    deviceId: tokens.deviceId,
    coolingOffUntil: tokens.coolingOffUntil,
  };
  const response = NextResponse.json(payload, { headers: { "Cache-Control": "no-store" } });
  response.cookies.set(refreshCookie, tokens.refreshToken, {
    httpOnly: true,
    secure: secureCookies(),
    sameSite: "strict",
    path: "/session",
    maxAge: tokens.refreshTokenExpiresIn,
  });
  return response;
}

/** Secure cookies need HTTPS; SESSION_COOKIE_SECURE=false allows plain http://localhost in docker compose. */
function secureCookies(): boolean {
  const configured = process.env.SESSION_COOKIE_SECURE;
  return configured ? configured === "true" : process.env.NODE_ENV === "production";
}

export function clearSession(response: NextResponse): NextResponse {
  response.cookies.set(refreshCookie, "", { httpOnly: true, sameSite: "strict", path: "/session", maxAge: 0 });
  return response;
}

export function problem(status: number, code: string, detail: string): NextResponse {
  return NextResponse.json({ status, title: detail, detail, code }, { status, headers: { "Content-Type": "application/problem+json" } });
}

/** The gateway could not be reached at all (stopped, wrong API_URL). */
export function unreachable(): NextResponse {
  return problem(502, "session.api_unreachable", "The WalletPK API is not reachable. Is docker compose running?");
}

function copyHeaders(upstream: Response, names: string[]): Headers {
  const headers = new Headers();
  for (const name of names) {
    const value = upstream.headers.get(name);
    if (value) headers.set(name, value);
  }
  return headers;
}
