"use client";

import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { ArrowLeft, Bookmark, ChevronRight, Flame, Phone, Trash2, Zap } from "lucide-react";
import { useState, type FormEvent } from "react";
import { StatusPill, Timeline } from "@/components/payment-status";
import { StepUpPrompt } from "@/components/step-up-prompt";
import { Button } from "@/components/ui/button";
import { Alert, Card } from "@/components/ui/card";
import { Field, Input } from "@/components/ui/input";
import { billKeys, billsApi, cleanConsumerNumber, dueLabel, isBillFinal, isConsumerNumber, type Bill, type Biller } from "@/lib/bills";
import { dayKey, formatDay, formatTime } from "@/lib/dates";
import { formatMoney, formatPaisa } from "@/lib/money";
import { cn } from "@/lib/utils";
import { needsStepUp, queryKeys } from "@/lib/wallet-api";
import { useBalance } from "@/lib/wallet-queries";

const icons: Record<string, React.ReactNode> = {
  Electricity: <Zap className="h-5 w-5" aria-hidden />,
  Gas: <Flame className="h-5 w-5" aria-hidden />,
};

/**
 * Pay a utility bill (M7): pick the biller, look the bill up, pay the payable amount, then watch the
 * payment (reserved, paid, settled; or retried, verified with the biller, returned). Saved billers
 * show their last bill and when it is due.
 */
export default function Bills() {
  const [openId, setOpenId] = useState<string | null>(null);
  return openId ? <Tracker id={openId} onBack={() => setOpenId(null)} /> : <PayBill onPaid={setOpenId} />;
}

