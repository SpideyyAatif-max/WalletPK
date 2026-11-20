"use client";

import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { ArrowLeft, Building2, ChevronRight } from "lucide-react";
import { useState, type FormEvent } from "react";
import { StatusPill, Timeline } from "@/components/payment-status";
import { StepUpPrompt } from "@/components/step-up-prompt";
import { Button } from "@/components/ui/button";
import { Alert, Card } from "@/components/ui/card";
import { Field, Input } from "@/components/ui/input";
import { newIdempotencyKey } from "@/lib/api";
import { formatDay, formatTime } from "@/lib/dates";
import { formatIban, normalizeIban } from "@/lib/iban";
import { formatMoney, formatPaisa, parseRupees } from "@/lib/money";
import { isFinal, paymentKeys, paymentsApi, type Bank, type Beneficiary } from "@/lib/payments";
import { toE164 } from "@/lib/phone";
import { cn } from "@/lib/utils";
import { needsStepUp, queryKeys } from "@/lib/wallet-api";
import { useBalance } from "@/lib/wallet-queries";

type Draft = { beneficiary: Beneficiary; amountPaisa: number; note: string; idempotencyKey: string };

/**
 * Send to a bank account over Raast (M6.4): look the account up (IBAN or Raast ID), check the name,
 * send, then watch the transfer move: reserved, sent, queued if the bank is down, delivered or returned.
 */
export default function SendToBank() {
  const [openId, setOpenId] = useState<string | null>(null);
  return openId ? <Tracker id={openId} onBack={() => setOpenId(null)} /> : <NewTransfer onSent={setOpenId} />;
}

