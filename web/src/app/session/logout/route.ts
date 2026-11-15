import { NextResponse, type NextRequest } from "next/server";
import { callIdentity, clearSession, refreshCookie, rejectCrossSite } from "@/lib/server/session";

/** Ends the session at Identity and forgets the cookie. Always succeeds for the browser. */
export async function POST(request: NextRequest) {
  const refused = rejectCrossSite(request);
  if (refused) return refused;

  const refreshToken = request.cookies.get(refreshCookie)?.value;
  if (refreshToken) {
    await callIdentity("/logout", { refreshToken }).catch(() => undefined);
  }
  return clearSession(new NextResponse(null, { status: 204 }));
}
