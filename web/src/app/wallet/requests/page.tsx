"use client";

import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { Plus, Trash2, Users } from "lucide-react";
import { useState, type FormEvent } from "react";
import { StatusPill } from "@/components/payment-status";
import { Button } from "@/components/ui/button";
import { Alert, Card } from "@/components/ui/card";
import { Field, Input } from "@/components/ui/input";
import { formatDay } from "@/lib/dates";
import { formatPaisa, parseRupees } from "@/lib/money";
import { equalShares, paymentKeys, paymentsApi, type MoneyRequest, type Split } from "@/lib/payments";
import { formatLocal, toE164 } from "@/lib/phone";
import { useSession } from "@/lib/session";
import { cn } from "@/lib/utils";
import { queryKeys } from "@/lib/wallet-api";

type Tab = "incoming" | "outgoing" | "splits";

/** Request money (M6.2): what others ask of me, what I asked, and split bills. */
export default function Requests() {
  const [tab, setTab] = useState<Tab>("incoming");
  const tabs: { id: Tab; label: string }[] = [
    { id: "incoming", label: "To pay" },
    { id: "outgoing", label: "I asked" },
    { id: "splits", label: "Split bills" },
  ];

  return (
    <div className="space-y-5">
      <div className="flex rounded-xl bg-ink-100 p-1" role="tablist">
        {tabs.map(({ id, label }) => (
          <button
            key={id}
            role="tab"
            aria-selected={tab === id}
            onClick={() => setTab(id)}
            className={cn("flex-1 rounded-lg py-2 text-sm font-semibold", tab === id ? "bg-white text-ink-900 shadow-sm" : "text-ink-600")}
          >
            {label}
          </button>
        ))}
      </div>
      {tab === "incoming" && <RequestList direction="incoming" />}
      {tab === "outgoing" && (
        <>
          <NewRequest />
          <RequestList direction="outgoing" />
        </>
      )}
      {tab === "splits" && (
        <>
          <NewSplit />
          <SplitList />
        </>
      )}
    </div>
  );
}

/** Refreshes while anything in the list is still moving (being paid). */
function useRequests(direction: "incoming" | "outgoing") {
  return useQuery({
    queryKey: paymentKeys.requests(direction),
    queryFn: () => paymentsApi.requests(direction),
    refetchInterval: (query) => (query.state.data?.some((request) => request.status === "Paying") ? 700 : 10_000),
  });
}

function RequestList({ direction }: { direction: "incoming" | "outgoing" }) {
  const requests = useRequests(direction);
  if (requests.isPending) return <p className="text-center text-sm text-ink-500">Loading…</p>;
  if (requests.error) return <Alert>{requests.error.message}</Alert>;
  if (requests.data.length === 0) {
    return <p className="py-6 text-center text-sm text-ink-500">{direction === "incoming" ? "Nobody has asked you for money." : "You have not asked anyone for money yet."}</p>;
  }
  return (
    <ul className="space-y-3">
      {requests.data.map((request) => (
        <RequestCard key={request.id} request={request} />
      ))}
    </ul>
  );
}

function RequestCard({ request }: { request: MoneyRequest }) {
  const queryClient = useQueryClient();
  const incoming = request.direction === "incoming";
  const other = incoming ? request.requesterWalletNumber : request.payerWalletNumber;

  const act = useMutation({
    mutationFn: (action: "pay" | "decline" | "cancel") =>
      action === "pay" ? paymentsApi.pay(request.id) : action === "decline" ? paymentsApi.decline(request.id) : paymentsApi.cancel(request.id),
    onSettled: () => {
      void queryClient.invalidateQueries({ queryKey: ["payments"] });
      void queryClient.invalidateQueries({ queryKey: queryKeys.wallet });
    },
  });

  return (
    <li>
      <Card className="space-y-3 p-4">
        <div className="flex items-start justify-between gap-3">
          <div className="min-w-0">
            <p className="text-sm text-ink-500">{incoming ? `${formatLocal(other)} asks you for` : `You asked ${formatLocal(other)} for`}</p>
            <p className="tabular text-2xl font-bold">{formatPaisa(request.amountPaisa)}</p>
            {(request.splitTitle ?? request.note) && <p className="text-sm text-ink-700">{request.splitTitle ?? request.note}</p>}
            <p className="text-xs text-ink-400">
              {formatDay(request.requestedAt)}
              {request.status === "Pending" && ` · expires ${formatDay(request.expiresAt)}`}
            </p>
          </div>
          <StatusPill status={request.status} />
        </div>
        {request.status === "Pending" && request.lastFailureReason && <Alert>Payment failed: {request.lastFailureReason}</Alert>}
        {request.status === "Declined" && request.declineReason && <p className="text-sm text-ink-600">“{request.declineReason}”</p>}
        {act.error && <Alert>{act.error.message}</Alert>}
        {request.status === "Pending" && incoming && (
          <div className="grid grid-cols-2 gap-3">
            <Button variant="outline" onClick={() => act.mutate("decline")} disabled={act.isPending}>
              Decline
            </Button>
            <Button onClick={() => act.mutate("pay")} loading={act.isPending && act.variables === "pay"}>
              Pay {formatPaisa(request.amountPaisa)}
            </Button>
          </div>
        )}
        {request.status === "Pending" && !incoming && (
          <Button variant="ghost" size="sm" onClick={() => act.mutate("cancel")} disabled={act.isPending}>
            Cancel request
          </Button>
        )}
      </Card>
    </li>
  );
}