function PayBill({ onPaid }: { onPaid: (id: string) => void }) {
  const queryClient = useQueryClient();
  const balance = useBalance();
  const billers = useQuery({ queryKey: billKeys.billers, queryFn: billsApi.billers, refetchInterval: 15_000 });
  const saved = useQuery({ queryKey: billKeys.saved, queryFn: billsApi.saved });
  const recent = useQuery({ queryKey: billKeys.payments, queryFn: () => billsApi.payments(5) });

  const [biller, setBiller] = useState<Biller | null>(null);
  const [number, setNumber] = useState("");
  const [error, setError] = useState<string | null>(null);
  const [bill, setBill] = useState<Bill | null>(null);

  const inquire = useMutation({
    mutationFn: ({ code, consumer }: { code: string; consumer: string }) => billsApi.inquire(code, consumer),
    onSuccess: (found) => setBill(found),
  });

  const pay = useMutation({
    mutationFn: (current: Bill) =>
      billsApi.pay({ biller: current.biller, consumerNumber: current.consumerNumber, amountPaisa: current.payablePaisa, customerName: current.customerName }),
    onSuccess: (payment) => onPaid(payment.id),
  });

  const save = useMutation({
    mutationFn: (current: Bill) => billsApi.save({ biller: current.biller, consumerNumber: current.consumerNumber }),
    onSuccess: () => void queryClient.invalidateQueries({ queryKey: billKeys.saved }),
  });

  const unsave = useMutation({
    mutationFn: billsApi.unsave,
    onSuccess: () => void queryClient.invalidateQueries({ queryKey: billKeys.saved }),
  });

  function choose(next: Biller) {
    setBiller(next);
    setBill(null);
    setError(null);
    inquire.reset();
    pay.reset();
  }

  function lookup(event: FormEvent) {
    event.preventDefault();
    if (!biller) return;
    if (!isConsumerNumber(biller, number)) {
      return setError(`A ${biller.name} ${biller.consumerNumberLabel.toLowerCase()} is ${biller.consumerNumberLength} digits.`);
    }
    setError(null);
    setBill(null);
    inquire.mutate({ code: biller.code, consumer: cleanConsumerNumber(number) });
  }

  function payFromSaved(code: string, consumer: string) {
    const target = billers.data?.find((item) => item.code === code);
    if (!target) return;
    choose(target);
    setNumber(consumer);
    inquire.mutate({ code, consumer });
  }

  const today = dayKey(new Date().toISOString());
  const alreadySaved = bill && saved.data?.some((item) => item.biller === bill.biller && item.consumerNumber === bill.consumerNumber);

  return (
    <div className="space-y-5">
      <Card className="space-y-5">
        <div>
          <h1 className="text-xl font-bold">Pay a bill</h1>
          <p className="mt-1 text-sm text-ink-600">
            Electricity, gas and phone, free. Balance: <span className="tabular font-medium">{balance.data ? formatMoney(balance.data.balance) : "…"}</span>
          </p>
        </div>

        {billers.error && <Alert>{billers.error.message}</Alert>}
        <div className="grid grid-cols-2 gap-2">
          {billers.data?.map((item) => (
            <button
              key={item.code}
              onClick={() => choose(item)}
              className={cn(
                "flex items-center gap-3 rounded-xl border p-3 text-left transition-colors",
                biller?.code === item.code ? "border-brand-500 bg-brand-50" : "border-ink-100 hover:bg-ink-50",
              )}
              aria-pressed={biller?.code === item.code}
            >
              <span className="grid h-9 w-9 shrink-0 place-items-center rounded-full bg-brand-50 text-brand-700">{icons[item.category] ?? <Phone className="h-5 w-5" aria-hidden />}</span>
              <span className="min-w-0">
                <span className="block truncate text-sm font-semibold">{item.name}</span>
                <span className="block text-xs text-ink-500">{item.status === "Available" ? item.category : item.status === "Down" ? "Unavailable" : "Slow"}</span>
              </span>
            </button>
          ))}
        </div>

        {biller && (
          <form onSubmit={lookup} className="space-y-3" noValidate>
            <Field label={`${biller.consumerNumberLabel} (${biller.consumerNumberLength} digits)`} htmlFor="consumer">
              <Input id="consumer" inputMode="numeric" autoComplete="off" value={number} onChange={(event) => { setNumber(event.target.value); setBill(null); }} autoFocus />
            </Field>
            {!bill && (
              <Button type="submit" variant="secondary" className="w-full" loading={inquire.isPending}>
                Get bill
              </Button>
            )}
          </form>
        )}

        {(error ?? inquire.error?.message) && !bill && <Alert>{error ?? inquire.error?.message}</Alert>}

        {bill && (
          <div className="space-y-4">
            <div className="space-y-2 rounded-xl bg-ink-50 p-4 text-sm">
              <div className="flex items-start justify-between gap-3">
                <div>
                  <p className="font-semibold">{bill.customerName}</p>
                  <p className="text-ink-600">
                    {biller?.name} · {bill.consumerNumber}
                  </p>
                </div>
                <StatusPill status={bill.status} />
              </div>
              <Row label="Billing month" value={bill.billingMonth} />
              <Row label="Due date" value={`${formatDay(`${bill.dueDate}T12:00:00+05:00`)} (${dueLabel(bill.dueDate, today)})`} />
              <Row label="Within due date" value={formatPaisa(bill.amountWithinDuePaisa)} />
              <Row label="After due date" value={formatPaisa(bill.amountAfterDuePaisa)} />
              <Row label="Payable now" value={formatPaisa(bill.payablePaisa)} strong />
            </div>
            {bill.status === "Paid" ? (
              <Alert tone="info">This month&apos;s bill is already paid.</Alert>
            ) : (
              <>
                {biller && biller.status !== "Available" && (
                  <Alert tone="info">{biller.name} is {biller.status === "Down" ? "unavailable" : "slow"} right now. We will keep trying; your money stays reserved, and comes back if the bill cannot be paid.</Alert>
                )}
                {pay.error && <Alert>{pay.error.message}</Alert>}
                <Button size="lg" className="w-full" onClick={() => pay.mutate(bill)} loading={pay.isPending}>
                  Pay {formatPaisa(bill.payablePaisa)}
                </Button>
              </>
            )}
            {!alreadySaved && (
              <Button variant="ghost" size="sm" onClick={() => save.mutate(bill)} loading={save.isPending}>
                <Bookmark className="h-4 w-4" aria-hidden /> Save this biller
              </Button>
            )}
          </div>
        )}
      </Card>

      {saved.data && saved.data.length > 0 && (
        <Card className="p-2">
          <h2 className="px-3 pt-2 pb-1 text-sm font-semibold text-ink-700">Saved billers</h2>
          <ul>
            {saved.data.map((item) => (
              <li key={item.id} className="flex items-center gap-2 rounded-xl px-3 py-2.5 hover:bg-ink-50">
                <button onClick={() => payFromSaved(item.biller, item.consumerNumber)} className="flex min-w-0 flex-1 items-center gap-3 text-left">
                  <div className="min-w-0 flex-1">
                    <p className="truncate text-sm font-medium">{item.nickname ?? item.customerName ?? item.consumerNumber}</p>
                    <p className="text-xs text-ink-500">
                      {item.billerName}
                      {item.lastStatus === "Unpaid" && item.lastDueDate && ` · ${dueLabel(item.lastDueDate, today)}`}
                      {item.lastStatus === "Paid" && " · paid this month"}
                    </p>
                  </div>
                  {item.lastPayablePaisa !== null && item.lastStatus === "Unpaid" && <span className="tabular text-sm font-semibold">{formatPaisa(item.lastPayablePaisa)}</span>}
                </button>
                <button onClick={() => unsave.mutate(item.id)} className="p-1 text-ink-400 hover:text-red-600" aria-label={`Remove ${item.nickname ?? item.consumerNumber}`}>
                  <Trash2 className="h-4 w-4" />
                </button>
              </li>
            ))}
          </ul>
        </Card>
      )}

      {recent.data && recent.data.length > 0 && (
        <Card className="p-2">
          <h2 className="px-3 pt-2 pb-1 text-sm font-semibold text-ink-700">Recent bill payments</h2>
          <ul>
            {recent.data.map((payment) => (
              <li key={payment.id}>
                <button onClick={() => onPaid(payment.id)} className="flex w-full items-center gap-3 rounded-xl px-3 py-2.5 text-left hover:bg-ink-50">
                  <div className="min-w-0 flex-1">
                    <p className="truncate text-sm font-medium">{payment.customerName ?? payment.consumerNumber}</p>
                    <p className="text-xs text-ink-500">
                      {payment.billerName} · {formatDay(payment.requestedAt)}
                    </p>
                  </div>
                  <div className="text-right">
                    <p className="tabular text-sm font-semibold">{formatPaisa(payment.amountPaisa)}</p>
                    <StatusPill status={payment.status} />
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

/** One payment, refreshed every second until it is final. */
function Tracker({ id, onBack }: { id: string; onBack: () => void }) {
  const queryClient = useQueryClient();
  const payment = useQuery({
    queryKey: billKeys.payment(id),
    queryFn: async () => {
      const current = await billsApi.payment(id);
      if (isBillFinal(current.status)) {
        void queryClient.invalidateQueries({ queryKey: queryKeys.wallet });
        void queryClient.invalidateQueries({ queryKey: billKeys.payments });
      }
      return current;
    },
    refetchInterval: (query) => (query.state.data && isBillFinal(query.state.data.status) ? false : 1_000),
  });

  const data = payment.data;
  return (
    <div className="space-y-4">
      <button onClick={onBack} className="inline-flex items-center gap-1 text-sm font-medium text-brand-700 hover:underline">
        <ArrowLeft className="h-4 w-4" aria-hidden /> Pay a bill
      </button>
      {payment.error && <Alert>{payment.error.message}</Alert>}
      {data && (
        <Card className="space-y-5">
          <div className="flex items-start justify-between gap-3">
            <div className="min-w-0">
              <p className="text-sm text-ink-500">
                {data.billerName} · {data.consumerNumber}
              </p>
              <p className="tabular mt-1 text-3xl font-bold">{formatPaisa(data.amountPaisa)}</p>
              {data.customerName && <p className="text-sm text-ink-600">{data.customerName}</p>}
            </div>
            <StatusPill status={data.status} />
          </div>
          {data.status === "PendingVerification" && (
            <Alert tone="info">{data.billerName} has not confirmed the payment yet. Your money stays reserved; it is either paid to the biller or returned to you, never both.</Alert>
          )}
          {data.status === "UnderReview" && <Alert tone="info">Our team is checking this payment with {data.billerName}. Your money stays reserved until it is resolved.</Alert>}
          {data.status === "Failed" && needsStepUp(data.failureCode) && <StepUpPrompt reason={data.failureReason} />}
          {data.status === "Failed" && !needsStepUp(data.failureCode) && <Alert>{data.failureReason} The reserved money is back in your wallet.</Alert>}
          {data.timeline && <Timeline steps={data.timeline} status={data.status} />}
          <dl className="space-y-2 rounded-xl bg-ink-50 p-4 text-sm">
            {data.billerReference && <Row label="Biller reference" value={<span className="font-mono text-xs">{data.billerReference}</span>} />}
            {data.billingMonth && <Row label="Billing month" value={data.billingMonth} />}
            <Row label="Requested" value={`${formatDay(data.requestedAt)}, ${formatTime(data.requestedAt)}`} />
            <Row label="Payment ID" value={<span className="font-mono text-xs">{data.id}</span>} />
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
