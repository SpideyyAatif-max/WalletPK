import { NextResponse, type NextRequest } from "next/server";
import { callIdentity, clearSession, refreshCookie, rejectCrossSite, relay, unreachable } from "@/lib/server/session";

/**
 * 200: trades the refresh-token cookie for a new access token (and a new cookie: refresh tokens are
 * single use). Tabs take turns through a Web Lock in the browser, so two tabs never present the
 * same refresh token, which Identity would treat as theft.
 */
export async function POST(request: NextRequest) {
  const refused = rejectCrossSite(request);
  if (refused) return refused;

  // No cookie is the normal signed-out case, not an error: 204, so browsers log nothing.
  const refreshToken = request.cookies.get(refreshCookie)?.value;
  if (!refreshToken) return new NextResponse(null, { status: 204 });

  let upstream: Response;
  try {
    upstream = await callIdentity("/refresh", { refreshToken });
  } catch {
    return unreachable();
  }

  const response = await relay(upstream);
  // Expired, revoked or reused: the cookie is worthless now.
  return upstream.status === 200 || upstream.status >= 500 ? response : clearSession(response);
}
