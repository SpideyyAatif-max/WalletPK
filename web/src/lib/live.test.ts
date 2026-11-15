import type { InfiniteData } from "@tanstack/react-query";
import { describe, expect, it } from "vitest";
import { hasGap, mergeBalance, mergeHistory } from "./live";
import type { LiveWalletEntry, StatementLine, StatementPage, WalletBalance } from "./types";

const pkr = (minorUnits: number) => ({ minorUnits, currency: "PKR" });

const line = (sequence: number, balanceAfter: number): StatementLine => ({
  sequence,
  transactionId: `t${sequence}`,
  type: "P2PTransfer",
  title: "Received from +92300*****67",
  direction: "In",
  amount: pkr(1_000),
  balanceAfter: pkr(balanceAfter),
  postedAt: "2026-11-15T10:00:00Z",
});

const push = (sequence: number, balance: number, walletId = "w1"): LiveWalletEntry => ({
  walletId,
  walletType: "CustomerWallet",
  sequence,
  balance: pkr(balance),
  entry: line(sequence, balance),
});

const balance = (sequence: number, minorUnits: number): WalletBalance => ({
  walletId: "w1",
  walletNumber: "+923001234567",
  balance: pkr(minorUnits),
  sequence,
  asOf: "2026-11-15T09:00:00Z",
});

const history = (...sequences: number[]): InfiniteData<StatementPage, number | null> => ({
  pages: [{ items: sequences.map((sequence) => line(sequence, sequence * 1_000)), nextBefore: null }],
  pageParams: [null],
});

describe("mergeBalance", () => {
  it("takes a newer entry's balance", () => {
    expect(mergeBalance(balance(4, 4_000), push(5, 5_000))).toMatchObject({ sequence: 5, balance: pkr(5_000) });
  });

  it("ignores late and repeated pushes", () => {
    const current = balance(5, 5_000);
    expect(mergeBalance(current, push(5, 9_999))).toBe(current);
    expect(mergeBalance(current, push(3, 3_000))).toBe(current);
  });

  it("ignores other wallets and an empty cache", () => {
    const current = balance(1, 1_000);
    expect(mergeBalance(current, push(2, 2_000, "w2"))).toBe(current);
    expect(mergeBalance(undefined, push(2, 2_000))).toBeUndefined();
  });
});

describe("mergeHistory", () => {
  it("puts a new entry on top of the first page", () => {
    const merged = mergeHistory(history(3, 2, 1), push(4, 4_000));
    expect(merged?.pages[0]?.items.map((item) => item.sequence)).toEqual([4, 3, 2, 1]);
  });

  it("never duplicates an entry the page already has", () => {
    const current = history(3, 2, 1);
    expect(mergeHistory(current, push(3, 3_000))).toBe(current);
    expect(mergeHistory(current, push(2, 2_000))).toBe(current);
  });

  it("works on an empty history", () => {
    expect(mergeHistory(history(), push(1, 1_000))?.pages[0]?.items).toHaveLength(1);
  });
});

describe("hasGap", () => {
  it("spots skipped entries so the screen refetches", () => {
    expect(hasGap(balance(4, 0), push(5, 0))).toBe(false);
    expect(hasGap(balance(4, 0), push(7, 0))).toBe(true);
    expect(hasGap(undefined, push(7, 0))).toBe(false);
  });
});
