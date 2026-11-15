/** The claims the app shows. Read without verifying: the API verifies every token itself. */
export type AccessClaims = {
  sub: string;
  phoneNumber: string | null;
  kycTier: string | null;
  roles: string[];
  deviceId: string | null;
  expiresAt: number; // ms since epoch
};

export function readClaims(token: string): AccessClaims | null {
  const part = token.split(".")[1];
  if (!part) return null;
  try {
    const json = JSON.parse(decodeBase64Url(part)) as Record<string, unknown>;
    const role = json.role;
    return {
      sub: String(json.sub ?? ""),
      phoneNumber: typeof json.phone_number === "string" ? json.phone_number : null,
      kycTier: typeof json.kyc_tier === "string" ? json.kyc_tier : null,
      roles: Array.isArray(role) ? role.map(String) : typeof role === "string" ? [role] : [],
      deviceId: typeof json.device_id === "string" ? json.device_id : null,
      expiresAt: typeof json.exp === "number" ? json.exp * 1000 : 0,
    };
  } catch {
    return null;
  }
}

function decodeBase64Url(value: string): string {
  const base64 = value.replace(/-/g, "+").replace(/_/g, "/").padEnd(Math.ceil(value.length / 4) * 4, "=");
  const bytes = Uint8Array.from(atob(base64), (char) => char.charCodeAt(0));
  return new TextDecoder().decode(bytes);
}
