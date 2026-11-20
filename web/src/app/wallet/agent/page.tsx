"use client";

import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { AlertTriangle, ArrowDownToLine, ArrowUpFromLine, CheckCircle2, Clock, Loader2, Store, XCircle } from "lucide-react";
import { useState, type FormEvent } from "react";
import { StatusPill } from "@/components/payment-status";
import { Button } from "@/components/ui/button";
import { Alert, Card } from "@/components/ui/card";
import { Field, Input } from "@/components/ui/input";
import { agentKeys, agentsApi, cashProblem, kindNames, quoteCashIn, type Agent, type AgentTransaction } from "@/lib/agents";
import { ApiError } from "@/lib/api";
import { formatDay, formatTime } from "@/lib/dates";
import { formatMoney, formatPaisa, parseRupees } from "@/lib/money";
import { toE164 } from "@/lib/phone";
import { cn } from "@/lib/utils";
import { queryKeys } from "@/lib/wallet-api";
import { useBalance } from "@/lib/wallet-queries";

/**
 * Agent mode (M9): apply, then run the counter. Cash-in takes the customer's cash and credits their
 * wallet from the float; cash-out takes the code the customer shows and pays out cash into the float;
 * the float is loaded from, or moved back to, the agent's own wallet. Commissions are paid nightly.
 */
export default function AgentMode() {
  const me = useQuery({
    queryKey: agentKeys.me,
    queryFn: agentsApi.me,
    retry: (count, error) => !(error instanceof ApiError && error.status === 404) && count < 2,
    refetchInterval: (query) => (query.state.data?.status === "Opening" ? 1_000 : query.state.data?.status === "PendingApproval" ? 10_000 : 30_000),
  });

  if (me.isLoading) return <p className="text-sm text-ink-500">Loading…</p>;
  if (me.error instanceof ApiError && me.error.status === 404) return <Apply />;
  if (me.error) return <Alert>{me.error.message}</Alert>;
  if (!me.data) return null;

  const agent = me.data;
  if (agent.status === "Active" || agent.status === "Suspended") return <Counter agent={agent} />;
  return (
    <Card className="flex items-start gap-3">
      {agent.status === "Rejected" ? (
        <XCircle className="h-5 w-5 shrink-0 text-red-600" aria-hidden />
      ) : agent.status === "Opening" ? (
        <Loader2 className="h-5 w-5 shrink-0 animate-spin text-sky-600" aria-hidden />
      ) : (
        <Clock className="h-5 w-5 shrink-0 text-amber-600" aria-hidden />
      )}
      <div className="text-sm">
        <p className="font-semibold">{agent.shopName}</p>
        <p className="text-ink-600">
          {agent.status === "PendingApproval" && "Your application is with our team. We will check your shop and approve it."}
          {agent.status === "Opening" && "Approved. Opening your float…"}
          {agent.status === "Rejected" && `Your application was not approved${agent.statusReason ? `: ${agent.statusReason}` : "."}`}
        </p>
      </div>
    </Card>
  );
}

function Apply() {
  const queryClient = useQueryClient();
  const [shopName, setShopName] = useState("");
  const [city, setCity] = useState("");
  const [area, setArea] = useState("");
  const apply = useMutation({
    mutationFn: () => agentsApi.apply({ shopName: shopName.trim(), city: city.trim(), area: area.trim() || undefined }),
    onSuccess: (agent) => queryClient.setQueryData(agentKeys.me, agent),
  });

  function submit(event: FormEvent) {
    event.preventDefault();
    apply.mutate();
  }

  return (
    <Card className="space-y-5">
      <div>
        <h1 className="text-xl font-bold">Become a WalletPK agent</h1>
        <p className="mt-1 text-sm text-ink-600">
          Do cash-in and cash-out for customers at your shop and earn a commission on each: 0.4% on cash-in, 60% of the cash-out fee. Commissions are paid to your float every night.
        </p>
      </div>
      <form onSubmit={submit} className="space-y-4" noValidate>
        <Field label="Shop name" htmlFor="shop">
          <Input id="shop" value={shopName} onChange={(event) => setShopName(event.target.value)} maxLength={60} placeholder="Bismillah Easy Cash" autoFocus />
        </Field>
        <div className="grid grid-cols-2 gap-3">
          <Field label="City" htmlFor="city">
            <Input id="city" value={city} onChange={(event) => setCity(event.target.value)} maxLength={40} placeholder="Multan" />
          </Field>
          <Field label="Area (optional)" htmlFor="area">
            <Input id="area" value={area} onChange={(event) => setArea(event.target.value)} maxLength={40} placeholder="Hussain Agahi" />
          </Field>
        </div>
        {apply.error && <Alert>{apply.error.message}</Alert>}
        <Button type="submit" size="lg" className="w-full" loading={apply.isPending}>
          Apply
        </Button>
      </form>
    </Card>
  );
}

