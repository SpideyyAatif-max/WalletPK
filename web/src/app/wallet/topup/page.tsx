"use client";

import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { ArrowLeft, ChevronRight, Smartphone } from "lucide-react";
import { useState, type FormEvent } from "react";
import { StatusPill, Timeline } from "@/components/payment-status";
import { Button } from "@/components/ui/button";
import { Alert, Card } from "@/components/ui/card";
import { Field, Input } from "@/components/ui/input";
import { formatDay, formatTime } from "@/lib/dates";
import { formatMoney, formatPaisa, parseRupees } from "@/lib/money";
import { formatLocal, toE164 } from "@/lib/phone";
import { airtimeProblem, isTopUpFinal, networkOf, quickAirtime, topUpKeys, topUpStepNames, topUpsApi, validity, type Bundle } from "@/lib/topups";
import { useSession } from "@/lib/session";
import { cn } from "@/lib/utils";
import { queryKeys } from "@/lib/wallet-api";
import { useBalance } from "@/lib/wallet-queries";

/**
 * Mobile load (M8.1): type a number (the network is worked out from it), pick airtime or a bundle, and
 * watch the top-up: reserved, sent to the network, settled; or retried, checked with the network,
 * returned. A number is never topped up twice.
 */
export default function TopUp() {
  const [openId, setOpenId] = useState<string | null>(null);
  return openId ? <Tracker id={openId} onBack={() => setOpenId(null)} /> : <NewTopUp onSent={setOpenId} />;
}

