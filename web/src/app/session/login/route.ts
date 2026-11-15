import type { NextRequest } from "next/server";
import { callIdentity, rejectCrossSite, relay, unreachable } from "@/lib/server/session";

/** Phone + MPIN + device → 200 session (trusted device) or 202 OTP challenge (new device). */
export async function POST(request: NextRequest) {
  const refused = rejectCrossSite(request);
  if (refused) return refused;

  const body = (await request.json().catch(() => ({}))) as Record<string, unknown>;
  try {
    return await relay(
      await callIdentity("/login", {
        phoneNumber: body.phoneNumber,
        mpin: body.mpin,
        installationId: body.installationId,
        deviceName: body.deviceName,
      }),
    );
  } catch {
    return unreachable();
  }
}