type Tab = "cash-in" | "cash-out" | "float";

function Counter({ agent }: { agent: Agent }) {
  const queryClient = useQueryClient();
  const float = useQuery({ queryKey: agentKeys.float, queryFn: agentsApi.float, refetchInterval: 10_000 });
  const [tab, setTab] = useState<Tab>("cash-in");
  const [openId, setOpenId] = useState<string | null>(null);
  const floatPaisa = float.data?.balance.minorUnits ?? null;
  const suspended = agent.status === "Suspended";

  function refresh() {
    void queryClient.invalidateQueries({ queryKey: agentKeys.float });
    void queryClient.invalidateQueries({ queryKey: agentKeys.me });
    void queryClient.invalidateQueries({ queryKey: agentKeys.transactions });
    void queryClient.invalidateQueries({ queryKey: queryKeys.wallet });
  }

  return (
    <div className="space-y-5">
      <Card className="space-y-4">
        <div className="flex items-start gap-3">
          <span className="grid h-10 w-10 shrink-0 place-items-center rounded-full bg-brand-50 text-brand-700">
            <Store className="h-5 w-5" aria-hidden />
          </span>
          <div className="min-w-0 flex-1">
            <h1 className="truncate text-xl font-bold">{agent.shopName}</h1>
            <p className="text-sm text-ink-500">
              {agent.code} · {agent.area ? `${agent.area}, ` : ""}
              {agent.city}
            </p>
          </div>
        </div>
        <div className="grid grid-cols-2 gap-3 rounded-xl bg-ink-50 p-4 text-sm">
          <div>
            <p className="text-ink-500">Float</p>
            <p className="tabular text-lg font-semibold">{float.data ? formatMoney(float.data.balance) : "…"}</p>
          </div>
          <div>
            <p className="text-ink-500">Commission due tonight</p>
            <p className="tabular text-lg font-semibold">{formatPaisa(agent.unpaidCommissionPaisa)}</p>
          </div>
        </div>
        {suspended && <Alert>Your agent account is suspended{agent.statusReason ? `: ${agent.statusReason}` : ""}. You can still move your float to your wallet.</Alert>}
        {!suspended && floatPaisa !== null && floatPaisa < agent.lowFloatPaisa && (
          <p className="flex items-center gap-2 rounded-xl bg-amber-50 p-3 text-sm text-amber-800">
            <AlertTriangle className="h-4 w-4 shrink-0" aria-hidden /> Float is below {formatPaisa(agent.lowFloatPaisa)}. Load more to keep doing cash-ins.
          </p>
        )}
      </Card>

      <Card className="space-y-4">
        {openId ? (
          <Result
            id={openId}
            onFinal={refresh}
            onDone={() => {
              setOpenId(null);
              refresh();
            }}
          />
        ) : (
          <>
            <div className="grid grid-cols-3 gap-1 rounded-xl bg-ink-100 p-1" role="tablist">
              {(["cash-in", "cash-out", "float"] as const).map((option) => (
                <button
                  key={option}
                  type="button"
                  role="tab"
                  aria-selected={tab === option}
                  onClick={() => setTab(option)}
                  className={cn("rounded-lg py-2 text-sm font-semibold", tab === option ? "bg-white text-ink-900 shadow-sm" : "text-ink-600")}
                >
                  {option === "cash-in" ? "Cash-in" : option === "cash-out" ? "Cash-out" : "Float"}
                </button>
              ))}
            </div>
            {tab === "cash-in" && <CashInForm disabled={suspended} onStarted={setOpenId} />}
            {tab === "cash-out" && <CashOutForm disabled={suspended} onStarted={setOpenId} />}
            {tab === "float" && <FloatForm suspended={suspended} onStarted={setOpenId} />}
          </>
        )}
      </Card>

      <Recent onOpen={setOpenId} />
      <Payouts />
    </div>
  );
}

