"use client";

import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { Lock, LockOpen, X } from "lucide-react";
import Link from "next/link";
import { useSearchParams } from "next/navigation";
import { Suspense, useState, type FormEvent } from "react";
import { ComplianceOnly, PageHeader, SeverityBadge, Table, When } from "@/components/ops/ui";
import { Button } from "@/components/ui/button";
import { Alert, Card } from "@/components/ui/card";
import { Input } from "@/components/ui/input";
import { formatPaisa } from "@/lib/money";
import { opsApi, opsKeys } from "@/lib/ops";
import { cn } from "@/lib/utils";

const severities = ["", "Critical", "High", "Medium", "Low"];

export default function FraudAlertsPage() {
  return (
    <ComplianceOnly>
      <Suspense>
        <FraudAlerts />
      </Suspense>
    </ComplianceOnly>
  );
}

/**
 * Fraud queue (M14.C4): the newest risk decisions worth a look, newest first. Opening a customer shows whether
 * they are frozen, their recent alerts, and lets compliance freeze, unfreeze or open a case.
 */
function FraudAlerts() {
  const params = useSearchParams();
  const [severity, setSeverity] = useState("");
  const [userId, setUserId] = useState<string | null>(params.get("userId"));
  const alerts = useQuery({
    queryKey: opsKeys.alerts({ severity }),
    queryFn: () => opsApi.alerts({ severity: severity || undefined }),
    refetchInterval: 10_000,
  });

  return (
    <div>
      <PageHeader title="Fraud alerts" description="Payments the fraud engine scored as risky: reviewed, asked for a code, or blocked. Open a customer to act." />
      <div className="mb-4 flex flex-wrap gap-2">
        {severities.map((value) => (
          <button
            key={value || "all"}
            onClick={() => setSeverity(value)}
            className={cn("rounded-full px-3 py-1.5 text-sm font-medium", value === severity ? "bg-brand-600 text-white" : "bg-white text-ink-700 ring-1 ring-ink-200 hover:bg-ink-50")}
          >
            {value || "All"}
          </button>
        ))}
      </div>
      {alerts.error && <Alert className="mb-4">{alerts.error.message}</Alert>}
      <div className={cn("grid gap-4", userId && "xl:grid-cols-[minmax(0,3fr)_minmax(0,2fr)]")}>
        <Table head={["When", "Severity", "Score", "Decision", "Rules", "Payment", "Customer"]} empty={alerts.isSuccess && alerts.data.length === 0 && "No alerts."}>
          {alerts.data?.map((alert) => (
            <tr key={alert.id} className={cn(alert.userId === userId && "bg-brand-50/60")}>
              <td className="px-4 py-2.5 text-ink-600">
                <When at={alert.createdAt} />
              </td>
              <td className="px-4 py-2.5">
                <SeverityBadge severity={alert.severity} />
              </td>
              <td className="tabular px-4 py-2.5 font-semibold">{alert.score}</td>
              <td className="px-4 py-2.5">{alert.action}</td>
              <td className="max-w-48 px-4 py-2.5 text-xs text-ink-600">{alert.rules.join(", ")}</td>
              <td className="px-4 py-2.5 text-xs">
                {alert.transactionType ?? alert.kind}
                {alert.amountPaisa != null && <span className="tabular block font-medium text-ink-900">{formatPaisa(Number(alert.amountPaisa))}</span>}
              </td>
              <td className="px-4 py-2.5">
                <button onClick={() => setUserId(alert.userId)} className="font-mono text-xs font-medium text-brand-700 hover:underline">
                  {alert.userId.slice(0, 8)}…
                </button>
              </td>
            </tr>
          ))}
        </Table>
        {userId && <AccountPanel userId={userId} onClose={() => setUserId(null)} />}
      </div>
    </div>
  );
}

