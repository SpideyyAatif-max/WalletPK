"use client";

import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { CheckCircle2, Copy, Loader2, QrCode, Store } from "lucide-react";
import QRCode from "qrcode";
import { useEffect, useState, type FormEvent } from "react";
import { StatusPill } from "@/components/payment-status";
import { Button } from "@/components/ui/button";
import { Alert, Card } from "@/components/ui/card";
import { Field, Input } from "@/components/ui/input";
import { ApiError } from "@/lib/api";
import { formatDay, formatTime } from "@/lib/dates";
import { countdown, describePlan, merchantKeys, merchantsApi, qrLimits, secondsLeft, splitFee, type Merchant, type Qr } from "@/lib/merchants";
import { formatPaisa, parseRupees } from "@/lib/money";
import { cn } from "@/lib/utils";

/**
 * My shop (M8.2-M8.5): register a business, take payments with a QR code for each sale (the till shows
 * when it is paid), see the day's sales and the nightly settlements to the bank.
 */
export default function Shop() {
  const me = useQuery({
    queryKey: merchantKeys.me,
    queryFn: merchantsApi.me,
    retry: (count, error) => !(error instanceof ApiError && error.status === 404) && count < 2,
    refetchInterval: (query) => (query.state.data?.status === "Pending" ? 1_000 : 30_000),
  });

  if (me.isLoading) return <p className="text-sm text-ink-500">Loading…</p>;
  if (me.error instanceof ApiError && me.error.status === 404) return <Register />;
  if (me.error) return <Alert>{me.error.message}</Alert>;
  if (!me.data) return null;
  if (me.data.status === "Pending") {
    return (
      <Card className="flex items-center gap-3">
        <Loader2 className="h-5 w-5 animate-spin text-sky-600" aria-hidden />
        <p className="text-sm">Opening the merchant wallet for {me.data.name}…</p>
      </Card>
    );
  }
  return <Till merchant={me.data} />;
}

function Register() {
  const queryClient = useQueryClient();
  const plans = useQuery({ queryKey: merchantKeys.feePlans, queryFn: merchantsApi.feePlans });
  const [name, setName] = useState("");
  const [category, setCategory] = useState("RETAIL");
  const [city, setCity] = useState("");

  const register = useMutation({
    mutationFn: () => merchantsApi.register({ name: name.trim(), category, city: city.trim() || undefined }),
    onSuccess: (merchant) => queryClient.setQueryData(merchantKeys.me, merchant),
  });

  const plan = plans.data?.plans.find((item) => item.code === plans.data?.categories.find((entry) => entry.code === category)?.feePlan);
  const example = plan ? splitFee(plan, 100_000, plans.data?.taxBasisPoints) : null;

  function submit(event: FormEvent) {
    event.preventDefault();
    register.mutate();
  }

  return (
    <Card className="space-y-5">
      <div>
        <h1 className="text-xl font-bold">Take payments with WalletPK</h1>
        <p className="mt-1 text-sm text-ink-600">Register your shop to take QR payments. Your sales go to your bank every night.</p>
      </div>
      <form onSubmit={submit} className="space-y-4" noValidate>
        <Field label="Business name" htmlFor="name">
          <Input id="name" value={name} onChange={(event) => setName(event.target.value)} maxLength={60} placeholder="Ali Tea Stall" autoFocus />
        </Field>
        <div>
          <p className="mb-2 text-sm font-medium text-ink-800">Kind of business</p>
          <div className="grid grid-cols-2 gap-2">
            {plans.data?.categories.map((item) => (
              <button
                key={item.code}
                type="button"
                onClick={() => setCategory(item.code)}
                aria-pressed={category === item.code}
                className={cn("rounded-xl border p-3 text-left text-sm font-medium", category === item.code ? "border-brand-500 bg-brand-50" : "border-ink-100 hover:bg-ink-50")}
              >
                {item.name}
              </button>
            ))}
          </div>
        </div>
        <Field label="City (optional)" htmlFor="city">
          <Input id="city" value={city} onChange={(event) => setCity(event.target.value)} maxLength={40} placeholder="Lahore" />
        </Field>
        {plan && example && (
          <div className="rounded-xl bg-ink-50 p-4 text-sm text-ink-700">
            <p>
              Fee plan <span className="font-semibold">{plan.name}</span>: {describePlan(plan)} per payment, plus 16% tax on the fee.
            </p>
            <p className="mt-1 text-ink-500">
              On a {formatPaisa(example.amountPaisa)} sale you receive {formatPaisa(example.netPaisa)}.
            </p>
          </div>
        )}
        {register.error && <Alert>{register.error.message}</Alert>}
        <Button type="submit" size="lg" className="w-full" loading={register.isPending}>
          Register my shop
        </Button>
      </form>
    </Card>
  );
}

