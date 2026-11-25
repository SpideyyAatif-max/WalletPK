"use client";

import { useMutation, useQuery } from "@tanstack/react-query";
import { CheckCircle2, Download, XCircle } from "lucide-react";
import { useState } from "react";
import { PageHeader, StatTile, Table, When } from "@/components/ops/ui";
import { Button } from "@/components/ui/button";
import { Alert, Card } from "@/components/ui/card";
import { Input } from "@/components/ui/input";
import { formatPaisa } from "@/lib/money";
import { pakistanDay, reportKeys, reportsApi } from "@/lib/reports";
import { isCompliance } from "@/lib/roles";
import { useSession } from "@/lib/session";

const journalNames: Record<string, string> = {
  CashIn: "Cash-in",
  CashOut: "Cash-out",
  P2PTransfer: "Wallet transfers",
  BankTransfer: "Bank transfers",
  BillPayment: "Bill payments",
  MobileTopup: "Mobile top-ups",
  MerchantPayment: "Merchant payments",
  Reversal: "Reversals",
  Adjustment: "Adjustments",
  CommissionPayout: "Commission payouts",
  FloatTransfer: "Agent float",
};

const accountNames: Record<string, string> = {
  CustomerWallet: "Customer wallets",
  MerchantWallet: "Merchant wallets",
  AgentWallet: "Agent floats",
  BANK_FLOAT: "Bank float",
  FEE_INCOME: "Fee income",
  TAX_PAYABLE: "Tax payable",
  BILLER_SETTLEMENT: "Biller settlement",
  TELCO_SETTLEMENT: "Telco settlement",
  COMMISSION_EXPENSE: "Commission expense",
  COMMISSION_PAYABLE: "Commission payable",
  SUSPENSE: "Suspense",
};

/**
 * Reports (M12.4, M12.7): the daily report (the day's business, the books now, invariant checks and the audit chain),
 * business figures over a range as CSV, and, for compliance, the suspicious-activity export.
 */
