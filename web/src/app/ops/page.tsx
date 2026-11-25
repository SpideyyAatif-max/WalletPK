"use client";

import { useQueries, useQuery } from "@tanstack/react-query";
import { ExternalLink } from "lucide-react";
import Link from "next/link";
import { LiveBadge } from "@/components/live-badge";
import { PageHeader, SeverityBadge, StatTile, When } from "@/components/ops/ui";
import { Alert, Card } from "@/components/ui/card";
import { grafanaUrl, jaegerUrl } from "@/lib/config";
import { opsApi, opsKeys, sagaServices, summarizeCounts, type SagaStateCount } from "@/lib/ops";
import { isCompliance } from "@/lib/roles";
import { useSession } from "@/lib/session";
import { useOpsHub } from "@/lib/use-ops-hub";

/**
 * Live operations dashboard (M14.C1, proposal 12.2): what is running and stuck across the services, what is
 * waiting for a person, whether the Ledger's books balance, and the live feed from /hubs/ops.
 */
export default function OpsDashboard() {
  const { user } = useSession();
  const { state, events } = useOpsHub();
  const counts = useQueries({
    queries: sagaServices.map((service) => ({
      queryKey: opsKeys.sagaCounts(service.id),
      queryFn: () => opsApi.sagaCounts(service.id),
      refetchInterval: 10_000,
    })),
  });
  const bills = useQuery({ queryKey: opsKeys.billDeadLetters, queryFn: opsApi.billDeadLetters, refetchInterval: 15_000 });
  const topUps = useQuery({ queryKey: opsKeys.topUpDeadLetters, queryFn: opsApi.topUpDeadLetters, refetchInterval: 15_000 });
  const invariants = useQuery({ queryKey: opsKeys.invariants, queryFn: opsApi.invariants, refetchInterval: 30_000 });

  const all: SagaStateCount[] = counts.flatMap((query) => query.data ?? []);
  const totals = summarizeCounts(all);
  const waiting = (bills.data?.length ?? 0) + (topUps.data?.length ?? 0);
  const failedCount = counts.find((query) => query.error);

  return (
    <div className="space-y-6">
      <PageHeader
        title="Live dashboard"
        description="Sagas running and finished in the last 24 hours, payments waiting for a person, and the books. Refreshes on its own."
        actions={
          <>
            <a href={`${grafanaUrl}/d/walletpk-overview`} target="_blank" rel="noreferrer" className="inline-flex items-center gap-1 rounded-lg border border-ink-200 bg-white px-3 py-1.5 text-sm font-medium text-ink-700 hover:bg-ink-50">
              Grafana <ExternalLink className="h-3.5 w-3.5" aria-hidden />
            </a>
            <a href={jaegerUrl} target="_blank" rel="noreferrer" className="inline-flex items-center gap-1 rounded-lg border border-ink-200 bg-white px-3 py-1.5 text-sm font-medium text-ink-700 hover:bg-ink-50">
              Jaeger <ExternalLink className="h-3.5 w-3.5" aria-hidden />
            </a>
          </>
        }
      />

      <div className="grid grid-cols-2 gap-4 lg:grid-cols-4">
        <StatTile label="Running Sagas" value={totals.running} hint="Payments in progress, all services" />
        <StatTile label="Waiting for a person" value={waiting} tone={waiting > 0 ? "warn" : "good"} hint="Dead-lettered bills and top-ups" />
        <StatTile label="Failed (24 h)" value={totals.failed} tone={totals.failed > 0 ? "warn" : "neutral"} hint={`of ${totals.finished} finished`} />
        <StatTile
          label="Ledger books"
          value={invariants.data ? (invariants.data.healthy ? "Balanced" : "Check!") : "…"}
          tone={invariants.data ? (invariants.data.healthy ? "good" : "bad") : "neutral"}
          hint={invariants.data ? `Checked ${new Date(invariants.data.checkedAt).toLocaleTimeString()}` : undefined}
        />
      </div>

      {failedCount?.error && <Alert>{failedCount.error.message}</Alert>}

      <div className="grid gap-6 xl:grid-cols-5">
        <Card className="space-y-3 p-4 xl:col-span-3">
          <div className="flex items-center justify-between">
            <h2 className="font-semibold">Live feed</h2>
            <LiveBadge state={state} className="bg-ink-800" />
          </div>
          {events.length === 0 ? (
            <p className="py-10 text-center text-sm text-ink-500">Fraud alerts, freezes, cases, stuck payments and low agent float appear here as they happen.</p>
          ) : (
            <ul className="divide-y divide-ink-100">
              {events.map((event) => (
                <li key={event.id} className="flex items-start gap-3 py-2.5">
                  <SeverityBadge severity={event.severity} />
                  <div className="min-w-0 flex-1">
                    <p className="text-sm font-medium text-ink-900">{event.title}</p>
                    {event.detail && <p className="text-sm text-ink-600">{event.detail}</p>}
                    <p className="text-xs text-ink-400">
                      {event.kind} · <When at={event.at} />
                      {event.userId && (
                        <>
                          {" · "}
                          <Link href={isCompliance(user) ? `/ops/fraud?userId=${event.userId}` : `/ops/sagas?userId=${event.userId}`} className="font-medium text-brand-700 hover:underline">
                            user {event.userId.slice(0, 8)}…
                          </Link>
                        </>
                      )}
                    </p>
                  </div>
                </li>
              ))}
            </ul>
          )}
        </Card>

        <Card className="space-y-3 p-4 xl:col-span-2">
          <h2 className="font-semibold">Running Sagas by state</h2>
          {sagaServices.map((service, index) => {
            const running = (counts[index]?.data ?? []).filter((count) => !count.isCompleted);
            return (
              <div key={service.id}>
                <p className="text-xs font-semibold tracking-wide text-ink-500 uppercase">{service.name}</p>
                {running.length === 0 ? (
                  <p className="py-1 text-sm text-ink-400">Nothing running</p>
                ) : (
                  <ul className="mt-1 space-y-1">
                    {running.map((count) => (
                      <li key={`${count.type}-${count.state}`}>
                        <Link
                          href={`/ops/sagas?service=${service.id}&type=${count.type}&state=${count.state}`}
                          className="flex items-center justify-between rounded-lg px-2 py-1 text-sm hover:bg-ink-50"
                        >
                          <span>
                            {count.type} · <span className="font-medium">{count.state}</span>
                          </span>
                          <span className="tabular font-semibold">{count.count}</span>
                        </Link>
                      </li>
                    ))}
                  </ul>
                )}
              </div>
            );
          })}
        </Card>
      </div>
    </div>
  );
}
