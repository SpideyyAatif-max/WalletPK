import type { InfiniteData } from "@tanstack/react-query";
import { describe, expect, it } from "vitest";
import { markReadInPages, mergeNotification, queriesFor, worthAToast, type AppNotification, type NotificationPage } from "./notifications";

const note = (id: string, template = "money_received", read = false): AppNotification => ({
  id,
  template,
  category: "Money",
  title: "Money received",
  body: "You received Rs. 250.00",
  createdAt: "2026-11-22T10:00:00Z",
  read,
});

const pages = (...items: AppNotification[][]): InfiniteData<NotificationPage, string | null> => ({
  pages: items.map((list, index) => ({ items: list, unreadCount: 1, nextBefore: index < items.length - 1 ? `cursor${index}` : null })),
  pageParams: items.map((_, index) => (index === 0 ? null : `cursor${index - 1}`)),
});

describe("mergeNotification", () => {
  it("puts a pushed notification on top and updates the unread count everywhere", () => {
    const merged = mergeNotification(pages([note("a")], [note("b", "welcome", true)]), { notification: note("c"), unreadCount: 2 });

    expect(merged?.pages[0]?.items.map((item) => item.id)).toEqual(["c", "a"]);
    expect(merged?.pages.map((page) => page.unreadCount)).toEqual([2, 2]);
  });

  it("does not add the same notification twice (a repeated push)", () => {
    const once = mergeNotification(pages([note("a")]), { notification: note("a"), unreadCount: 1 });

    expect(once?.pages[0]?.items).toHaveLength(1);
  });

  it("leaves an unloaded list alone", () => {
    expect(mergeNotification(undefined, { notification: note("a"), unreadCount: 1 })).toBeUndefined();
  });
});

describe("markReadInPages", () => {
  it("marks one notification read and sets the count", () => {
    const updated = markReadInPages(pages([note("a"), note("b")]), "b", 1);

    expect(updated?.pages[0]?.items.map((item) => item.read)).toEqual([false, true]);
    expect(updated?.pages[0]?.unreadCount).toBe(1);
  });
});

describe("queriesFor", () => {
  it("refreshes the payment's own screen and its list", () => {
    expect(queriesFor({ kind: "bank", id: "t1", status: "Completed", step: null })).toEqual([
      ["payments", "bank-transfers", "t1"],
      ["payments", "bank-transfers"],
    ]);
    expect(queriesFor({ kind: "bill", id: "b1", status: "Failed", step: "Refunded" })).toContainEqual(["bills", "payments", "b1"]);
    expect(queriesFor({ kind: "request", id: "r1", status: "Paid", step: null })).toContainEqual(["payments", "requests"]);
    expect(queriesFor({ kind: "something-new", id: "x", status: "Done", step: null })).toEqual([]);
  });
});

describe("worthAToast", () => {
  it("skips money the user just sent themselves", () => {
    expect(worthAToast(note("a", "money_received"))).toBe(true);
    expect(worthAToast(note("a", "payment_failed"))).toBe(true);
    expect(worthAToast(note("a", "money_sent"))).toBe(false);
    expect(worthAToast(note("a", "wallet_debited"))).toBe(false);
  });
});
