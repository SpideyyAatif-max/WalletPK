import { describe, expect, it } from "vitest";
import { checkDateOfBirth, formatCnic, normalizeCnic, usedPercent } from "./identity";

const rs = (paisa: number) => ({ minorUnits: paisa, currency: "PKR" });

describe("CNIC", () => {
  it("accepts 13 digits however they are typed", () => {
    expect(normalizeCnic("35202-1234567-1")).toBe("3520212345671");
    expect(normalizeCnic("3520212345671")).toBe("3520212345671");
    expect(normalizeCnic(" 35202 1234567 1 ")).toBe("3520212345671");
    expect(normalizeCnic("35202-123456-1")).toBeNull();
    expect(normalizeCnic("35202-1234567-1a")).toBeNull();
  });

  it("formats as it is typed", () => {
    expect(formatCnic("352")).toBe("352");
    expect(formatCnic("3520212")).toBe("35202-12");
    expect(formatCnic("3520212345671")).toBe("35202-1234567-1");
    expect(formatCnic("35202-1234567-19999")).toBe("35202-1234567-1");
  });
});

describe("date of birth", () => {
  const today = new Date(Date.UTC(2026, 10, 26));

  it("needs a real date and an adult", () => {
    expect(checkDateOfBirth("1998-04-21", today)).toEqual({ ok: true });
    expect(checkDateOfBirth("2008-11-26", today)).toEqual({ ok: true });
    expect(checkDateOfBirth("2008-11-27", today).ok).toBe(false);
    expect(checkDateOfBirth("1998-02-30", today).ok).toBe(false);
    expect(checkDateOfBirth("", today).ok).toBe(false);
  });
});

describe("limit bars", () => {
  it("shows the share used, capped at 100", () => {
    expect(usedPercent({ limit: rs(2_500_000), used: rs(625_000), remaining: rs(1_875_000) })).toBe(25);
    expect(usedPercent({ limit: rs(100), used: rs(150), remaining: rs(0) })).toBe(100);
    expect(usedPercent({ limit: rs(0), used: rs(0), remaining: rs(0) })).toBe(100);
  });
});