function AccountPanel({ userId, onClose }: { userId: string; onClose: () => void }) {
  const queryClient = useQueryClient();
  const account = useQuery({ queryKey: opsKeys.account(userId), queryFn: () => opsApi.account(userId), refetchInterval: 5_000 });
  const [reason, setReason] = useState("");
  const [title, setTitle] = useState("");

  const refresh = () => {
    void queryClient.invalidateQueries({ queryKey: opsKeys.account(userId) });
    void queryClient.invalidateQueries({ queryKey: ["ops", "fraud", "cases"] });
  };
  const toggle = useMutation({
    mutationFn: (freeze: boolean) => (freeze ? opsApi.freeze(userId, reason.trim()) : opsApi.unfreeze(userId, reason.trim())),
    onSuccess: () => {
      setReason("");
      refresh();
    },
  });
  const openCase = useMutation({
    mutationFn: () => opsApi.openCase(userId, title.trim()),
    onSuccess: () => {
      setTitle("");
      refresh();
    },
  });

  const data = account.data;
  function submitFreeze(event: FormEvent) {
    event.preventDefault();
    if (data && reason.trim()) toggle.mutate(!data.frozen);
  }

  return (
    <Card className="space-y-4 self-start p-5 xl:sticky xl:top-6">
      <div className="flex items-start justify-between gap-3">
        <div className="min-w-0">
          <p className="text-xs font-semibold tracking-wide text-ink-500 uppercase">Customer</p>
          <p className="font-mono text-sm break-all">{userId}</p>
        </div>
        <button onClick={onClose} className="grid h-8 w-8 place-items-center rounded-full text-ink-500 hover:bg-ink-100" aria-label="Close">
          <X className="h-4 w-4" />
        </button>
      </div>
      {account.error && <Alert>{account.error.message}</Alert>}
      {data && (
        <>
          <div className={cn("flex items-start gap-3 rounded-xl p-4 text-sm", data.frozen ? "bg-red-50 text-red-900" : "bg-brand-50 text-brand-900")}>
            {data.frozen ? <Lock className="h-5 w-5 shrink-0" aria-hidden /> : <LockOpen className="h-5 w-5 shrink-0" aria-hidden />}
            <div>
              <p className="font-semibold">{data.frozen ? "Frozen" : "Not frozen"}</p>
              {data.reason && <p>{data.reason}</p>}
              {data.changedBy && data.changedAt && (
                <p className="text-xs opacity-80">
                  {data.changedBy}, <When at={data.changedAt} />
                  {data.frozen && !data.ledgerConfirmed && " · waiting for the Ledger to confirm"}
                </p>
              )}
            </div>
          </div>
          <form onSubmit={submitFreeze} className="flex gap-2" noValidate>
            <Input className="h-10" placeholder="Reason (required)" aria-label="Reason" value={reason} onChange={(event) => setReason(event.target.value)} />
            <Button type="submit" size="sm" className="h-10" variant={data.frozen ? "outline" : "danger"} disabled={!reason.trim()} loading={toggle.isPending}>
              {data.frozen ? "Unfreeze" : "Freeze"}
            </Button>
          </form>
          {toggle.error && <Alert>{toggle.error.message}</Alert>}
          <form
            onSubmit={(event) => {
              event.preventDefault();
              if (title.trim()) openCase.mutate();
            }}
            className="flex gap-2"
            noValidate
          >
            <Input className="h-10" placeholder="Case title" aria-label="Case title" value={title} onChange={(event) => setTitle(event.target.value)} />
            <Button type="submit" size="sm" variant="secondary" className="h-10" disabled={!title.trim()} loading={openCase.isPending}>
              Open case
            </Button>
          </form>
          {openCase.error && <Alert>{openCase.error.message}</Alert>}
          {openCase.data && (
            <Alert tone="success">
              Case #{openCase.data.case.number} opened.{" "}
              <Link href={`/ops/fraud/cases?id=${openCase.data.case.id}`} className="font-medium underline">
                Open it
              </Link>
            </Alert>
          )}
          <div>
            <h3 className="mb-2 text-sm font-semibold">Recent alerts</h3>
            <ul className="divide-y divide-ink-100 text-sm">
              {data.alerts.map((alert) => (
                <li key={alert.id} className="flex items-start justify-between gap-3 py-2">
                  <div>
                    <p className="text-ink-900">{alert.summary}</p>
                    <p className="text-xs text-ink-500">
                      <When at={alert.createdAt} /> · score {alert.score} · {alert.action}
                    </p>
                  </div>
                  <SeverityBadge severity={alert.severity} />
                </li>
              ))}
            </ul>
          </div>
          <Link href={`/ops/sagas?userId=${userId}`} className="inline-block text-sm font-medium text-brand-700 hover:underline">
            Their payments in the Saga explorer
          </Link>
        </>
      )}
    </Card>
  );
}
