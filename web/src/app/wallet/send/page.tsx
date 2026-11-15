"use client";

import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { CheckCircle2 } from "lucide-react";
import Link from "next/link";
import { useState, type FormEvent } from "react";
import { Button } from "@/components/ui/button";
import { Alert, Card } from "@/components/ui/card";
import { Field, Input } from "@/components/ui/input";
import { newIdempotencyKey } from "@/lib/api";
import { formatDay, formatTime } from "@/lib/dates";
import { formatMoney, formatPaisa, parseRupees } from "@/lib/money";
import { formatLocal, toE164 } from "@/lib/phone";
import { useSession } from "@/lib/session";
import type { TransferReceipt } from "@/lib/types";
import { queryKeys, walletApi } from "@/lib/wallet-api";
import { useBalance } from "@/lib/wallet-queries";

type Draft = { to: string; amountPaisa: number; idempotencyKey: string };

/** Send money to another WalletPK number: enter → review the fee → send → receipt. */
export default function SendMoney() {
  const { user } = useSession();
  const balance = useBalance();
  const queryClient = useQueryClient();

  const [to, setTo] = useState("");
  const [amount, setAmount] = useState("");
  const [error, setError] = useState<string | null>(null);
  const [draft, setDraft] = useState<Draft | null>(null);
  const [receipt, setReceipt] = useState<TransferReceipt | null>(null);

  const quote = useQuery({
    queryKey: queryKeys.quote(draft?.amountPaisa ?? 0),
    queryFn: () => walletApi.quote(draft!.amountPaisa),
    enabled: !!draft,
  });

  const send = useMutation({
    // The same key for every retry of this review screen: the server sends the money at most once.
    mutationFn: (current: Draft) => walletApi.transfer(current.to, current.amountPaisa, current.idempotencyKey),
    onSuccess: (result) => {
      setReceipt(result);
      setDraft(null);
      void queryClient.invalidateQueries({ queryKey: queryKeys.wallet });
    },
  });

  function review(event: FormEvent) {
    event.preventDefault();
    const e164 = toE164(to);
    const paisa = parseRupees(amount);
    if (!e164) return setError("Enter the recipient's mobile number, for example 0300 1234567.");
    if (e164 === user?.phoneNumber) return setError("That is your own wallet.");
    if (!paisa) return setError("Enter an amount, for example 500 or 1,250.50.");
    if (paisa < 100) return setError("The smallest transfer is Rs. 1.");
    setError(null);
    send.reset();
    setDraft({ to: e164, amountPaisa: paisa, idempotencyKey: newIdempotencyKey() });
  }

  if (receipt) {
    return (
      <Card className="space-y-5 text-center">
        <CheckCircle2 className="mx-auto h-14 w-14 text-brand-600" aria-hidden />
        <div>
          <p className="text-sm text-ink-500">Sent to {formatLocal(receipt.toWalletNumber)}</p>
          <p className="tabular mt-1 text-3xl font-bold">{formatMoney(receipt.amount)}</p>
          <p className="mt-1 text-xs text-ink-500">
            {formatDay(receipt.postedAt)}, {formatTime(receipt.postedAt)}
          </p>
        </div>
        <dl className="space-y-2 rounded-xl bg-ink-50 p-4 text-left text-sm">
          <Row label="Fee" value={formatMoney(receipt.fee)} />
          <Row label="Tax on fee" value={formatMoney(receipt.tax)} />
          <Row label="Total debited" value={formatMoney(receipt.totalDebited)} strong />
          <Row label="Balance now" value={formatMoney(receipt.balanceAfter)} />
          <Row label="Transaction ID" value={<span className="font-mono text-xs">{receipt.transactionId}</span>} />
        </dl>
        <div className="grid grid-cols-2 gap-3">
          <Button variant="outline" onClick={() => { setReceipt(null); setAmount(""); }}>
            Send again
          </Button>
          <Link href="/wallet" className="inline-flex h-11 items-center justify-center rounded-xl bg-brand-600 text-sm font-semibold text-white hover:bg-brand-700">
            Done
          </Link>
        </div>
      </Card>
    );
  }

  if (draft) {
    const total = quote.data?.totalDebit.minorUnits;
    const after = total !== undefined && balance.data ? balance.data.balance.minorUnits - total : undefined;
    return (
      <Card className="space-y-5">
        <div>
          <h1 className="text-xl font-bold">Review</h1>
          <p className="mt-1 text-sm text-ink-600">To {formatLocal(draft.to)}</p>
        </div>
        <dl className="space-y-2 rounded-xl bg-ink-50 p-4 text-sm">
          <Row label="Amount" value={formatPaisa(draft.amountPaisa)} />
          <Row label="Fee" value={quote.data ? (quote.data.isFree ? "Free" : formatMoney(quote.data.fee)) : "…"} />
          {quote.data && !quote.data.isFree && <Row label="Tax on fee (16%)" value={formatMoney(quote.data.tax)} />}
          <Row label="Total" value={quote.data ? formatMoney(quote.data.totalDebit) : "…"} strong />
          {after !== undefined && <Row label="Balance after" value={formatPaisa(after)} />}
        </dl>
        {quote.error && <Alert>{quote.error.message}</Alert>}
        {after !== undefined && after < 0 && <Alert>Not enough balance for this transfer.</Alert>}
        {send.error && <Alert>{send.error.message}</Alert>}
        <div className="grid grid-cols-2 gap-3">
          <Button variant="outline" onClick={() => setDraft(null)} disabled={send.isPending}>
            Edit
          </Button>
          <Button onClick={() => send.mutate(draft)} loading={send.isPending} disabled={!quote.data || (after !== undefined && after < 0)}>
            Send {formatPaisa(draft.amountPaisa)}
          </Button>
        </div>
      </Card>
    );
  }

  return (
    <Card className="space-y-5">
      <div>
        <h1 className="text-xl font-bold">Send money</h1>
        <p className="mt-1 text-sm text-ink-600">
          To any WalletPK number. Balance: <span className="tabular font-medium">{balance.data ? formatMoney(balance.data.balance) : "…"}</span>
        </p>
      </div>
      <form onSubmit={review} className="space-y-4" noValidate>
        <Field label="Recipient's mobile number" htmlFor="to">
          <Input id="to" inputMode="tel" autoComplete="off" placeholder="0300 1234567" value={to} onChange={(event) => setTo(event.target.value)} autoFocus />
        </Field>
        <Field label="Amount (Rs.)" htmlFor="amount" hint="Free up to Rs. 1,000; above that a small fee plus 16% tax.">
          <Input id="amount" inputMode="decimal" autoComplete="off" placeholder="0.00" value={amount} onChange={(event) => setAmount(event.target.value)} className="tabular text-lg" />
        </Field>
        {error && <Alert>{error}</Alert>}
        <Button type="submit" size="lg" className="w-full">
          Review
        </Button>
      </form>
    </Card>
  );
}

function Row({ label, value, strong = false }: { label: string; value: React.ReactNode; strong?: boolean }) {
  return (
    <div className="flex items-center justify-between gap-4">
      <dt className="text-ink-600">{label}</dt>
      <dd className={strong ? "tabular font-semibold text-ink-900" : "tabular text-ink-900"}>{value}</dd>
    </div>
  );
}
