import { describe, expect, it } from "vitest";
import { countdown, describePlan, isQrPayload, secondsLeft, splitFee } from "./merchants";

const standard = { code: "STANDARD", name: "Standard", basisPoints: 100, minFeePaisa: 100, maxFeePaisa: 50_000 };
const kiryana = { code: "KIRYANA", name: "Kiryana", basisPoints: 50, minFeePaisa: 0, maxFeePaisa: 25_000 };

describe("fee split", () => {
  it("matches the server: a percentage, 16% tax on it, the rest to the merchant", () => {
    expect(splitFee(standard, 100_000)).toEqual({ amountPaisa: 100_000, feePaisa: 1_000, taxPaisa: 160, netPaisa: 98_840 });
  });

  it("rounds half up and keeps the floor and ceiling", () => {
    expect(splitFee(standard, 12_350)).toEqual({ amountPaisa: 12_350, feePaisa: 124, taxPaisa: 20, netPaisa: 12_206 });
    expect(splitFee(standard, 5_000).feePaisa).toBe(100);
    expect(splitFee(standard, 50_000_000).feePaisa).toBe(50_000);
    expect(splitFee(kiryana, 100)).toEqual({ amountPaisa: 100, feePaisa: 1, taxPaisa: 0, netPaisa: 99 });
  });

  it("describes a plan", () => {
    expect(describePlan(standard)).toBe("1% (min Rs. 1, max Rs. 500)");
    expect(describePlan(kiryana)).toBe("0.5% (max Rs. 250)");
  });
});

describe("QR codes", () => {
  it("are recognised by their shape", () => {
    expect(isQrPayload("WPKQR1.eyJxIjoiMSJ9.c2lnbmF0dXJl")).toBe(true);
    expect(isQrPayload("  WPKQR1.abc-_.def_-  ")).toBe(true);
    expect(isQrPayload("WPKQR2.abc.def")).toBe(false);
    expect(isQrPayload("https://example.com")).toBe(false);
  });

  it("count down to their expiry", () => {
    const now = Date.parse("2026-11-18T10:00:00Z");
    expect(secondsLeft("2026-11-18T10:01:15Z", now)).toBe(75);
    expect(secondsLeft("2026-11-18T09:59:00Z", now)).toBe(0);
    expect(countdown(75)).toBe("1:15");
    expect(countdown(5)).toBe("0:05");
  });
});
