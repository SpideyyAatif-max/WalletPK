"use client";

import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { CheckCircle2, Loader2, XCircle } from "lucide-react";
import Link from "next/link";
import { useEffect, useState, type FormEvent } from "react";
import { MpinConfirm } from "@/components/mpin-confirm";
import { StepUpPrompt } from "@/components/step-up-prompt";
import { Button } from "@/components/ui/button";
import { Alert, Card } from "@/components/ui/card";
import { Field, Input } from "@/components/ui/input";
import { newIdempotencyKey } from "@/lib/api";
import { formatDay, formatTime } from "@/lib/dates";
import { formatMoney, formatPaisa, parseRupees } from "@/lib/money";
import { formatLocal, toE164 } from "@/lib/phone";
import { useSession } from "@/lib/session";
import { isFinal, paymentKeys, paymentsApi } from "@/lib/payments";
import { needsStepUp, queryKeys, walletApi } from "@/lib/wallet-api";
import { useBalance } from "@/lib/wallet-queries";

/** `retryKey` is the key a step-up retry is sent with; its MPIN confirmation is taken together with the first. */
type Draft = { to: string; amountPaisa: number; note: string; idempotencyKey: string; retryKey: string };
type Attempt = Draft & { confirmation: string };

/**
 * Send money to another WalletPK number: enter → review the fee → MPIN → send → receipt. The send goes through
 * Payments (M6.1), which asks the Ledger to move the money in one journal; the answer comes back in a
 * moment, so the receipt waits for it. If the fraud engine wants it confirmed (step-up OTP), the user types the
 * code texted to them and the same transfer is sent again (with a new key: the refused one is final), using the
 * second MPIN confirmation taken on the review screen, so the MPIN is not asked twice.
 */