export default function ReportsPage() {
  const { user } = useSession();
  const [date, setDate] = useState(() => pakistanDay(1));
  const [from, setFrom] = useState(() => pakistanDay(30));
  const [to, setTo] = useState(() => pakistanDay(1));
  const [minSeverity, setMinSeverity] = useState("Medium");
  const report = useQuery({ queryKey: reportKeys.daily(date), queryFn: () => reportsApi.daily(date) });
  const business = useMutation({ mutationFn: () => reportsApi.businessCsv(from, to) });
  const suspicious = useMutation({ mutationFn: () => reportsApi.suspiciousCsv(from, to, minSeverity) });

  const data = report.data;
  const totals = data?.totals;
  const error = report.error ?? business.error ?? suspicious.error;

  return (
    <div>
      <PageHeader
        title="Reports"
        description="Straight from the ledger tables, by Pakistan day. The checks run when the report is opened."
        actions={
          <>
            <label className="sr-only" htmlFor="report-date">
              Day
            </label>
            <Input id="report-date" type="date" className="h-9 w-40" max={pakistanDay(0)} value={date} onChange={(event) => event.target.value && setDate(event.target.value)} />
          </>
        }
      />
      {error && <Alert className="mb-4">{error.message}</Alert>}

      <div className="mb-5 grid gap-3 sm:grid-cols-2 xl:grid-cols-6">
        <StatTile label="Journals" value={totals?.journalCount.toLocaleString("en-PK") ?? "…"} />
        <StatTile label="Money moved" value={totals ? formatPaisa(totals.volumePaisa) : "…"} />
        <StatTile label="Fee income" value={totals ? formatPaisa(totals.feeIncomePaisa) : "…"} tone="good" />
        <StatTile label="Tax collected" value={totals ? formatPaisa(totals.taxCollectedPaisa) : "…"} />
        <StatTile label="Wallets opened" value={totals?.walletsOpened ?? "…"} />
        <StatTile label="Active wallets" value={totals?.activeWallets ?? "…"} hint={totals ? `commission ${formatPaisa(totals.commissionPaisa)}` : undefined} />
      </div>

      <div className="grid gap-4 xl:grid-cols-2">
        <div>
          <h2 className="mb-2 text-sm font-semibold tracking-wide text-ink-500 uppercase">The day by type</h2>
          <Table head={["Type", "Journals", "Volume"]} empty={totals && totals.journals.length === 0 && "No journals that day."}>
            {totals?.journals.map((journal) => (
              <tr key={journal.type}>
                <td className="px-4 py-2.5">{journalNames[journal.type] ?? journal.type}</td>
                <td className="tabular px-4 py-2.5">{journal.count.toLocaleString("en-PK")}</td>
                <td className="tabular px-4 py-2.5">{formatPaisa(journal.volumePaisa)}</td>
              </tr>
            ))}
          </Table>

          <h2 className="mt-5 mb-2 text-sm font-semibold tracking-wide text-ink-500 uppercase">The books now</h2>
          <Table head={["Account", "Accounts", "Balance"]}>
            {data?.balances.map((balance) => (
              <tr key={balance.type}>
                <td className="px-4 py-2.5">{accountNames[balance.type] ?? balance.type}</td>
                <td className="tabular px-4 py-2.5">{balance.accounts.toLocaleString("en-PK")}</td>
                <td className="tabular px-4 py-2.5">{formatPaisa(balance.balancePaisa)}</td>
              </tr>
            ))}
          </Table>
        </div>

        <div className="space-y-4">
          <Card className="space-y-3">
            <div className="flex items-center justify-between">
              <h2 className="font-semibold">Invariant checks</h2>
              {data && (
                <span className={data.invariants.healthy ? "text-sm font-medium text-brand-700" : "text-sm font-medium text-red-700"}>
                  {data.invariants.healthy ? "All passed" : "Failures"} · {data.invariants.durationMilliseconds} ms
                </span>
              )}
            </div>
            <ul className="divide-y divide-ink-100 text-sm">
              {data?.invariants.checks.map((check) => (
                <li key={check.name} className="flex items-start gap-2 py-2">
                  {check.passed ? <CheckCircle2 className="mt-0.5 h-4 w-4 shrink-0 text-brand-600" aria-label="Passed" /> : <XCircle className="mt-0.5 h-4 w-4 shrink-0 text-red-600" aria-label="Failed" />}
                  <div>
                    <p className="font-medium">{check.name}</p>
                    <p className="text-ink-600">{check.passed ? check.description : check.detail}</p>
                  </div>
                </li>
              ))}
            </ul>
          </Card>

          <Card className="space-y-1">
            <h2 className="font-semibold">Ledger audit chain</h2>
            {data && (
              <p className={data.auditChain.intact ? "text-sm text-brand-800" : "text-sm text-red-700"}>
                {data.auditChain.intact
                  ? `Intact: ${data.auditChain.sealedCount.toLocaleString("en-PK")} rows chained, ${data.auditChain.pendingCount} waiting to be sealed.`
                  : `Broken at row #${data.auditChain.firstBrokenSequence}: ${data.auditChain.problem}`}
              </p>
            )}
            {data && (
              <p className="text-xs text-ink-500">
                Generated <When at={data.generatedAt} />
              </p>
            )}
          </Card>

          <Card className="space-y-3">
            <h2 className="font-semibold">Downloads</h2>
            <div className="flex flex-wrap items-end gap-2">
              <label className="text-sm">
                <span className="mb-1 block text-xs text-ink-500">From</span>
                <Input type="date" className="h-9 w-40" value={from} max={to} onChange={(event) => event.target.value && setFrom(event.target.value)} />
              </label>
              <label className="text-sm">
                <span className="mb-1 block text-xs text-ink-500">To</span>
                <Input type="date" className="h-9 w-40" value={to} min={from} max={pakistanDay(0)} onChange={(event) => event.target.value && setTo(event.target.value)} />
              </label>
            </div>
            <div className="flex flex-wrap gap-2">
              <Button size="sm" variant="outline" onClick={() => business.mutate()} loading={business.isPending}>
                <Download className="h-4 w-4" aria-hidden /> Business figures (CSV)
              </Button>
              {isCompliance(user) && (
                <>
                  <label className="sr-only" htmlFor="severity">
                    Lowest severity
                  </label>
                  <select id="severity" value={minSeverity} onChange={(event) => setMinSeverity(event.target.value)} className="h-9 rounded-xl bg-white px-3 text-sm ring-1 ring-ink-200">
                    <option value="Low">All alerts</option>
                    <option value="Medium">Medium and high</option>
                    <option value="High">High only</option>
                  </select>
                  <Button size="sm" variant="outline" onClick={() => suspicious.mutate()} loading={suspicious.isPending}>
                    <Download className="h-4 w-4" aria-hidden /> Suspicious activity (CSV)
                  </Button>
                </>
              )}
            </div>
            {business.isSuccess && <p className="text-xs text-ink-500">Saved {business.data}.</p>}
            {suspicious.isSuccess && <p className="text-xs text-ink-500">Saved {suspicious.data}. The export is on the compliance audit log.</p>}
          </Card>
        </div>
      </div>
    </div>
  );
}