function CashInForm({ disabled, onStarted }: { disabled: boolean; onStarted: (id: string) => void }) {
  const [number, setNumber] = useState("");
  const [amount, setAmount] = useState("");
  const [error, setError] = useState<string | null>(null);
  const cashIn = useMutation({ mutationFn: ({ msisdn, paisa }: { msisdn: string; paisa: number }) => agentsApi.cashIn(msisdn, paisa), onSuccess: (transaction) => onStarted(transaction.id) });
  const paisa = parseRupees(amount);

  function submit(event: FormEvent) {
    event.preventDefault();
    const msisdn = toE164(number);
    if (!msisdn) return setError("Enter the customer's mobile number.");
    const problem = cashProblem(paisa);
    if (problem) return setError(problem);
    setError(null);
    cashIn.mutate({ msisdn, paisa: paisa! });
  }

  return (
    <form onSubmit={submit} className="space-y-4" noValidate>
      <p className="text-sm text-ink-600">Take the customer&apos;s cash, then deposit it to their wallet from your float.</p>
      <Field label="Customer's mobile number" htmlFor="cash-in-number">
        <Input id="cash-in-number" inputMode="tel" placeholder="0300 1234567" value={number} onChange={(event) => setNumber(event.target.value)} />
      </Field>
      <Field label="Cash received" htmlFor="cash-in-amount">
        <Input id="cash-in-amount" inputMode="numeric" placeholder="Rs. 1,000" value={amount} onChange={(event) => setAmount(event.target.value)} />
      </Field>
      {paisa !== null && cashProblem(paisa) === null && <p className="text-sm text-ink-600">You earn {formatPaisa(quoteCashIn(paisa).commissionPaisa)}.</p>}
      {(error ?? cashIn.error?.message) && <Alert>{error ?? cashIn.error?.message}</Alert>}
      <Button type="submit" size="lg" className="w-full" loading={cashIn.isPending} disabled={disabled}>
        <ArrowDownToLine className="h-5 w-5" aria-hidden /> Deposit {paisa && cashProblem(paisa) === null ? formatPaisa(paisa) : ""}
      </Button>
    </form>
  );
}

function CashOutForm({ disabled, onStarted }: { disabled: boolean; onStarted: (id: string) => void }) {
  const [number, setNumber] = useState("");
  const [code, setCode] = useState("");
  const [error, setError] = useState<string | null>(null);
  const take = useMutation({ mutationFn: ({ msisdn, digits }: { msisdn: string; digits: string }) => agentsApi.takeCashOut(msisdn, digits), onSuccess: (transaction) => onStarted(transaction.id) });

  function submit(event: FormEvent) {
    event.preventDefault();
    const msisdn = toE164(number);
    const digits = code.replace(/\s+/g, "");
    if (!msisdn) return setError("Enter the customer's mobile number.");
    if (!/^\d{6}$/.test(digits)) return setError("The code is six digits, on the customer's phone.");
    setError(null);
    take.mutate({ msisdn, digits });
  }

  return (
    <form onSubmit={submit} className="space-y-4" noValidate>
      <p className="text-sm text-ink-600">The customer makes a cash-out request for your shop in their app and shows you a six-digit code. Hand over the cash once it is done.</p>
      <Field label="Customer's mobile number" htmlFor="cash-out-number">
        <Input id="cash-out-number" inputMode="tel" placeholder="0300 1234567" value={number} onChange={(event) => setNumber(event.target.value)} />
      </Field>
      <Field label="Code" htmlFor="cash-out-code">
        <Input id="cash-out-code" inputMode="numeric" placeholder="123 456" value={code} onChange={(event) => setCode(event.target.value)} maxLength={7} autoComplete="one-time-code" />
      </Field>
      {(error ?? take.error?.message) && <Alert>{error ?? take.error?.message}</Alert>}
      <Button type="submit" size="lg" className="w-full" loading={take.isPending} disabled={disabled}>
        <ArrowUpFromLine className="h-5 w-5" aria-hidden /> Pay out
      </Button>
    </form>
  );
}

