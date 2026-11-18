import { describe, expect, it } from "vitest";
import { formatIban, normalizeIban } from "./iban";
import { equalShares, isFinal, stepName } from "./payments";

describe("normalizeIban", () => {
  it("accepts valid Pakistani IBANs however they are typed", () => {
    expect(normalizeIban("PK36SCBL0000001123456702")).toBe("PK36SCBL0000001123456702");
    expect(normalizeIban("pk36 scbl 0000 0011 2345 6702")).toBe("PK36SCBL0000001123456702");
    expect(normalizeIban("PK36-SCBL-0000-0011-2345-6702")).toBe("PK36SCBL0000001123456702");
  });

  it("refuses typos, wrong lengths and other countries", () => {
    expect(normalizeIban("PK37SCBL0000001123456702")).toBeNull();
    expect(normalizeIban("PK36SCBL000000112345670")).toBeNull();
    expect(normalizeIban("GB36SCBL0000001123456702")).toBeNull();
    expect(normalizeIban("")).toBeNull();
  });

  it("formats in groups of four", () => {
    expect(formatIban("PK36SCBL0000001123456702")).toBe("PK36 SCBL 0000 0011 2345 6702");
  });
});

describe("equalShares", () => {
  it("splits like the server: the odd paisa to the first people", () => {
    expect(equalShares(300_100, 2, true)).toEqual({ each: [100_034, 100_033], mine: 100_033 });
    expect(equalShares(100_001, 2, false)).toEqual({ each: [50_001, 50_000], mine: 0 });
  });

  it("has nothing to split without people", () => {
    expect(equalShares(1_000, 0, true)).toEqual({ each: [], mine: 0 });
  });
});

describe("transfer states", () => {
  it("knows which states are final", () => {
    expect(isFinal("Completed")).toBe(true);
    expect(isFinal("Expired")).toBe(true);
    expect(isFinal("Queued")).toBe(false);
    expect(isFinal("Pending")).toBe(false);
  });

  it("names steps for people", () => {
    expect(stepName("AwaitingBank")).toBe("Waiting for the bank");
    expect(stepName("SomethingNew")).toBe("SomethingNew");
  });
});
