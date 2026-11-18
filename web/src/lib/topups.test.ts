import { describe, expect, it } from "vitest";
import { airtimeProblem, isTopUpFinal, networkOf, validity } from "./topups";

describe("networks", () => {
  it("are found from the number's prefix, however it is typed", () => {
    expect(networkOf("0300 1234567")).toBe("JAZZ");
    expect(networkOf("+92 321 1234567")).toBe("JAZZ");
    expect(networkOf("03151234567")).toBe("ZONG");
    expect(networkOf("923331234567")).toBe("UFONE");
    expect(networkOf("0345-1234567")).toBe("TELENOR");
  });

  it("are unknown for other or incomplete numbers", () => {
    expect(networkOf("03551234567")).toBeNull();
    expect(networkOf("0300 12345")).toBeNull();
    expect(networkOf("")).toBeNull();
  });
});

describe("airtime", () => {
  it("is whole rupees from Rs. 20 to Rs. 5,000", () => {
    expect(airtimeProblem(2_000)).toBeNull();
    expect(airtimeProblem(500_000)).toBeNull();
    expect(airtimeProblem(1_900)).toMatch(/Rs. 20/);
    expect(airtimeProblem(500_100)).toMatch(/Rs. 5,000/);
    expect(airtimeProblem(10_050)).toMatch(/whole rupees/);
    expect(airtimeProblem(null)).toMatch(/amount/);
  });
});

describe("top-ups", () => {
  it("are final once completed or failed", () => {
    expect(isTopUpFinal("Completed")).toBe(true);
    expect(isTopUpFinal("Failed")).toBe(true);
    expect(isTopUpFinal("Processing")).toBe(false);
    expect(isTopUpFinal("UnderReview")).toBe(false);
  });

  it("show bundle validity in days", () => {
    expect(validity(1)).toBe("1 day");
    expect(validity(30)).toBe("30 days");
  });
});