function FloatForm({ suspended, onStarted }: { suspended: boolean; onStarted: (id: string) => void }) {
  const balance = useBalance();
  const [direction, setDirection] = useState<"ToFloat" | "ToWallet">(suspended ? "ToWallet" : "ToFloat");
  const [amount, setAmount] = useState("");
  const [error, setError] = useState<string | null>(null);
  const move = useMutation({ mutationFn: (paisa: number) => agentsApi.moveFloat(direction, paisa), onSuccess: (transaction) => onStarted(transaction.id) });

  function submit(event: FormEvent) {
    event.preventDefault();
    const paisa = parseRupees(amount);
    if (paisa === null || paisa < 10_000 || paisa > 100_000_000) return setError("Rs. 100 to Rs. 1,000,000.");
    setError(null);
    move.mutate(paisa);
  }

  return (
    <form onSubmit={submit} className="space-y-4" noValidate>
      <div className="grid grid-cols-2 gap-2">
        {(["ToFloat", "ToWallet"] as const).map((option) => (
          <button
            key={option}
            type="button"
            onClick={() => setDirection(option)}
            aria-pressed={direction === option}
            disabled={suspended && option === "ToFloat"}
            className={cn("rounded-xl border p-3 text-left text-sm font-medium disabled:opacity-40", direction === option ? "border-brand-500 bg-brand-50" : "border-ink-100 hover:bg-ink-50")}
          >
            {option === "ToFloat" ? "Load float from my wallet" : "Move float to my wallet"}
          </button>
        ))}
      </div>
      <p className="text-sm text-ink-600">
        My wallet: <span className="tabular font-medium">{balance.data ? formatMoney(balance.data.balance) : "…"}</span>. From your wallet you can send money to a bank.
      </p>
      <Field label="Amount" htmlFor="float-amount">
        <Input id="float-amount" inputMode="decimal" placeholder="Rs. 10,000" value={amount} onChange={(event) => setAmount(event.target.value)} />
      </Field>
      {(error ?? move.error?.message) && <Alert>{error ?? move.error?.message}</Alert>}
      <Button type="submit" size="lg" className="w-full" loading={move.isPending}>
        {direction === "ToFloat" ? "Load float" : "Move to wallet"}
      </Button>
    </form>
  );
}

