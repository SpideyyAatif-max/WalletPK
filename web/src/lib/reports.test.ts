import { describe, expect, it } from "vitest";
import { fileNameOf } from "./api";
import { breakKindLabel, countByKind, pakistanDay, partyLabel, recentMonths } from "./reports";
import { isFinance } from "./roles";

describe("reconciliation and reports", () => {
  it("lets finance and admins work breaks", () => {
    expect(isFinance({ roles: ["finance"] })).toBe(true);
    expect(isFinance({ roles: ["admin"] })).toBe(true);
    expect(isFinance({ roles: ["ops", "compliance"] })).toBe(false);
    expect(isFinance(null)).toBe(false);
  });

  it("names parties and kinds of break", () => {
    expect(partyLabel("bank:HBL")).toBe("HBL (bank)");
    expect(partyLabel("telco:JAZZ")).toBe("JAZZ (network)");
    expect(partyLabel("raast")).toBe("raast");
    expect(breakKindLabel("MissingAtProvider")).toBe("Missing at provider");
    expect(breakKindLabel("Something")).toBe("Something");
  });

  it("counts breaks by kind in a fixed order", () => {
    expect(countByKind([{ kind: "Duplicate" }, { kind: "MissingAtProvider" }, { kind: "Duplicate" }])).toEqual([
      { kind: "MissingAtProvider", count: 1 },
      { kind: "Duplicate", count: 2 },
    ]);
  });

  it("works out Pakistan days and months", () => {
    // 20:30 UTC on 30 November is 01:30 on 1 December in Pakistan.
    const now = new Date("2026-11-30T20:30:00Z");
    expect(pakistanDay(0, now)).toBe("2026-12-01");
    expect(pakistanDay(1, now)).toBe("2026-11-30");
    const months = recentMonths(3, now);
    expect(months.map((month) => month.id)).toEqual(["2026-12", "2026-11", "2026-10"]);
    expect(months[1]?.label).toBe("November 2026");
    expect(recentMonths(2, new Date("2027-01-15T00:00:00Z")).map((month) => month.id)).toEqual(["2027-01", "2026-12"]);
  });

  it("reads a download's file name", () => {
    expect(fileNameOf('attachment; filename=walletpk-statement-2026-11.pdf; filename*=UTF-8\'\'walletpk-statement-2026-11.pdf', "x")).toBe("walletpk-statement-2026-11.pdf");
    expect(fileNameOf('attachment; filename="report.csv"', "x")).toBe("report.csv");
    expect(fileNameOf(null, "fallback.csv")).toBe("fallback.csv");
  });
});
