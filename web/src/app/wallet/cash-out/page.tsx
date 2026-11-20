"use client";

import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { Banknote, CheckCircle2, ChevronRight, Loader2, MapPin, XCircle } from "lucide-react";
import { useEffect, useState, type FormEvent } from "react";
import { StatusPill } from "@/components/payment-status";
import { Button } from "@/components/ui/button";
import { Alert, Card } from "@/components/ui/card";
import { Field, Input } from "@/components/ui/input";
import { agentKeys, agentsApi, cashProblem, isCashOutLive, kindNames, normaliseAgentCode, quoteCashOut, spacedCode, type CashOutRequest } from "@/lib/agents";
import { ApiError } from "@/lib/api";
import { formatDay } from "@/lib/dates";
import { countdown, secondsLeft } from "@/lib/merchants";
import { formatMoney, formatPaisa, parseRupees } from "@/lib/money";
import { queryKeys } from "@/lib/wallet-api";
import { useBalance } from "@/lib/wallet-queries";

/**
 * Cash-out at an agent (M9.3): pick the agent (its code is on the shop's sign), say how much, and show the
 * six-digit code at the counter. The agent types it and hands over the cash; nothing leaves the wallet
 * until then.
 */
export default function CashOut() {
  const queryClient = useQueryClient();
  const current = useQuery({
    queryKey: agentKeys.cashOut,
    queryFn: agentsApi.currentCashOut,
    retry: (count, error) => !(error instanceof ApiError && error.status === 404) && count < 2,
    refetchInterval: (query) => (query.state.data && isCashOutLive(query.state.data.status) ? 2_000 : false),
  });
  // The code exists only in the answer that created the request; it is kept here while the page is open.
  const [created, setCreated] = useState<{ id: string; code: string } | null>(null);

  const request = current.data;
  const showRequest = request && (isCashOutLive(request.status) || created?.id === request.id);

  return (
    <div className="space-y-5">
      {showRequest ? (
        <RequestCard
          request={request}
          code={created?.id === request.id ? created.code : null}
          onDone={() => {
            setCreated(null);
            void queryClient.invalidateQueries({ queryKey: queryKeys.wallet });
          }}
        />
      ) : (
        <NewRequest
          onCreated={(request) => {
            setCreated({ id: request.id, code: request.code ?? "" });
            queryClient.setQueryData(agentKeys.cashOut, request);
          }}
        />
      )}
      <Recent />
    </div>
  );
}

function NewRequest({ onCreated }: { onCreated: (request: CashOutRequest) => void }) {
  const balance = useBalance();
  const [agentCode, setAgentCode] = useState("");
  const [amount, setAmount] = useState("");
  const [city, setCity] = useState("");
  const [error, setError] = useState<string | null>(null);
  const code = normaliseAgentCode(agentCode);
  const agent = useQuery({ queryKey: ["agents", "by-code", code], queryFn: () => agentsApi.byCode(code!), enabled: code !== null, retry: false });
  const found = useQuery({ queryKey: agentKeys.find(city.trim().toLowerCase()), queryFn: () => agentsApi.find(city.trim()), enabled: city.trim().length >= 3 });

  const create = useMutation({
    mutationFn: (amountPaisa: number) => agentsApi.requestCashOut(code!, amountPaisa),
    onSuccess: onCreated,
  });

  const paisa = parseRupees(amount);
  const quote = paisa !== null && cashProblem(paisa) === null ? quoteCashOut(paisa) : null;

  function submit(event: FormEvent) {
    event.preventDefault();
    if (!code) return setError("Enter the agent's code, for example WPKA123456. It is on the shop's sign.");
    const problem = cashProblem(paisa);
    if (problem) return setError(problem);
    setError(null);
    create.mutate(paisa!);
  }

  return (
    <Card className="space-y-5">
      <div>
        <h1 className="text-xl font-bold">Cash out at an agent</h1>
        <p className="mt-1 text-sm text-ink-600">
          Get cash at any WalletPK agent. Balance: <span className="tabular font-medium">{balance.data ? formatMoney(balance.data.balance) : "…"}</span>
        </p>
      </div>
      <form onSubmit={submit} className="space-y-4" noValidate>
        <Field label="Agent code" htmlFor="agent">
          <Input id="agent" value={agentCode} onChange={(event) => setAgentCode(event.target.value)} placeholder="WPKA123456" autoCapitalize="characters" autoFocus />
        </Field>
        {agent.data && (
          <p className="flex items-center gap-1.5 text-sm text-ink-700">
            <MapPin className="h-4 w-4 text-brand-600" aria-hidden /> {agent.data.shopName}, {agent.data.area ? `${agent.data.area}, ` : ""}
            {agent.data.city}
          </p>
        )}
        {agent.error && <p className="text-sm text-red-600">{agent.error.message}</p>}
        <Field label="Cash (Rs. 100 to Rs. 50,000)" htmlFor="amount">
          <Input id="amount" inputMode="numeric" placeholder="Rs. 1,000" value={amount} onChange={(event) => setAmount(event.target.value)} />
        </Field>
        {quote && (
          <div className="rounded-xl bg-ink-50 p-3 text-sm text-ink-700">
            Fee {formatPaisa(quote.feePaisa)} + tax {formatPaisa(quote.taxPaisa)}. {formatPaisa(quote.totalPaisa)} leaves your wallet when the agent pays you.
          </div>
        )}
        {(error ?? create.error?.message) && <Alert>{error ?? create.error?.message}</Alert>}
        <Button type="submit" size="lg" className="w-full" loading={create.isPending}>
          Get a cash-out code
        </Button>
      </form>

      <div className="space-y-2 border-t border-ink-100 pt-4">
        <Field label="Find an agent in your city" htmlFor="city">
          <Input id="city" value={city} onChange={(event) => setCity(event.target.value)} placeholder="Multan" />
        </Field>
        {found.data?.length === 0 && <p className="text-sm text-ink-500">No agents found in {city}.</p>}
        <ul className="space-y-1">
          {found.data?.map((item) => (
            <li key={item.code}>
              <button type="button" onClick={() => setAgentCode(item.code)} className="flex w-full items-center justify-between rounded-xl px-3 py-2 text-left hover:bg-ink-50">
                <span>
                  <span className="block text-sm font-medium">{item.shopName}</span>
                  <span className="block text-xs text-ink-500">
                    {item.area ? `${item.area}, ` : ""}
                    {item.city} · {item.code}
                  </span>
                </span>
                <ChevronRight className="h-4 w-4 text-ink-400" aria-hidden />
              </button>
            </li>
          ))}
        </ul>
      </div>
    </Card>
  );
}