export default function SendMoney() {
  const { user } = useSession();
  const balance = useBalance();
  const queryClient = useQueryClient();

  const [to, setTo] = useState("");
  const [amount, setAmount] = useState("");
  const [note, setNote] = useState("");
  const [error, setError] = useState<string | null>(null);
  const [draft, setDraft] = useState<Draft | null>(null);
  const [sentId, setSentId] = useState<string | null>(null);
  const [confirming, setConfirming] = useState(false);
  const [retry, setRetry] = useState<{ key: string; token: string } | null>(null);

  const quote = useQuery({
    queryKey: queryKeys.quote(draft?.amountPaisa ?? 0),
    queryFn: () => walletApi.quote(draft!.amountPaisa),
    enabled: !!draft,
  });

  const send = useMutation({
    // The same key for every retry of this review screen: the server sends the money at most once.
    mutationFn: (current: Attempt) =>
      paymentsApi.send(
        { toWalletNumber: current.to, amountPaisa: current.amountPaisa, note: current.note || undefined },
        current.idempotencyKey,
        current.confirmation,
      ),
    onSuccess: (result) => {
      setSentId(result.id);
      setDraft(null);
      setConfirming(false);
    },
  });

  // The Ledger's answer: poll until the transfer is final, then refresh the balance and history.
  const sent = useQuery({
    queryKey: paymentKeys.p2pTransfer(sentId ?? ""),
    queryFn: () => paymentsApi.p2pTransfer(sentId!),
    enabled: !!sentId,
    refetchInterval: (query) => (query.state.data && isFinal(query.state.data.status) ? false : 500),
  });
  const receipt = sent.data && isFinal(sent.data.status) ? sent.data : null;
  const finishedId = receipt?.id;
  useEffect(() => {
    if (finishedId) void queryClient.invalidateQueries({ queryKey: queryKeys.wallet });
  }, [finishedId, queryClient]);

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
    if (note.length > 140) return setError("The note is at most 140 characters.");
    setConfirming(false);
    setDraft({ to: e164, amountPaisa: paisa, note: note.trim(), idempotencyKey: newIdempotencyKey(), retryKey: newIdempotencyKey() });
  }

  if ((sentId && !receipt) || (send.isPending && !draft)) {
    return (
      <Card className="space-y-4 text-center">
        <Loader2 className="mx-auto h-12 w-12 animate-spin text-brand-600" aria-hidden />
        <p className="text-sm text-ink-600">Sending…</p>
        {sent.error && <Alert>{sent.error.message}</Alert>}
      </Card>
    );
  }

  if (receipt && receipt.status === "Failed" && needsStepUp(receipt.failureCode) && send.variables) {
    const refused = send.variables;
    return (
      <StepUpPrompt
        reason={receipt.failureReason}
        onCancel={() => setSentId(null)}
        onConfirmed={() => {
          setSentId(null);
          if (retry) {
            send.mutate({ ...refused, idempotencyKey: retry.key, confirmation: retry.token });
            setRetry(null);
          } else {
            // Already retried once: confirm the next attempt with the MPIN again.
            setDraft({ ...refused, idempotencyKey: newIdempotencyKey(), retryKey: newIdempotencyKey() });
            setConfirming(true);
          }
        }}
      />
    );
  }

  if (receipt && receipt.status === "Failed") {
    return (
      <Card className="space-y-5 text-center">
        <XCircle className="mx-auto h-14 w-14 text-red-600" aria-hidden />
        <div>
          <p className="text-lg font-semibold">Not sent</p>
          <p className="mt-1 text-sm text-ink-600">{receipt.failureReason}</p>
          <p className="mt-1 text-xs text-ink-400">No money left your wallet.</p>
        </div>
        <Button variant="outline" className="w-full" onClick={() => setSentId(null)}>
          Try again
        </Button>
      </Card>
    );
  }

  if (receipt) {
    const total = receipt.amountPaisa + receipt.feePaisa + receipt.taxPaisa;
    return (
      <Card className="space-y-5 text-center">
        <CheckCircle2 className="mx-auto h-14 w-14 text-brand-600" aria-hidden />
        <div>
          <p className="text-sm text-ink-500">Sent to {formatLocal(receipt.toWalletNumber)}</p>
          <p className="tabular mt-1 text-3xl font-bold">{formatPaisa(receipt.amountPaisa)}</p>
          <p className="mt-1 text-xs text-ink-500">
            {formatDay(receipt.completedAt ?? receipt.updatedAt)}, {formatTime(receipt.completedAt ?? receipt.updatedAt)}
          </p>
        </div>
        <dl className="space-y-2 rounded-xl bg-ink-50 p-4 text-left text-sm">
          <Row label="Fee" value={receipt.feePaisa === 0 ? "Free" : formatPaisa(receipt.feePaisa)} />
          {receipt.feePaisa > 0 && <Row label="Tax on fee" value={formatPaisa(receipt.taxPaisa)} />}
          <Row label="Total debited" value={formatPaisa(total)} strong />
          {balance.data && <Row label="Balance now" value={formatMoney(balance.data.balance)} />}
          {receipt.note && <Row label="Note" value={receipt.note} />}
          <Row label="Transaction ID" value={<span className="font-mono text-xs">{receipt.id}</span>} />
        </dl>
        <div className="grid grid-cols-2 gap-3">
          <Button variant="outline" onClick={() => { setSentId(null); setAmount(""); setNote(""); }}>
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
          {draft.note && <Row label="Note" value={draft.note} />}
          <Row label="Fee" value={quote.data ? (quote.data.isFree ? "Free" : formatMoney(quote.data.fee)) : "…"} />
          {quote.data && !quote.data.isFree && <Row label="Tax on fee (16%)" value={formatMoney(quote.data.tax)} />}
          <Row label="Total" value={quote.data ? formatMoney(quote.data.totalDebit) : "…"} strong />
          {after !== undefined && <Row label="Balance after" value={formatPaisa(after)} />}
        </dl>
        {quote.error && <Alert>{quote.error.message}</Alert>}
        {after !== undefined && after < 0 && <Alert>Not enough balance for this transfer.</Alert>}
        {confirming ? (
          <MpinConfirm
            bindings={[draft.idempotencyKey, draft.retryKey]}
            action={`Send ${formatPaisa(draft.amountPaisa)}`}
            busy={send.isPending}
            error={send.error?.message}
            onCancel={() => setConfirming(false)}
            onConfirmed={([confirmation, retryToken]) => {
              setRetry(retryToken ? { key: draft.retryKey, token: retryToken } : null);
              send.mutate({ ...draft, confirmation });
            }}
          />
        ) : (
          <div className="grid grid-cols-2 gap-3">
            <Button variant="outline" onClick={() => setDraft(null)}>
              Edit
            </Button>
            <Button onClick={() => setConfirming(true)} disabled={!quote.data || (after !== undefined && after < 0)}>
              Confirm
            </Button>
          </div>
        )}
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
        <Field label="Note (optional)" htmlFor="note">
          <Input id="note" autoComplete="off" maxLength={140} placeholder="Rent, lunch…" value={note} onChange={(event) => setNote(event.target.value)} />
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
