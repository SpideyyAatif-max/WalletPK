"use client";

import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { ChevronDown, ChevronRight } from "lucide-react";
import Link from "next/link";
import { useState, type FormEvent } from "react";
import { PageHeader, ShortId, StateBadge, Table, since } from "@/components/ops/ui";
import { Timeline } from "@/components/payment-status";
import { Button } from "@/components/ui/button";
import { Alert } from "@/components/ui/card";
import { Input } from "@/components/ui/input";
import { formatPaisa } from "@/lib/money";
import { opsApi, opsKeys, type BillDeadLetter, type TopUpDeadLetter } from "@/lib/ops";
import { formatLocal } from "@/lib/phone";
import { cn } from "@/lib/utils";

type Queue = "bills" | "topups";

/**
 * Dead-letter console (M14.C3, FR-4.3): payments the services gave up on and left for a person. Each can be
 * replayed (the step runs again), refunded (the reserved money goes back) or confirmed with the provider's
 * reference from its settlement file. Every action is recorded on the payment's timeline with who did it.
 */
export default function DeadLetters() {
  const [queue, setQueue] = useState<Queue>("bills");
  const bills = useQuery({ queryKey: opsKeys.billDeadLetters, queryFn: opsApi.billDeadLetters, refetchInterval: 10_000 });
  const topUps = useQuery({ queryKey: opsKeys.topUpDeadLetters, queryFn: opsApi.topUpDeadLetters, refetchInterval: 10_000 });

  const tabs: { id: Queue; label: string; count: number | undefined }[] = [
    { id: "bills", label: "Bill payments", count: bills.data?.length },
    { id: "topups", label: "Mobile top-ups", count: topUps.data?.length },
  ];

  return (
    <div>
      <PageHeader
        title="Dead letters"
        description="Payments the system stopped retrying, with the money still reserved. Check the provider's settlement file, then replay, refund or confirm."
      />
      <div className="mb-4 flex gap-2" role="tablist">
        {tabs.map((tab) => (
          <button
            key={tab.id}
            role="tab"
            aria-selected={tab.id === queue}
            onClick={() => setQueue(tab.id)}
            className={cn("rounded-full px-3 py-1.5 text-sm font-medium", tab.id === queue ? "bg-brand-600 text-white" : "bg-white text-ink-700 ring-1 ring-ink-200 hover:bg-ink-50")}
          >
            {tab.label}
            {tab.count !== undefined && <span className="tabular ml-1.5 opacity-80">{tab.count}</span>}
          </button>
        ))}
      </div>

      {queue === "bills" ? (
        <>
          {bills.error && <Alert className="mb-4">{bills.error.message}</Alert>}
          <Table head={["", "Biller", "Consumer", "Amount", "Stopped at", "Waiting", "Payment"]} empty={bills.isSuccess && bills.data.length === 0 && "Nothing waiting. Every bill payment finished."}>
            {bills.data?.map((item) => <BillRow key={item.id} item={item} />)}
          </Table>
        </>
      ) : (
        <>
          {topUps.error && <Alert className="mb-4">{topUps.error.message}</Alert>}
          <Table head={["", "Network", "Number", "Amount", "Stopped at", "Waiting", "Top-up"]} empty={topUps.isSuccess && topUps.data.length === 0 && "Nothing waiting. Every top-up finished."}>
            {topUps.data?.map((item) => <TopUpRow key={item.id} item={item} />)}
          </Table>
        </>
      )}
    </div>
  );
}

function BillRow({ item }: { item: BillDeadLetter }) {
  const [open, setOpen] = useState(false);
  return (
    <>
      <tr className="cursor-pointer hover:bg-ink-50" onClick={() => setOpen(!open)}>
        <td className="w-8 px-3 py-2.5 text-ink-400">{open ? <ChevronDown className="h-4 w-4" /> : <ChevronRight className="h-4 w-4" />}</td>
        <td className="px-4 py-2.5 font-medium">{item.biller}</td>
        <td className="px-4 py-2.5 font-mono text-xs">{item.consumerNumber}</td>
        <td className="tabular px-4 py-2.5">{formatPaisa(Number(item.amountPaisa))}</td>
        <td className="px-4 py-2.5">
          <StateBadge state={item.step} />
        </td>
        <td className="tabular px-4 py-2.5 text-ink-600">{item.deadLetteredAt ? since(item.deadLetteredAt) : "—"}</td>
        <td className="px-4 py-2.5">
          <ShortId id={item.id} />
        </td>
      </tr>
      {open && (
        <tr>
          <td colSpan={7} className="bg-ink-50/60 px-6 py-4">
            <Resolve
              reason={item.reason}
              userId={item.userId}
              sagaLink={`/ops/sagas?service=bills&userId=${item.userId}`}
              referenceLabel="Biller reference"
              canRefund
              canConfirm
              actions={{
                replay: () => opsApi.replayBill(item.id),
                refund: (reason) => opsApi.refundBill(item.id, reason),
                confirm: (reference) => opsApi.confirmBill(item.id, reference),
              }}
              queryKey={opsKeys.billDeadLetters}
            />
            <div className="mt-4 max-w-xl rounded-xl bg-white p-4">
              <Timeline steps={item.timeline.map((step) => ({ ...step, sequence: Number(step.sequence) }))} status="DeadLettered" />
            </div>
          </td>
        </tr>
      )}
    </>
  );
}

