"use client";

import { useMutation, useQueryClient } from "@tanstack/react-query";
import { ArrowUpRight, Banknote, Briefcase, Building2, FlaskConical, HandCoins, Receipt, ScanLine, Smartphone, Store } from "lucide-react";
import Link from "next/link";
import { useState } from "react";
import { LiveBadge } from "@/components/live-badge";
import { TransactionList } from "@/components/transaction-list";
import { Alert, Card } from "@/components/ui/card";
import { devToolsEnabled } from "@/lib/config";
import { formatMoney } from "@/lib/money";
import { formatLocal } from "@/lib/phone";
import { useBalance, useHistory, useWallet, walletNotReady } from "@/lib/wallet-queries";
import { devTools, queryKeys } from "@/lib/wallet-api";
import { useWalletShell } from "./wallet-context";

export default function WalletHome() {
  const { live, freshSequences } = useWalletShell();
  const balance = useBalance();
  const wallet = useWallet();
  const history = useHistory({}, 10);

  const settingUp = walletNotReady(balance.error) || walletNotReady(wallet.error);
  const lines = history.data?.pages.flatMap((page) => page.items).slice(0, 6) ?? [];
  const held = wallet.data?.held.minorUnits ?? 0;

  return (
    <div className="space-y-5">
      <section className="relative overflow-hidden rounded-3xl bg-gradient-to-br from-brand-600 to-brand-900 p-6 text-white shadow-lg">
        <div className="flex items-center justify-between">
          <p className="text-sm text-brand-100">Wallet balance</p>
          <LiveBadge state={live} />
        </div>
        <p className="tabular mt-2 text-4xl font-bold tracking-tight" aria-live="polite">
          {balance.data ? formatMoney(balance.data.balance) : settingUp ? "Setting up…" : "Rs. —"}
        </p>
        <div className="mt-3 flex flex-wrap gap-x-4 gap-y-1 text-sm text-brand-100">
          {balance.data?.walletNumber && <span>Wallet {formatLocal(balance.data.walletNumber)}</span>}
          {held > 0 && wallet.data && <span>Available {formatMoney(wallet.data.available)} · On hold {formatMoney(wallet.data.held)}</span>}
        </div>
        <div aria-hidden className="pointer-events-none absolute -top-10 -right-10 h-40 w-40 rounded-full bg-white/10" />
      </section>

      {settingUp && <Alert tone="info">Your wallet is being opened. This takes a second after sign-up.</Alert>}
      {(balance.error && !settingUp) && <Alert>{balance.error.message}</Alert>}

      <div className="grid grid-cols-4 gap-3">
        <QuickAction href="/wallet/send" label="Send money" icon={<ArrowUpRight className="h-5 w-5" />} />
        <QuickAction href="/wallet/bank" label="To bank" icon={<Building2 className="h-5 w-5" />} />
        <QuickAction href="/wallet/bills" label="Bills" icon={<Receipt className="h-5 w-5" />} />
        <QuickAction href="/wallet/requests" label="Requests" icon={<HandCoins className="h-5 w-5" />} />
        <QuickAction href="/wallet/topup" label="Mobile load" icon={<Smartphone className="h-5 w-5" />} />
        <QuickAction href="/wallet/pay" label="Scan & pay" icon={<ScanLine className="h-5 w-5" />} />
        <QuickAction href="/wallet/shop" label="My shop" icon={<Store className="h-5 w-5" />} />
        <QuickAction href="/wallet/cash-out" label="Cash out" icon={<Banknote className="h-5 w-5" />} />
        <QuickAction href="/wallet/agent" label="Agent mode" icon={<Briefcase className="h-5 w-5" />} />
        {devToolsEnabled && <DevTopUp />}
      </div>

      <Card className="p-4">
        <div className="mb-1 flex items-center justify-between px-1">
          <h2 className="text-base font-semibold">Recent activity</h2>
          <Link href="/wallet/history" className="text-sm font-medium text-brand-700 hover:underline">
            See all
          </Link>
        </div>
        {history.isPending ? (
          <p className="px-1 py-6 text-center text-sm text-ink-500">Loading…</p>
        ) : lines.length === 0 ? (
          <p className="px-1 py-6 text-center text-sm text-ink-500">No transactions yet. Money you send or receive shows up here instantly.</p>
        ) : (
          <TransactionList lines={lines} freshSequences={freshSequences} grouped={false} />
        )}
      </Card>
    </div>
  );
}

function QuickAction({ href, label, icon }: { href: string; label: string; icon: React.ReactNode }) {
  return (
    <Link href={href} className="flex flex-col items-center gap-2 rounded-2xl border border-ink-100 bg-white p-3 text-center text-xs font-medium text-ink-800 shadow-sm hover:border-brand-200 hover:bg-brand-50">
      <span className="grid h-10 w-10 place-items-center rounded-full bg-brand-50 text-brand-700">{icon}</span>
      {label}
    </Link>
  );
}

/** Development only: add Rs. 5,000 of test money (a cash-in from the bank float). */
function DevTopUp() {
  const queryClient = useQueryClient();
  const [error, setError] = useState<string | null>(null);
  const topUp = useMutation({
    mutationFn: () => devTools.topUp(500_000),
    onSuccess: () => {
      setError(null);
      void queryClient.invalidateQueries({ queryKey: queryKeys.me });
    },
    onError: (caught) => setError(caught.message),
  });

  return (
    <button
      type="button"
      onClick={() => topUp.mutate()}
      disabled={topUp.isPending}
      title={error ?? "Development only: a test cash-in of Rs. 5,000"}
      className="flex flex-col items-center gap-2 rounded-2xl border border-dashed border-amber-300 bg-amber-50 p-3 text-center text-xs font-medium text-amber-900 hover:bg-amber-100 disabled:opacity-60"
    >
      <span className="grid h-10 w-10 place-items-center rounded-full bg-amber-100">
        <FlaskConical className="h-5 w-5" aria-hidden />
      </span>
      {topUp.isPending ? "Adding…" : error ? "Top-up failed" : "Add Rs. 5,000"}
    </button>
  );
}