/** The live request: the code to show (only right after creating it), a countdown, and the outcome. */
function RequestCard({ request, code, onDone }: { request: CashOutRequest; code: string | null; onDone: () => void }) {
  const queryClient = useQueryClient();
  const [left, setLeft] = useState(() => secondsLeft(request.expiresAt));
  useEffect(() => {
    const timer = setInterval(() => setLeft(secondsLeft(request.expiresAt)), 1_000);
    return () => clearInterval(timer);
  }, [request.expiresAt]);

  const cancel = useMutation({
    mutationFn: () => agentsApi.cancelCashOut(request.id),
    onSuccess: (cancelled) => queryClient.setQueryData(agentKeys.cashOut, cancelled),
  });

  if (request.status === "Completed") {
    return (
      <Card className="space-y-4 text-center">
        <CheckCircle2 className="mx-auto h-14 w-14 text-brand-600" aria-hidden />
        <div>
          <p className="text-sm text-ink-500">Collect your cash</p>
          <p className="tabular text-3xl font-bold">{formatPaisa(request.amountPaisa)}</p>
          <p className="text-sm text-ink-600">from {request.agentName}</p>
        </div>
        <Button size="lg" className="w-full" onClick={onDone}>
          Done
        </Button>
      </Card>
    );
  }

  if (!isCashOutLive(request.status)) {
    return (
      <Card className="space-y-4 text-center">
        <XCircle className="mx-auto h-14 w-14 text-ink-400" aria-hidden />
        <p className="text-sm text-ink-700">
          {request.status === "Locked" ? "Too many wrong codes were tried. Create a new request." : request.status === "Expired" ? "This code has expired." : "This request was cancelled."}
        </p>
        <Button size="lg" className="w-full" onClick={onDone}>
          New cash-out
        </Button>
      </Card>
    );
  }

  return (
    <Card className="space-y-5 text-center">
      <div>
        <p className="text-sm text-ink-500">Show this code at {request.agentName}</p>
        {code ? (
          <p className="tabular mt-2 text-5xl font-bold tracking-widest">{spacedCode(code)}</p>
        ) : (
          <p className="mt-2 text-sm text-ink-600">The code was shown when you made this request. If you no longer have it, cancel and make a new one.</p>
        )}
      </div>
      <dl className="space-y-2 rounded-xl bg-ink-50 p-4 text-left text-sm">
        <Row label="Cash" value={formatPaisa(request.amountPaisa)} />
        <Row label="Fee and tax" value={formatPaisa(request.feePaisa + request.taxPaisa)} />
        <Row label="From your wallet" value={formatPaisa(request.totalPaisa)} />
        <Row label="Agent" value={request.agentCode} />
      </dl>
      {request.status === "Processing" ? (
        <p className="flex items-center justify-center gap-2 text-sm text-ink-600">
          <Loader2 className="h-4 w-4 animate-spin" aria-hidden /> The agent is paying you out…
        </p>
      ) : (
        <p className="text-sm text-ink-600">{left > 0 ? `Expires in ${countdown(left)}` : "Expired"}</p>
      )}
      {request.failureReason && <Alert>{request.failureReason} The agent can try again once you have enough.</Alert>}
      {cancel.error && <Alert>{cancel.error.message}</Alert>}
      {request.status === "Open" && (
        <Button variant="ghost" className="w-full" onClick={() => cancel.mutate()} loading={cancel.isPending}>
          Cancel
        </Button>
      )}
    </Card>
  );
}

function Recent() {
  const mine = useQuery({ queryKey: agentKeys.mine, queryFn: () => agentsApi.myTransactions(5) });
  if (!mine.data || mine.data.length === 0) return null;
  return (
    <Card className="p-2">
      <h2 className="px-3 pt-2 pb-1 text-sm font-semibold text-ink-700">At agents</h2>
      <ul>
        {mine.data.map((transaction) => (
          <li key={transaction.id} className="flex items-center gap-3 rounded-xl px-3 py-2.5">
            <span className="grid h-9 w-9 shrink-0 place-items-center rounded-full bg-brand-50 text-brand-700">
              <Banknote className="h-5 w-5" aria-hidden />
            </span>
            <div className="min-w-0 flex-1">
              <p className="truncate text-sm font-medium">
                {kindNames[transaction.kind]} · {transaction.agentName}
              </p>
              <p className="text-xs text-ink-500">{formatDay(transaction.requestedAt)}</p>
            </div>
            <div className="text-right">
              <p className="tabular text-sm font-semibold">{formatPaisa(transaction.amountPaisa)}</p>
              <StatusPill status={transaction.status} />
            </div>
          </li>
        ))}
      </ul>
    </Card>
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
