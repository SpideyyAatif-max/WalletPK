"use client";

import { useState } from "react";
import { TransactionList } from "@/components/transaction-list";
import { Button } from "@/components/ui/button";
import { Alert, Card } from "@/components/ui/card";
import type { HistoryFilter } from "@/lib/wallet-api";
import { useHistory } from "@/lib/wallet-queries";
import { cn } from "@/lib/utils";
import { useWalletShell } from "../wallet-context";

const directions: { value: HistoryFilter["direction"]; label: string }[] = [
  { value: undefined, label: "All" },
  { value: "in", label: "Money in" },
  { value: "out", label: "Money out" },
];

const types = [
  { value: "", label: "All types" },
  { value: "P2PTransfer", label: "Wallet transfers" },
  { value: "CashIn", label: "Cash-in" },
  { value: "CashOut", label: "Cash-out" },
];

/** The full statement, newest first, with filters; pages load on demand (keyset paging on the API). */
export default function HistoryPage() {
  const { freshSequences } = useWalletShell();
  const [filter, setFilter] = useState<HistoryFilter>({});
  const history = useHistory(filter);
  const lines = history.data?.pages.flatMap((page) => page.items) ?? [];

  return (
    <div className="space-y-4">
      <h1 className="text-xl font-bold">History</h1>

      <div className="flex flex-wrap items-center gap-2">
        {directions.map((option) => (
          <button
            key={option.label}
            type="button"
            onClick={() => setFilter((current) => ({ ...current, direction: option.value }))}
            className={cn(
              "rounded-full px-3.5 py-1.5 text-sm font-medium",
              filter.direction === option.value ? "bg-ink-900 text-white" : "bg-white text-ink-700 ring-1 ring-ink-200 hover:bg-ink-100",
            )}
          >
            {option.label}
          </button>
        ))}
        <label className="sr-only" htmlFor="type">
          Type
        </label>
        <select
          id="type"
          value={filter.type ?? ""}
          onChange={(event) => setFilter((current) => ({ ...current, type: event.target.value || undefined }))}
          className="ml-auto h-9 rounded-full bg-white px-3 text-sm text-ink-700 ring-1 ring-ink-200"
        >
          {types.map((option) => (
            <option key={option.value} value={option.value}>
              {option.label}
            </option>
          ))}
        </select>
      </div>

      <Card className="p-4">
        {history.isPending ? (
          <p className="py-8 text-center text-sm text-ink-500">Loading…</p>
        ) : history.error ? (
          <Alert>{history.error.message}</Alert>
        ) : lines.length === 0 ? (
          <p className="py-8 text-center text-sm text-ink-500">Nothing here yet.</p>
        ) : (
          <TransactionList lines={lines} freshSequences={freshSequences} />
        )}
      </Card>

      {history.hasNextPage && (
        <Button variant="outline" className="w-full" onClick={() => void history.fetchNextPage()} loading={history.isFetchingNextPage}>
          Load older
        </Button>
      )}
    </div>
  );
}