function NewRequest() {
  const { user } = useSession();
  const queryClient = useQueryClient();
  const [from, setFrom] = useState("");
  const [amount, setAmount] = useState("");
  const [note, setNote] = useState("");
  const [error, setError] = useState<string | null>(null);

  const ask = useMutation({
    mutationFn: paymentsApi.ask,
    onSuccess: () => {
      setFrom("");
      setAmount("");
      setNote("");
      void queryClient.invalidateQueries({ queryKey: paymentKeys.requests("outgoing") });
    },
  });

  function submit(event: FormEvent) {
    event.preventDefault();
    const e164 = toE164(from);
    const paisa = parseRupees(amount);
    if (!e164) return setError("Enter their mobile number, for example 0300 1234567.");
    if (e164 === user?.phoneNumber) return setError("You cannot ask yourself for money.");
    if (!paisa || paisa < 100) return setError("Enter an amount of at least Rs. 1.");
    setError(null);
    ask.mutate({ fromWalletNumber: e164, amountPaisa: paisa, note: note.trim() || undefined });
  }

  return (
    <Card className="space-y-4">
      <h2 className="text-base font-semibold">Ask for money</h2>
      <form onSubmit={submit} className="space-y-3" noValidate>
        <Field label="From (mobile number)" htmlFor="from">
          <Input id="from" inputMode="tel" autoComplete="off" placeholder="0300 1234567" value={from} onChange={(event) => setFrom(event.target.value)} />
        </Field>
        <div className="grid grid-cols-2 gap-3">
          <Field label="Amount (Rs.)" htmlFor="request-amount">
            <Input id="request-amount" inputMode="decimal" autoComplete="off" value={amount} onChange={(event) => setAmount(event.target.value)} className="tabular" />
          </Field>
          <Field label="What for" htmlFor="request-note">
            <Input id="request-note" autoComplete="off" maxLength={140} placeholder="Lunch" value={note} onChange={(event) => setNote(event.target.value)} />
          </Field>
        </div>
        {(error ?? ask.error?.message) && <Alert>{error ?? ask.error?.message}</Alert>}
        {ask.isSuccess && <Alert tone="success">Request sent. They will see it under “To pay”.</Alert>}
        <Button type="submit" className="w-full" loading={ask.isPending}>
          Send request
        </Button>
      </form>
    </Card>
  );
}

