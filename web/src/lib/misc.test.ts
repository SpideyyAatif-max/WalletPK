import { describe, expect, it } from "vitest";
import { dayKey, formatDay } from "./dates";
import { deviceName } from "./device";
import { readClaims } from "./jwt";

describe("readClaims", () => {
  const encode = (value: object) => Buffer.from(JSON.stringify(value)).toString("base64url");

  it("reads the claims the app shows", () => {
    const token = `${encode({ alg: "none" })}.${encode({ sub: "u1", phone_number: "+923001234567", role: ["customer"], kyc_tier: "L1", exp: 1_800_000_000 })}.sig`;
    expect(readClaims(token)).toEqual({
      sub: "u1",
      phoneNumber: "+923001234567",
      kycTier: "L1",
      roles: ["customer"],
      deviceId: null,
      expiresAt: 1_800_000_000_000,
    });
  });

  it("returns null for garbage", () => {
    expect(readClaims("not-a-token")).toBeNull();
    expect(readClaims("a.%%%.c")).toBeNull();
  });
});

describe("dates in Pakistan time", () => {
  it("groups by the Pakistani calendar day", () => {
    // 20:30 UTC is 01:30 the next day in Karachi (UTC+5).
    expect(dayKey("2026-11-14T20:30:00Z")).toBe("2026-11-15");
    expect(formatDay("2026-11-14T20:30:00Z", new Date("2026-11-15T08:00:00Z"))).toBe("Today");
    expect(formatDay("2026-11-14T08:00:00Z", new Date("2026-11-15T08:00:00Z"))).toBe("Yesterday");
  });
});

describe("deviceName", () => {
  it("names common browsers", () => {
    expect(deviceName("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/142.0 Safari/537.36")).toBe("Chrome on Windows");
    expect(deviceName("Mozilla/5.0 (Windows NT 10.0) AppleWebKit/537.36 Chrome/142.0 Safari/537.36 Edg/142.0")).toBe("Edge on Windows");
    expect(deviceName("Mozilla/5.0 (Linux; Android 15) Gecko/20100101 Firefox/145.0")).toBe("Firefox on Android");
  });
});
