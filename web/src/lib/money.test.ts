import { describe, expect, it } from "vitest";
import { formatPaisa, parseRupees } from "./money";

describe("formatPaisa", () => {
  it("shows rupees with two decimals and thousands separators", () => {
    expect(formatPaisa(150_050)).toBe("Rs. 1,500.50");
    expect(formatPaisa(0)).toBe("Rs. 0.00");
    expect(formatPaisa(1)).toBe("Rs. 0.01");
    expect(formatPaisa(123_456_789)).toBe("Rs. 1,234,567.89");
  });

  it("signs statement lines", () => {
    expect(formatPaisa(50_000, { signed: "in" })).toBe("+ Rs. 500.00");
    expect(formatPaisa(50_000, { signed: "out" })).toBe("− Rs. 500.00");
    expect(formatPaisa(-50_000)).toBe("− Rs. 500.00");
  });
});

describe("parseRupees", () => {
  it.each([
    ["500", 50_000],
    ["1,500", 150_000],
    ["1500.5", 150_050],
    ["1500.05", 150_005],
    ["Rs 1,250.50", 125_050],
    ["rs. 10", 1_000],
    ["  0.01 ", 1],
  ])("reads %s as %i paisa", (input, paisa) => {
    expect(parseRupees(input)).toBe(paisa);
  });

  it.each(["", "0", "0.00", "-5", "1.234", "abc", "1e5", "12.", ".5"])("refuses %j", (input) => {
    expect(parseRupees(input)).toBeNull();
  });

  it("never loses a paisa to floating point", () => {
    for (let paisa = 1; paisa < 2_000; paisa += 7) {
      const typed = `${Math.floor(paisa / 100)}.${String(paisa % 100).padStart(2, "0")}`;
      expect(parseRupees(typed)).toBe(paisa);
    }
  });
});
