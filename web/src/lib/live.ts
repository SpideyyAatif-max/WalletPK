import type { InfiniteData } from "@tanstack/react-query";
import type { LiveWalletEntry, StatementPage, WalletBalance } from "./types";

// Pure merge rules for live pushes, so they are easy to test. A push can arrive late, twice, or
// before the screen's own fetch; the wallet's entry sequence decides what is newer.

/** The balance after a push: only a strictly newer sequence replaces what is shown. */
export function mergeBalance(current: WalletBalance | undefined, live: LiveWalletEntry): WalletBalance | undefined {
  if (!current || current.walletId !== live.walletId) return current;
  if (live.sequence <= current.sequence) return current;
  return { ...current, balance: live.balance, sequence: live.sequence, asOf: live.entry.postedAt };
}

/**
 * The unfiltered history after a push: the entry goes on top of the first page if it is newer than
 * everything there. Older or duplicate pushes change nothing (a refetch fills real gaps).
 */
export function mergeHistory(
  current: InfiniteData<StatementPage, number | null> | undefined,
  live: LiveWalletEntry,
): InfiniteData<StatementPage, number | null> | undefined {
  const first = current?.pages[0];
  if (!current || !first) return current;
  const newest = first.items[0]?.sequence ?? 0;
  if (live.entry.sequence <= newest) return current;
  return { ...current, pages: [{ ...first, items: [live.entry, ...first.items] }, ...current.pages.slice(1)] };
}

/** True when the push skipped entries the screen has not seen (it was offline): refetch instead. */
export function hasGap(current: WalletBalance | undefined, live: LiveWalletEntry): boolean {
  return !!current && current.walletId === live.walletId && live.sequence > current.sequence + 1;
}
