import { describe, expect, it } from "vitest";
import { cashProblem, isCashOutLive, normaliseAgentCode, quoteCashIn, quoteCashOut, spacedCode } from "./agents";

describe("agent quotes", () => {
  it("match the server's plan for cash-out: 1% fee (Rs. 10 to Rs. 300), 16% tax, 60% to the agent", () => {
    expect(quoteCashOut(100_000)).toEqual({ amountPaisa: 100_000, feePaisa: 1_000, taxPaisa: 160, commissionPaisa: 600, totalPaisa: 101_160 });
    expect(quoteCashOut(250_000)).toEqual({ amountPaisa: 250_000, feePaisa: 2_500, taxPaisa: 400, commissionPaisa: 1_500, totalPaisa: 252_900 });
    expect(quoteCashOut(5_000_000).feePaisa).toBe(30_000);
  });

  it("match the server's plan for cash-in: free for the customer, 0.4% to the agent (Rs. 2 to Rs. 40)", () => {
    expect(quoteCashIn(200_000)).toEqual({ amountPaisa: 200_000, feePaisa: 0, taxPaisa: 0, commissionPaisa: 800, totalPaisa: 200_000 });
    expect(quoteCashIn(10_000).commissionPaisa).toBe(200);
    expect(quoteCashIn(5_000_000).commissionPaisa).toBe(4_000);
  });
});

describe("cash amounts", () => {
  it("are whole rupees from Rs. 100 to Rs. 50,000", () => {
    expect(cashProblem(10_000)).toBeNull();
    expect(cashProblem(5_000_000)).toBeNull();
    expect(cashProblem(9_900)).toMatch(/Rs. 100/);
    expect(cashProblem(10_050)).toMatch(/whole rupees/);
    expect(cashProblem(null)).toMatch(/amount/);
  });
});

describe("agent codes and cash-out codes", () => {
  it("are tidied up", () => {
    expect(normaliseAgentCode(" wpka 123456 ")).toBe("WPKA123456");
    expect(normaliseAgentCode("WPKM123456")).toBeNull();
    expect(normaliseAgentCode("WPKA12345")).toBeNull();
    expect(spacedCode("123456")).toBe("123 456");
  });

  it("are live while open or being paid out", () => {
    expect(isCashOutLive("Open")).toBe(true);
    expect(isCashOutLive("Processing")).toBe(true);
    expect(isCashOutLive("Completed")).toBe(false);
    expect(isCashOutLive("Expired")).toBe(false);
  });
});