/** One transaction, refreshed every second until the Ledger has answered. */
function Result({ id, onFinal, onDone }: { id: string; onFinal: () => void; onDone: () => void }) {
  const transaction = useQuery({
    queryKey: agentKeys.transaction(id),
    queryFn: async () => {
      const current = await agentsApi.transaction(id);
      if (current.status !== "Processing") onFinal();
      return current;
    },
    refetchInterval: (query) => (query.state.data && query.state.data.status !== "Processing" ? false : 1_000),
  });
  const data = transaction.data;
  if (transaction.error) return <Alert>{transaction.error.message}</Alert>;
  if (!data) return <p className="text-sm text-ink-500">Loading…</p>;

  return (
    <div className="space-y-4 text-center">
      {data.status === "Completed" ? (
        <CheckCircle2 className="mx-auto h-14 w-14 text-brand-600" aria-hidden />
      ) : data.status === "Failed" ? (
        <XCircle className="mx-auto h-14 w-14 text-red-600" aria-hidden />
      ) : (
        <Loader2 className="mx-auto h-14 w-14 animate-spin text-sky-600" aria-hidden />
      )}
      <div>
        <p className="text-sm text-ink-500">{kindNames[data.kind]}</p>
        <p className="tabular text-3xl font-bold">{formatPaisa(data.amountPaisa)}</p>
        {data.customerName && <p className="text-sm text-ink-600">{data.customerName}</p>}
      </div>
      {data.status === "Completed" && data.kind === "CashOut" && <Alert tone="info">Hand over {formatPaisa(data.amountPaisa)} in cash.</Alert>}
      {data.status === "Completed" && data.kind === "CashIn" && <p className="text-sm text-ink-600">Deposited. You earned {formatPaisa(data.commissionPaisa)}.</p>}
      {data.status === "Failed" && <Alert>{data.failureReason}</Alert>}
      {data.status === "UnderReview" && <Alert tone="info">The wallet service has not answered yet. Our team will check this; do not hand over or take cash until it is done.</Alert>}
      {data.agentBalancePaisa !== null && <p className="text-xs text-ink-500">Float now {formatPaisa(data.agentBalancePaisa)}</p>}
      {data.status !== "Processing" && (
        <Button size="lg" className="w-full" onClick={onDone}>
          Next customer
        </Button>
      )}
    </div>
  );
}

function Recent({ onOpen }: { onOpen: (id: string) => void }) {
  const transactions = useQuery({ queryKey: agentKeys.transactions, queryFn: () => agentsApi.agentTransactions(15) });
  if (!transactions.data || transactions.data.length === 0) return null;
  return (
    <Card className="p-2">
      <h2 className="px-3 pt-2 pb-1 text-sm font-semibold text-ink-700">Today at the counter</h2>
      <ul>
        {transactions.data.map((transaction: AgentTransaction) => (
          <li key={transaction.id}>
            <button onClick={() => onOpen(transaction.id)} className="flex w-full items-center gap-3 rounded-xl px-3 py-2.5 text-left hover:bg-ink-50">
              <div className="min-w-0 flex-1">
                <p className="truncate text-sm font-medium">
                  {kindNames[transaction.kind]}
                  {transaction.customerName ? ` · ${transaction.customerName}` : ""}
                </p>
                <p className="text-xs text-ink-500">
                  {formatDay(transaction.requestedAt)}, {formatTime(transaction.requestedAt)}
                  {transaction.commissionPaisa > 0 && transaction.status === "Completed" ? ` · earned ${formatPaisa(transaction.commissionPaisa)}` : ""}
                </p>
              </div>
              <div className="text-right">
                <p className="tabular text-sm font-semibold">{formatPaisa(transaction.amountPaisa)}</p>
                <StatusPill status={transaction.status} />
              </div>
            </button>
          </li>
        ))}
      </ul>
    </Card>
  );
}

function Payouts() {
  const payouts = useQuery({ queryKey: agentKeys.payouts, queryFn: () => agentsApi.payouts(14) });
  if (!payouts.data || payouts.data.length === 0) return null;
  return (
    <Card className="p-2">
      <h2 className="px-3 pt-2 pb-1 text-sm font-semibold text-ink-700">Commission payouts</h2>
      <ul>
        {payouts.data.map((payout) => (
          <li key={payout.id} className="flex items-center gap-3 rounded-xl px-3 py-2.5">
            <div className="min-w-0 flex-1">
              <p className="text-sm font-medium">{formatDay(`${payout.businessDate}T12:00:00+05:00`)}</p>
              <p className="text-xs text-ink-500">
                {payout.transactionCount} transaction{payout.transactionCount === 1 ? "" : "s"} · tax {formatPaisa(payout.taxPaisa)}
                {payout.status === "Failed" && " · will be retried tonight"}
              </p>
            </div>
            <div className="text-right">
              <p className="tabular text-sm font-semibold">{formatPaisa(payout.netPaisa)}</p>
              <StatusPill status={payout.status === "Paid" ? "Completed" : payout.status} />
            </div>
          </li>
        ))}
      </ul>
    </Card>
  );
}