function NewTopUp({ onSent }: { onSent: (id: string) => void }) {
  const { user } = useSession();
  const balance = useBalance();
  const networks = useQuery({ queryKey: topUpKeys.networks, queryFn: topUpsApi.networks, refetchInterval: 15_000 });
  const recent = useQuery({ queryKey: topUpKeys.list, queryFn: () => topUpsApi.list(5) });

  const [number, setNumber] = useState("");
  const [kind, setKind] = useState<"airtime" | "bundle">("airtime");
  const [amount, setAmount] = useState("");
  const [bundle, setBundle] = useState<Bundle | null>(null);
  const [error, setError] = useState<string | null>(null);

  const networkCode = networkOf(number);
  const network = networks.data?.find((item) => item.code === networkCode) ?? null;
  const bundles = useQuery({
    queryKey: topUpKeys.bundles(networkCode ?? ""),
    queryFn: () => topUpsApi.bundles(networkCode!),
    enabled: kind === "bundle" && networkCode !== null,
  });

  const send = useMutation({
    mutationFn: (body: Parameters<typeof topUpsApi.topUp>[0]) => topUpsApi.topUp(body),
    onSuccess: (topUp) => onSent(topUp.id),
  });

  const ownNumber = user?.phoneNumber ?? null;

  function submit(event: FormEvent) {
    event.preventDefault();
    const msisdn = toE164(number);
    if (!msisdn) return setError("Enter a mobile number, for example 0300 1234567.");
    if (!networkCode) return setError("This number is not on Jazz, Zong, Ufone or Telenor.");
    if (kind === "bundle") {
      if (!bundle) return setError("Pick a bundle.");
      setError(null);
      return send.mutate({ msisdn, amountPaisa: bundle.pricePaisa, bundleCode: bundle.code, bundleName: bundle.name });
    }
    const paisa = parseRupees(amount);
    const problem = airtimeProblem(paisa);
    if (problem) return setError(problem);
    setError(null);
    send.mutate({ msisdn, amountPaisa: paisa! });
  }

  const total = kind === "bundle" ? bundle?.pricePaisa ?? null : parseRupees(amount);

  return (
    <div className="space-y-5">
      <Card className="space-y-5">
        <div>
          <h1 className="text-xl font-bold">Mobile load</h1>
          <p className="mt-1 text-sm text-ink-600">
            Airtime or bundles on any network, free. Balance: <span className="tabular font-medium">{balance.data ? formatMoney(balance.data.balance) : "…"}</span>
          </p>
        </div>

        <form onSubmit={submit} className="space-y-4" noValidate>
          <Field label="Mobile number" htmlFor="msisdn">
            <div className="relative">
              <Input
                id="msisdn"
                inputMode="tel"
                autoComplete="tel"
                placeholder="0300 1234567"
                value={number}
                onChange={(event) => {
                  setNumber(event.target.value);
                  setBundle(null);
                }}
                autoFocus
              />
              {network && <span className="absolute top-1/2 right-3 -translate-y-1/2 rounded-full bg-brand-50 px-2 py-0.5 text-xs font-semibold text-brand-800">{network.name}</span>}
            </div>
          </Field>
          {ownNumber && number === "" && (
            <button type="button" onClick={() => setNumber(formatLocal(ownNumber))} className="text-sm font-medium text-brand-700 hover:underline">
              My number ({formatLocal(ownNumber)})
            </button>
          )}
          {network && network.status !== "Available" && (
            <Alert tone="info">{network.name} is {network.status === "Down" ? "unavailable" : "slow"} right now. We will keep trying for a few minutes; if it cannot be done, the money comes back.</Alert>
          )}

          <div className="grid grid-cols-2 gap-1 rounded-xl bg-ink-100 p-1" role="tablist">
            {(["airtime", "bundle"] as const).map((option) => (
              <button
                key={option}
                type="button"
                role="tab"
                aria-selected={kind === option}
                onClick={() => setKind(option)}
                className={cn("rounded-lg py-2 text-sm font-semibold", kind === option ? "bg-white text-ink-900 shadow-sm" : "text-ink-600")}
              >
                {option === "airtime" ? "Airtime" : "Bundles"}
              </button>
            ))}
          </div>

          {kind === "airtime" ? (
            <div className="space-y-3">
              <Field label="Amount (Rs. 20 to Rs. 5,000)" htmlFor="amount">
                <Input id="amount" inputMode="numeric" placeholder="Rs. 100" value={amount} onChange={(event) => setAmount(event.target.value)} />
              </Field>
              <div className="flex gap-2">
                {quickAirtime.map((paisa) => (
                  <button key={paisa} type="button" onClick={() => setAmount(String(paisa / 100))} className="flex-1 rounded-lg border border-ink-200 py-1.5 text-sm font-medium hover:bg-ink-50">
                    {paisa / 100}
                  </button>
                ))}
              </div>
            </div>
          ) : !networkCode ? (
            <p className="text-sm text-ink-500">Enter the number first to see its network&apos;s bundles.</p>
          ) : (
            <div className="space-y-2">
              {bundles.error && <Alert>{bundles.error.message}</Alert>}
              {bundles.isLoading && <p className="text-sm text-ink-500">Loading bundles…</p>}
              {bundles.data?.map((item) => (
                <button
                  key={item.code}
                  type="button"
                  onClick={() => setBundle(item)}
                  aria-pressed={bundle?.code === item.code}
                  className={cn("flex w-full items-center justify-between rounded-xl border p-3 text-left", bundle?.code === item.code ? "border-brand-500 bg-brand-50" : "border-ink-100 hover:bg-ink-50")}
                >
                  <span>
                    <span className="block text-sm font-semibold">{item.name}</span>
                    <span className="block text-xs text-ink-500">Valid {validity(item.validityDays)}</span>
                  </span>
                  <span className="tabular text-sm font-semibold">{formatPaisa(item.pricePaisa)}</span>
                </button>
              ))}
            </div>
          )}

          {(error ?? send.error?.message) && <Alert>{error ?? send.error?.message}</Alert>}
          <Button type="submit" size="lg" className="w-full" loading={send.isPending}>
            {total ? `Load ${formatPaisa(total)}` : "Load"}
          </Button>
        </form>
      </Card>

      {recent.data && recent.data.length > 0 && (
        <Card className="p-2">
          <h2 className="px-3 pt-2 pb-1 text-sm font-semibold text-ink-700">Recent loads</h2>
          <ul>
            {recent.data.map((topUp) => (
              <li key={topUp.id}>
                <button onClick={() => onSent(topUp.id)} className="flex w-full items-center gap-3 rounded-xl px-3 py-2.5 text-left hover:bg-ink-50">
                  <span className="grid h-9 w-9 shrink-0 place-items-center rounded-full bg-brand-50 text-brand-700">
                    <Smartphone className="h-5 w-5" aria-hidden />
                  </span>
                  <div className="min-w-0 flex-1">
                    <p className="truncate text-sm font-medium">{formatLocal(topUp.msisdn)}</p>
                    <p className="text-xs text-ink-500">
                      {topUp.networkName} {topUp.bundleName ?? "airtime"} · {formatDay(topUp.requestedAt)}
                    </p>
                  </div>
                  <div className="text-right">
                    <p className="tabular text-sm font-semibold">{formatPaisa(topUp.amountPaisa)}</p>
                    <StatusPill status={topUp.status} />
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

/** One top-up, refreshed every second until it is final. */
function Tracker({ id, onBack }: { id: string; onBack: () => void }) {
  const queryClient = useQueryClient();
  const topUp = useQuery({
    queryKey: topUpKeys.topUp(id),
    queryFn: async () => {
      const current = await topUpsApi.topUpById(id);
      if (isTopUpFinal(current.status)) {
        void queryClient.invalidateQueries({ queryKey: queryKeys.wallet });
        void queryClient.invalidateQueries({ queryKey: topUpKeys.list });
      }
      return current;
    },
    refetchInterval: (query) => (query.state.data && isTopUpFinal(query.state.data.status) ? false : 1_000),
  });

  const data = topUp.data;
  return (
    <div className="space-y-4">
      <button onClick={onBack} className="inline-flex items-center gap-1 text-sm font-medium text-brand-700 hover:underline">
        <ArrowLeft className="h-4 w-4" aria-hidden /> Mobile load
      </button>
      {topUp.error && <Alert>{topUp.error.message}</Alert>}
      {data && (
        <Card className="space-y-5">
          <div className="flex items-start justify-between gap-3">
            <div className="min-w-0">
              <p className="text-sm text-ink-500">
                {data.networkName} · {formatLocal(data.msisdn)}
              </p>
              <p className="tabular mt-1 text-3xl font-bold">{formatPaisa(data.amountPaisa)}</p>
              <p className="text-sm text-ink-600">{data.bundleName ? `${data.bundleName} bundle` : "Airtime"}</p>
            </div>
            <StatusPill status={data.status} />
          </div>
          {data.status === "UnderReview" && <Alert tone="info">{data.networkName} has not confirmed this top-up. Our team is checking it; your money stays reserved until it is resolved.</Alert>}
          {data.status === "Failed" && <Alert>{data.failureReason}. The reserved money is back in your wallet.</Alert>}
          {data.timeline && <Timeline steps={data.timeline} status={data.status} names={topUpStepNames} />}
          <dl className="space-y-2 rounded-xl bg-ink-50 p-4 text-sm">
            {data.telcoReference && <Row label="Network reference" value={<span className="font-mono text-xs">{data.telcoReference}</span>} />}
            <Row label="Requested" value={`${formatDay(data.requestedAt)}, ${formatTime(data.requestedAt)}`} />
            <Row label="Top-up ID" value={<span className="font-mono text-xs">{data.id}</span>} />
          </dl>
        </Card>
      )}
    </div>
  );
}

function Row({ label, value }: { label: string; value: React.ReactNode }) {
  return (
    <div className="flex items-center justify-between gap-4">
      <dt className="text-ink-600">{label}</dt>
      <dd className="tabular text-right text-ink-900">{value}</dd>
    </div>
  );
}