function NewSplit() {
  const { user } = useSession();
  const queryClient = useQueryClient();
  const [title, setTitle] = useState("");
  const [total, setTotal] = useState("");
  const [people, setPeople] = useState<string[]>([""]);
  const [includeMe, setIncludeMe] = useState(true);
  const [error, setError] = useState<string | null>(null);

  const split = useMutation({
    mutationFn: paymentsApi.split,
    onSuccess: () => {
      setTitle("");
      setTotal("");
      setPeople([""]);
      void queryClient.invalidateQueries({ queryKey: paymentKeys.splits });
    },
  });

  const totalPaisa = parseRupees(total) ?? 0;
  const numbers = people.map(toE164);
  const preview = equalShares(totalPaisa, people.length, includeMe);

  function submit(event: FormEvent) {
    event.preventDefault();
    if (!title.trim()) return setError("Give the bill a name, for example “Dinner at Monal”.");
    if (totalPaisa < 100) return setError("Enter the bill's total.");
    if (numbers.some((number) => !number)) return setError("Every person needs a valid mobile number.");
    if (numbers.includes(user?.phoneNumber ?? null)) return setError("Leave yourself out of the list; use “Include my share”.");
    if (new Set(numbers).size !== numbers.length) return setError("Each person only once.");
    if (preview.each.some((share) => share < 100)) return setError("Each share must be at least Rs. 1.");
    setError(null);
    split.mutate({ title: title.trim(), totalPaisa, participants: numbers.map((walletNumber) => ({ walletNumber: walletNumber! })), includeMe });
  }

  return (
    <Card className="space-y-4">
      <h2 className="flex items-center gap-2 text-base font-semibold">
        <Users className="h-5 w-5 text-brand-700" aria-hidden /> Split a bill
      </h2>
      <form onSubmit={submit} className="space-y-3" noValidate>
        <div className="grid grid-cols-2 gap-3">
          <Field label="What" htmlFor="split-title">
            <Input id="split-title" autoComplete="off" maxLength={80} placeholder="Dinner" value={title} onChange={(event) => setTitle(event.target.value)} />
          </Field>
          <Field label="Total (Rs.)" htmlFor="split-total">
            <Input id="split-total" inputMode="decimal" autoComplete="off" value={total} onChange={(event) => setTotal(event.target.value)} className="tabular" />
          </Field>
        </div>
        <div className="space-y-2">
          <p className="text-sm font-medium text-ink-700">With</p>
          {people.map((person, index) => (
            <div key={index} className="flex items-center gap-2">
              <Input
                aria-label={`Person ${index + 1}`}
                inputMode="tel"
                autoComplete="off"
                placeholder="0300 1234567"
                value={person}
                onChange={(event) => setPeople((current) => current.map((value, i) => (i === index ? event.target.value : value)))}
              />
              <span className="tabular w-24 shrink-0 text-right text-sm text-ink-600">{totalPaisa > 0 ? formatPaisa(preview.each[index] ?? 0) : ""}</span>
              {people.length > 1 && (
                <button type="button" onClick={() => setPeople((current) => current.filter((_, i) => i !== index))} className="text-ink-400 hover:text-red-600" aria-label={`Remove person ${index + 1}`}>
                  <Trash2 className="h-4 w-4" />
                </button>
              )}
            </div>
          ))}
          {people.length < 20 && (
            <Button type="button" variant="ghost" size="sm" onClick={() => setPeople((current) => [...current, ""])}>
              <Plus className="h-4 w-4" aria-hidden /> Add person
            </Button>
          )}
        </div>
        <label className="flex items-center justify-between gap-3 text-sm">
          <span>
            <input type="checkbox" className="mr-2 accent-brand-600" checked={includeMe} onChange={(event) => setIncludeMe(event.target.checked)} />
            Include my share
          </span>
          {includeMe && totalPaisa > 0 && <span className="tabular text-ink-600">Mine: {formatPaisa(preview.mine)}</span>}
        </label>
        {(error ?? split.error?.message) && <Alert>{error ?? split.error?.message}</Alert>}
        <Button type="submit" className="w-full" loading={split.isPending}>
          Ask everyone
        </Button>
      </form>
    </Card>
  );
}

function SplitList() {
  const splits = useQuery({
    queryKey: paymentKeys.splits,
    queryFn: paymentsApi.splits,
    refetchInterval: (query) => (query.state.data?.some((split) => split.shares.some((share) => share.status === "Paying")) ? 700 : 10_000),
  });
  if (splits.isPending) return <p className="text-center text-sm text-ink-500">Loading…</p>;
  if (splits.error) return <Alert>{splits.error.message}</Alert>;
  if (splits.data.length === 0) return <p className="py-4 text-center text-sm text-ink-500">No split bills yet.</p>;
  return (
    <ul className="space-y-3">
      {splits.data.map((split) => (
        <SplitCard key={split.id} split={split} />
      ))}
    </ul>
  );
}

function SplitCard({ split }: { split: Split }) {
  return (
    <li>
      <Card className="space-y-3 p-4">
        <div className="flex items-start justify-between gap-3">
          <div>
            <p className="font-semibold">{split.title}</p>
            <p className="text-xs text-ink-500">
              {formatDay(split.createdAt)} · total {formatPaisa(split.totalPaisa)}
              {split.ownSharePaisa > 0 && ` · mine ${formatPaisa(split.ownSharePaisa)}`}
            </p>
          </div>
          <div className="text-right">
            <p className="tabular text-sm font-semibold">{formatPaisa(split.collectedPaisa)}</p>
            <p className="text-xs text-ink-500">
              {split.paidCount} of {split.shares.length} paid
            </p>
          </div>
        </div>
        <ul className="divide-y divide-ink-100 rounded-xl bg-ink-50 px-3">
          {split.shares.map((share) => (
            <li key={share.requestId} className="flex items-center justify-between gap-3 py-2 text-sm">
              <span>{formatLocal(share.walletNumber)}</span>
              <span className="flex items-center gap-2">
                <span className="tabular">{formatPaisa(share.amountPaisa)}</span>
                <StatusPill status={share.status} />
              </span>
            </li>
          ))}
        </ul>
      </Card>
    </li>
  );
}