function NewTransfer({ onSent }: { onSent: (id: string) => void }) {
  const balance = useBalance();
  const banks = useQuery({ queryKey: paymentKeys.banks, queryFn: paymentsApi.banks, refetchInterval: 15_000 });
  const recent = useQuery({ queryKey: paymentKeys.bankTransfers, queryFn: () => paymentsApi.bankTransfers(5) });

  const [target, setTarget] = useState("");
  const [amount, setAmount] = useState("");
  const [note, setNote] = useState("");
  const [error, setError] = useState<string | null>(null);
  const [beneficiary, setBeneficiary] = useState<Beneficiary | null>(null);
  const [draft, setDraft] = useState<Draft | null>(null);

  const lookup = useMutation({
    mutationFn: (value: string) => {
      const iban = normalizeIban(value);
      return paymentsApi.lookup(iban ? { iban } : { raastId: toE164(value)! });
    },
    onSuccess: (found) => {
      setBeneficiary(found);
      setError(null);
    },
  });

  const send = useMutation({
    mutationFn: (current: Draft) =>
      paymentsApi.sendToBank(
        { toIban: current.beneficiary.iban, amountPaisa: current.amountPaisa, beneficiaryTitle: current.beneficiary.title, note: current.note || undefined },
        current.idempotencyKey,
      ),
    onSuccess: (transfer) => onSent(transfer.id),
  });

  function find(event: FormEvent) {
    event.preventDefault();
    if (!normalizeIban(target) && !toE164(target)) {
      return setError("Enter a Pakistani IBAN (PK.. 24 characters) or the mobile number linked to the account (Raast ID).");
    }
    setError(null);
    setBeneficiary(null);
    lookup.mutate(target);
  }

  function review(event: FormEvent) {
    event.preventDefault();
    const paisa = parseRupees(amount);
    if (!paisa) return setError("Enter an amount, for example 5,000.");
    if (paisa < 100) return setError("The smallest transfer is Rs. 1.");
    if (note.length > 140) return setError("The note is at most 140 characters.");
    setError(null);
    send.reset();
    setDraft({ beneficiary: beneficiary!, amountPaisa: paisa, note: note.trim(), idempotencyKey: newIdempotencyKey() });
  }

  const bankOf = (code: string): Bank | undefined => banks.data?.find((bank) => bank.code === code);

  if (draft) {
    const bank = bankOf(draft.beneficiary.bank);
    return (
      <Card className="space-y-5">
        <div>
          <h1 className="text-xl font-bold">Review</h1>
          <p className="mt-1 text-sm text-ink-600">
            To {draft.beneficiary.title}, {draft.beneficiary.bankName}
          </p>
        </div>
        <dl className="space-y-2 rounded-xl bg-ink-50 p-4 text-sm">
          <Row label="Account" value={<span className="font-mono text-xs">{draft.beneficiary.maskedIban}</span>} />
          <Row label="Amount" value={formatPaisa(draft.amountPaisa)} strong />
          <Row label="Fee" value="Free (Raast)" />
          {draft.note && <Row label="Note" value={draft.note} />}
        </dl>
        {bank && bank.status !== "Available" && (
          <Alert tone="info">
            {bank.name} is {bank.status === "Down" ? "unavailable" : "recovering"} right now. Your transfer will be queued and sent automatically as soon as the bank is back; the money stays reserved in your wallet meanwhile.
          </Alert>
        )}
        {send.error && <Alert>{send.error.message}</Alert>}
        <div className="grid grid-cols-2 gap-3">
          <Button variant="outline" onClick={() => setDraft(null)} disabled={send.isPending}>
            Edit
          </Button>
          <Button onClick={() => send.mutate(draft)} loading={send.isPending}>
            Send {formatPaisa(draft.amountPaisa)}
          </Button>
        </div>
      </Card>
    );
  }

  return (
    <div className="space-y-5">
      <Card className="space-y-5">
        <div>
          <h1 className="text-xl font-bold">Send to bank</h1>
          <p className="mt-1 text-sm text-ink-600">
            Any account on Raast, free. Balance: <span className="tabular font-medium">{balance.data ? formatMoney(balance.data.balance) : "…"}</span>
          </p>
        </div>

        <form onSubmit={find} className="space-y-3" noValidate>
          <Field label="IBAN or Raast ID" htmlFor="target" hint="For example PK36 SCBL 0000 0011 2345 6702, or 0300 1234567.">
            <Input id="target" autoComplete="off" value={target} onChange={(event) => { setTarget(event.target.value); setBeneficiary(null); }} autoFocus />
          </Field>
          {!beneficiary && (
            <Button type="submit" variant="secondary" className="w-full" loading={lookup.isPending}>
              Find account
            </Button>
          )}
        </form>

        {lookup.error && !beneficiary && <Alert>{lookup.error.message}</Alert>}

        {beneficiary && (
          <div className="space-y-4">
            <div className="flex items-center gap-3 rounded-xl bg-ink-50 p-4">
              <Building2 className="h-8 w-8 shrink-0 text-brand-700" aria-hidden />
              <div className="min-w-0">
                <p className="font-semibold">{beneficiary.title}</p>
                <p className="text-sm text-ink-600">{beneficiary.bankName}</p>
                <p className="font-mono text-xs text-ink-500">{formatIban(beneficiary.iban)}</p>
              </div>
            </div>
            {!beneficiary.canReceive ? (
              <Alert>This account is {beneficiary.accountStatus.toLowerCase()} and cannot receive money.</Alert>
            ) : (
              <form onSubmit={review} className="space-y-4" noValidate>
                <Field label="Amount (Rs.)" htmlFor="amount">
                  <Input id="amount" inputMode="decimal" autoComplete="off" placeholder="0.00" value={amount} onChange={(event) => setAmount(event.target.value)} className="tabular text-lg" />
                </Field>
                <Field label="Note (optional)" htmlFor="note">
                  <Input id="note" autoComplete="off" maxLength={140} value={note} onChange={(event) => setNote(event.target.value)} />
                </Field>
                <Button type="submit" size="lg" className="w-full">
                  Review
                </Button>
              </form>
            )}
          </div>
        )}
        {error && <Alert>{error}</Alert>}
      </Card>

      {banks.data && (
        <Card className="p-4">
          <h2 className="mb-2 px-1 text-sm font-semibold text-ink-700">Banks right now</h2>
          <ul className="grid grid-cols-2 gap-2">
            {banks.data.map((bank) => (
              <li key={bank.code} className="flex items-center gap-2 rounded-lg px-2 py-1.5 text-sm">
                <span aria-hidden className={cn("h-2 w-2 rounded-full", bank.status === "Available" ? "bg-brand-500" : bank.status === "Degraded" ? "bg-amber-500" : "bg-red-500")} />
                <span className="truncate">{bank.name}</span>
                {bank.status !== "Available" && <span className="text-xs text-ink-500">{bank.status}</span>}
              </li>
            ))}
          </ul>
        </Card>
      )}

      {recent.data && recent.data.length > 0 && (
        <Card className="p-2">
          <h2 className="px-3 pt-2 pb-1 text-sm font-semibold text-ink-700">Recent bank transfers</h2>
          <ul>
            {recent.data.map((transfer) => (
              <li key={transfer.id}>
                <button onClick={() => onSent(transfer.id)} className="flex w-full items-center gap-3 rounded-xl px-3 py-2.5 text-left hover:bg-ink-50">
                  <div className="min-w-0 flex-1">
                    <p className="truncate text-sm font-medium">{transfer.beneficiaryTitle ?? transfer.toIban}</p>
                    <p className="text-xs text-ink-500">
                      {transfer.toBankName} · {formatDay(transfer.requestedAt)}
                    </p>
                  </div>
                  <div className="text-right">
                    <p className="tabular text-sm font-semibold">{formatPaisa(transfer.amountPaisa)}</p>
                    <StatusPill status={transfer.status} />
                  </div>
                  <ChevronRight className="h-4 w-4 text-ink-400" aria-hidden />
                </button>
              </li>
            ))}
          </ul>
        </Card>
      )}
    </div>
  );
}