function Till({ merchant }: { merchant: Merchant }) {
  const queryClient = useQueryClient();
  const [amount, setAmount] = useState("");
  const [reference, setReference] = useState("");
  const [error, setError] = useState<string | null>(null);
  const [qr, setQr] = useState<Qr | null>(null);

  const create = useMutation({
    mutationFn: (amountPaisa: number) => merchantsApi.createQr({ amountPaisa, reference: reference.trim() || undefined }),
    onSuccess: (created) => setQr(created),
  });

  function submit(event: FormEvent) {
    event.preventDefault();
    const paisa = parseRupees(amount);
    if (paisa === null || paisa < qrLimits.minPaisa || paisa > qrLimits.maxPaisa) return setError("Enter Rs. 10 to Rs. 500,000.");
    setError(null);
    create.mutate(paisa);
  }

  function done() {
    setQr(null);
    setAmount("");
    setReference("");
    create.reset();
    void queryClient.invalidateQueries({ queryKey: merchantKeys.me });
    void queryClient.invalidateQueries({ queryKey: merchantKeys.sales });
  }

  return (
    <div className="space-y-5">
      <Card className="space-y-4">
        <div className="flex items-start gap-3">
          <span className="grid h-10 w-10 shrink-0 place-items-center rounded-full bg-brand-50 text-brand-700">
            <Store className="h-5 w-5" aria-hidden />
          </span>
          <div className="min-w-0 flex-1">
            <h1 className="truncate text-xl font-bold">{merchant.name}</h1>
            <p className="text-sm text-ink-500">
              {merchant.code} · {merchant.feePlan.name}: {describePlan(merchant.feePlan)}
            </p>
          </div>
        </div>
        <div className="grid grid-cols-2 gap-3 rounded-xl bg-ink-50 p-4 text-sm">
          <div>
            <p className="text-ink-500">To settle tonight</p>
            <p className="tabular text-lg font-semibold">{formatPaisa(merchant.unsettledNetPaisa)}</p>
          </div>
          <div>
            <p className="text-ink-500">Sales</p>
            <p className="tabular text-lg font-semibold">
              {merchant.unsettledPayments} · {formatPaisa(merchant.unsettledGrossPaisa)}
            </p>
          </div>
        </div>
      </Card>

      <Card className="space-y-4">
        {qr ? (
          <ShowQr initial={qr} onDone={done} />
        ) : (
          <form onSubmit={submit} className="space-y-4" noValidate>
            <h2 className="font-semibold">New sale</h2>
            <Field label="Amount" htmlFor="amount">
              <Input id="amount" inputMode="decimal" placeholder="Rs. 1,000" value={amount} onChange={(event) => setAmount(event.target.value)} />
            </Field>
            <Field label="Reference (optional)" htmlFor="reference">
              <Input id="reference" value={reference} onChange={(event) => setReference(event.target.value)} maxLength={40} placeholder="INV-42" />
            </Field>
            {(error ?? create.error?.message) && <Alert>{error ?? create.error?.message}</Alert>}
            <Button type="submit" size="lg" className="w-full" loading={create.isPending}>
              <QrCode className="h-5 w-5" aria-hidden /> Show QR code
            </Button>
          </form>
        )}
      </Card>

      <Sales />
      <Settlements />
    </div>
  );
}