function TopUpRow({ item }: { item: TopUpDeadLetter }) {
  const [open, setOpen] = useState(false);
  return (
    <>
      <tr className="cursor-pointer hover:bg-ink-50" onClick={() => setOpen(!open)}>
        <td className="w-8 px-3 py-2.5 text-ink-400">{open ? <ChevronDown className="h-4 w-4" /> : <ChevronRight className="h-4 w-4" />}</td>
        <td className="px-4 py-2.5 font-medium">{item.network}</td>
        <td className="px-4 py-2.5">{formatLocal(item.msisdn)}</td>
        <td className="tabular px-4 py-2.5">
          {formatPaisa(Number(item.amountPaisa))}
          {item.bundleCode && <span className="ml-1 text-xs text-ink-500">({item.bundleCode})</span>}
        </td>
        <td className="px-4 py-2.5">
          <StateBadge state={item.deadLetteredFrom ?? "DeadLettered"} />
        </td>
        <td className="tabular px-4 py-2.5 text-ink-600">{item.deadLetteredAt ? since(item.deadLetteredAt) : "—"}</td>
        <td className="px-4 py-2.5">
          <ShortId id={item.id} />
        </td>
      </tr>
      {open && (
        <tr>
          <td colSpan={7} className="bg-ink-50/60 px-6 py-4">
            <Resolve
              reason={item.reason}
              userId={item.userId}
              sagaLink={`/ops/sagas?service=topups&userId=${item.userId}`}
              referenceLabel="Network reference"
              canRefund={item.canRefund}
              canConfirm={item.canConfirm}
              actions={{
                replay: () => opsApi.replayTopUp(item.id),
                refund: (reason) => opsApi.refundTopUp(item.id, reason),
                confirm: (reference) => opsApi.confirmTopUp(item.id, reference),
              }}
              queryKey={opsKeys.topUpDeadLetters}
            />
            <div className="mt-4 max-w-xl rounded-xl bg-white p-4">
              <Timeline steps={item.timeline.map((step) => ({ ...step, sequence: Number(step.sequence) }))} status="DeadLettered" />
            </div>
          </td>
        </tr>
      )}
    </>
  );
}

type Action = "replay" | "refund" | "confirm";

/** Replay, refund (with a reason) or confirm (with the provider's reference). */
function Resolve({
  reason,
  userId,
  sagaLink,
  referenceLabel,
  canRefund,
  canConfirm,
  actions,
  queryKey,
}: {
  reason: string | null;
  userId: string;
  sagaLink: string;
  referenceLabel: string;
  canRefund: boolean;
  canConfirm: boolean;
  actions: { replay: () => Promise<unknown>; refund: (reason: string) => Promise<unknown>; confirm: (reference: string) => Promise<unknown> };
  queryKey: readonly unknown[];
}) {
  const queryClient = useQueryClient();
  const [form, setForm] = useState<Action | null>(null);
  const [text, setText] = useState("");

  const act = useMutation({
    mutationFn: ({ action, value }: { action: Action; value: string }) =>
      action === "replay" ? actions.replay() : action === "refund" ? actions.refund(value) : actions.confirm(value),
    onSuccess: () => {
      setForm(null);
      setText("");
      void queryClient.invalidateQueries({ queryKey });
      void queryClient.invalidateQueries({ queryKey: ["ops", "sagas"] });
    },
  });

  function submit(event: FormEvent) {
    event.preventDefault();
    if (!form || !text.trim()) return;
    act.mutate({ action: form, value: text.trim() });
  }

  return (
    <div className="space-y-3">
      <p className="text-sm text-ink-700">
        <span className="font-medium">Why it stopped:</span> {reason ?? "—"}
      </p>
      <p className="text-xs text-ink-500">
        Customer <span className="font-mono">{userId}</span> ·{" "}
        <Link href={sagaLink} className="font-medium text-brand-700 hover:underline">
          their Sagas
        </Link>
      </p>
      {act.isSuccess && <Alert tone="success">Done. The payment is moving again; it leaves this queue when it finishes.</Alert>}
      {act.error && <Alert>{act.error.message}</Alert>}
      <div className="flex flex-wrap gap-2">
        <Button size="sm" onClick={() => act.mutate({ action: "replay", value: "" })} loading={act.isPending && act.variables?.action === "replay"}>
          Replay
        </Button>
        <Button size="sm" variant="outline" onClick={() => setForm(form === "refund" ? null : "refund")} disabled={!canRefund} title={canRefund ? undefined : "The provider applied it: it cannot be refunded"}>
          Refund…
        </Button>
        <Button size="sm" variant="outline" onClick={() => setForm(form === "confirm" ? null : "confirm")} disabled={!canConfirm} title={canConfirm ? undefined : "The money is already being returned"}>
          Confirm…
        </Button>
      </div>
      {form && (
        <form onSubmit={submit} className="flex max-w-xl gap-2" noValidate>
          <Input
            className="h-10"
            autoFocus
            placeholder={form === "refund" ? "Why the money goes back" : referenceLabel}
            aria-label={form === "refund" ? "Refund reason" : referenceLabel}
            value={text}
            onChange={(event) => setText(event.target.value)}
          />
          <Button type="submit" size="sm" className="h-10" variant={form === "refund" ? "danger" : "primary"} disabled={!text.trim()} loading={act.isPending}>
            {form === "refund" ? "Refund" : "Confirm"}
          </Button>
        </form>
      )}
      <p className="text-xs text-ink-400">Your name goes on the payment&apos;s timeline with the action.</p>
    </div>
  );
}