/** One transfer, refreshed every second until it is final. */
function Tracker({ id, onBack }: { id: string; onBack: () => void }) {
  const queryClient = useQueryClient();
  const transfer = useQuery({
    queryKey: paymentKeys.bankTransfer(id),
    queryFn: async () => {
      const current = await paymentsApi.bankTransfer(id);
      if (isFinal(current.status)) {
        void queryClient.invalidateQueries({ queryKey: queryKeys.wallet });
        void queryClient.invalidateQueries({ queryKey: paymentKeys.bankTransfers });
      }
      return current;
    },
    refetchInterval: (query) => (query.state.data && isFinal(query.state.data.status) ? false : 1_000),
  });

  const data = transfer.data;
  return (
    <div className="space-y-4">
      <button onClick={onBack} className="inline-flex items-center gap-1 text-sm font-medium text-brand-700 hover:underline">
        <ArrowLeft className="h-4 w-4" aria-hidden /> Send to bank
      </button>
      {transfer.error && <Alert>{transfer.error.message}</Alert>}
      {data && (
        <Card className="space-y-5">
          <div className="flex items-start justify-between gap-3">
            <div className="min-w-0">
              <p className="text-sm text-ink-500">To {data.beneficiaryTitle ?? "beneficiary"}, {data.toBankName}</p>
              <p className="tabular mt-1 text-3xl font-bold">{formatPaisa(data.amountPaisa)}</p>
              <p className="font-mono text-xs text-ink-500">{data.toIban}</p>
            </div>
            <StatusPill status={data.status} />
          </div>
          {data.status === "Queued" && (
            <Alert tone="info">{data.toBankName} is unavailable. Your transfer is queued and will be sent automatically when the bank is back; the money stays reserved.</Alert>
          )}
          {data.status === "Failed" && needsStepUp(data.failureCode) && <StepUpPrompt reason={data.failureReason} />}
          {data.status === "Failed" && !needsStepUp(data.failureCode) && <Alert>{data.failureReason} No money left your wallet.</Alert>}
          {data.timeline && <Timeline steps={data.timeline} status={data.status} />}
          <dl className="space-y-2 rounded-xl bg-ink-50 p-4 text-sm">
            {data.raastReference && <Row label="Raast reference" value={<span className="font-mono text-xs">{data.raastReference}</span>} />}
            <Row label="Requested" value={`${formatDay(data.requestedAt)}, ${formatTime(data.requestedAt)}`} />
            <Row label="Transfer ID" value={<span className="font-mono text-xs">{data.id}</span>} />
          </dl>
        </Card>
      )}
    </div>
  );
}

function Row({ label, value, strong = false }: { label: string; value: React.ReactNode; strong?: boolean }) {
  return (
    <div className="flex items-center justify-between gap-4">
      <dt className="text-ink-600">{label}</dt>
      <dd className={strong ? "tabular font-semibold text-ink-900" : "tabular text-right text-ink-900"}>{value}</dd>
    </div>
  );
}
