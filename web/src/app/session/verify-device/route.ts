import type { NextRequest } from "next/server";
import { callIdentity, rejectCrossSite, relay, unreachable } from "@/lib/server/session";

/** Challenge + OTP → 200 session; the device becomes trusted. */
export async function POST(request: NextRequest) {
  const refused = rejectCrossSite(request);
  if (refused) return refused;

  const body = (await request.json().catch(() => ({}))) as Record<string, unknown>;
  try {
    return await relay(await callIdentity("/login/verify-device", { challengeId: body.challengeId, code: body.code }));
  } catch {
    return unreachable();
  }
}