/** The QR code on the till, polled every second until it is paid or expires. */
function ShowQr({ initial, onDone }: { initial: Qr; onDone: () => void }) {
  const [image, setImage] = useState<string | null>(null);
  const [copied, setCopied] = useState(false);
  const [left, setLeft] = useState(() => secondsLeft(initial.expiresAt));
  const status = useQuery({
    queryKey: merchantKeys.qr(initial.id),
    queryFn: () => merchantsApi.qr(initial.id),
    initialData: initial,
    refetchInterval: (query) => (["Paid", "Expired"].includes(query.state.data?.status ?? "") ? false : 1_000),
  });

  useEffect(() => {
    void QRCode.toDataURL(initial.payload, { errorCorrectionLevel: "M", margin: 1, width: 320 }).then(setImage);
  }, [initial.payload]);

  useEffect(() => {
    const timer = setInterval(() => setLeft(secondsLeft(initial.expiresAt)), 1_000);
    return () => clearInterval(timer);
  }, [initial.expiresAt]);

  const current = status.data;
  const expired = current.status === "Expired" || (left === 0 && current.status === "Open");

  async function copy() {
    await navigator.clipboard.writeText(initial.payload);
    setCopied(true);
  }

  if (current.status === "Paid") {
    return (
      <div className="space-y-4 text-center">
        <CheckCircle2 className="mx-auto h-14 w-14 text-brand-600" aria-hidden />
        <div>
          <p className="text-sm text-ink-500">Paid</p>
          <p className="tabular text-3xl font-bold">{formatPaisa(current.amountPaisa)}</p>
          {current.netPaisa !== null && <p className="text-sm text-ink-600">You receive {formatPaisa(current.netPaisa)} after the fee</p>}
        </div>
        <Button size="lg" className="w-full" onClick={onDone}>
          Next sale
        </Button>
      </div>
    );
  }

  return (
    <div className="space-y-4 text-center">
      <div>
        <p className="tabular text-3xl font-bold">{formatPaisa(current.amountPaisa)}</p>
        {current.reference && <p className="text-sm text-ink-600">{current.reference}</p>}
      </div>
      <div className={cn("mx-auto w-64 rounded-2xl border border-ink-100 p-2", expired && "opacity-30")}>
        {/* A data URL made here: next/image has nothing to optimise. */}
        {/* eslint-disable-next-line @next/next/no-img-element */}
        {image ? <img src={image} alt="QR code to pay" className="h-auto w-full" /> : <div className="aspect-square animate-pulse rounded-xl bg-ink-100" />}
      </div>
      <p className="flex items-center justify-center gap-2 text-sm text-ink-600">
        {expired ? (
          "Expired"
        ) : current.status === "Paying" ? (
          <>
            <Loader2 className="h-4 w-4 animate-spin" aria-hidden /> Customer is paying…
          </>
        ) : (
          <>
            <StatusPill status="Pending" /> Waiting for payment · {countdown(left)}
          </>
        )}
      </p>
      <div className="flex gap-2">
        <Button variant="secondary" className="flex-1" onClick={() => void copy()}>
          <Copy className="h-4 w-4" aria-hidden /> {copied ? "Copied" : "Copy code"}
        </Button>
        <Button variant="ghost" className="flex-1" onClick={onDone}>
          {expired ? "New sale" : "Cancel"}
        </Button>
      </div>
    </div>
  );
}

function Sales() {
  const sales = useQuery({ queryKey: merchantKeys.sales, queryFn: () => merchantsApi.sales(20), refetchInterval: 10_000 });
  if (!sales.data || sales.data.length === 0) return null;
  return (
    <Card className="p-2">
      <h2 className="px-3 pt-2 pb-1 text-sm font-semibold text-ink-700">Sales</h2>
      <ul>
        {sales.data.map((sale) => (
          <li key={sale.id} className="flex items-center gap-3 rounded-xl px-3 py-2.5">
            <div className="min-w-0 flex-1">
              <p className="truncate text-sm font-medium">{sale.reference ?? sale.payerWalletNumber ?? "QR payment"}</p>
              <p className="text-xs text-ink-500">
                {sale.payerWalletNumber && sale.reference ? `${sale.payerWalletNumber} · ` : ""}
                {formatDay(sale.requestedAt)}, {formatTime(sale.requestedAt)}
              </p>
            </div>
            <div className="text-right">
              <p className="tabular text-sm font-semibold">{formatPaisa(sale.amountPaisa)}</p>
              {sale.status === "Completed" ? (
                <p className="tabular text-xs text-ink-500">net {formatPaisa(sale.netPaisa)}</p>
              ) : (
                <StatusPill status={sale.status} />
              )}
            </div>
          </li>
        ))}
      </ul>
    </Card>
  );
}

function Settlements() {
  const settlements = useQuery({ queryKey: merchantKeys.settlements, queryFn: () => merchantsApi.settlements(14) });
  if (!settlements.data || settlements.data.length === 0) return null;
  return (
    <Card className="p-2">
      <h2 className="px-3 pt-2 pb-1 text-sm font-semibold text-ink-700">Settlements to your bank</h2>
      <ul>
        {settlements.data.map((settlement) => (
          <li key={settlement.id} className="flex items-center gap-3 rounded-xl px-3 py-2.5">
            <div className="min-w-0 flex-1">
              <p className="text-sm font-medium">{formatDay(`${settlement.businessDate}T12:00:00+05:00`)}</p>
              <p className="text-xs text-ink-500">
                {settlement.paymentCount} sale{settlement.paymentCount === 1 ? "" : "s"} · fees {formatPaisa(settlement.feePaisa + settlement.taxPaisa)}
                {settlement.status === "Failed" && " · will be retried tonight"}
              </p>
            </div>
            <div className="text-right">
              <p className="tabular text-sm font-semibold">{formatPaisa(settlement.netPaisa)}</p>
              <StatusPill status={settlement.status === "Settled" ? "Completed" : settlement.status} />
            </div>
          </li>
        ))}
      </ul>
    </Card>
  );
}
