"use client";

import { useInfiniteQuery, useQuery, useQueryClient } from "@tanstack/react-query";
import { RefreshCw, ShieldCheck, ShieldX } from "lucide-react";
import { useState } from "react";
import { PageHeader, Table, When } from "@/components/ops/ui";
import { Button } from "@/components/ui/button";
import { Alert, Card } from "@/components/ui/card";
import { reconApi, reportKeys, reportsApi } from "@/lib/reports";
import { isCompliance } from "@/lib/roles";
import { useSession } from "@/lib/session";
import { cn } from "@/lib/utils";

/** One row, whichever chain it is from. */
type Row = { key: string; position: number | null; at: string; actor: string; action: string; subject: string; details: string; hash: string | null };

type Verdict = { intact: boolean; summary: string };

type Chain = {
  id: "ledger" | "fraud" | "recon";
  name: string;
  description: string;
  compliance: boolean;
  page: (before?: number) => Promise<{ rows: Row[]; next?: number }>;
  verify: () => Promise<Verdict>;
};

const chains: Chain[] = [
  {
    id: "ledger",
    name: "Ledger",
    description: "Every posting, hold and account change, sealed into the chain moments after it happens (M3.10).",
    compliance: false,
    page: async (before) => {
      const rows = await reportsApi.ledgerAudit(before);
      return {
        rows: rows.map((row) => ({
          key: String(row.id),
          position: row.chainSequence,
          at: row.occurredAt,
          actor: row.actor,
          action: row.eventType,
          subject: `${row.subjectType}:${row.subjectId}`,
          details: row.payload,
          hash: row.hash,
        })),
        next: rows.length === 50 ? rows.at(-1)?.id : undefined,
      };
    },
    verify: async () => {
      const report = await reportsApi.ledgerVerify();
      return {
        intact: report.intact,
        summary: report.intact
          ? `${report.sealedCount.toLocaleString("en-PK")} rows chained, ${report.pendingCount} waiting to be sealed. Head ${report.headHash.slice(0, 12)}…`
          : `Broken at #${report.firstBrokenSequence}: ${report.problem}`,
      };
    },
  },
  {
    id: "fraud",
    name: "Compliance",
    description: "Freezes, cases, rule changes and report exports by compliance officers (FR-5.3).",
    compliance: true,
    page: async (before) => {
      const rows = await reportsApi.fraudAudit(before);
      return { rows: rows.map(entryRow), next: rows.length === 50 ? rows.at(-1)?.sequence : undefined };
    },
    verify: async () => {
      const result = await reportsApi.fraudVerify();
      return { intact: result.intact, summary: result.intact ? `${result.entries.toLocaleString("en-PK")} entries, every hash checks out.` : `Broken at #${result.brokenAt}.` };
    },
  },
  {
    id: "recon",
    name: "Reconciliation",
    description: "Every reconciliation run and every dispute, resolution and reopening by finance (M12.3).",
    compliance: false,
    page: async (before) => {
      const rows = await reconApi.audit(before);
      return { rows: rows.map(entryRow), next: rows.length === 50 ? rows.at(-1)?.sequence : undefined };
    },
    verify: async () => {
      const result = await reconApi.verify();
      return { intact: result.intact, summary: result.intact ? `${result.entries.toLocaleString("en-PK")} entries, every hash checks out.` : `Broken at #${result.brokenAt}.` };
    },
  },
];

function entryRow(entry: { sequence: number; at: string; actor: string; action: string; subject: string; details: string; hash: string }): Row {
  return { key: String(entry.sequence), position: entry.sequence, at: entry.at, actor: entry.actor, action: entry.action, subject: entry.subject, details: entry.details, hash: entry.hash };
}

/**
 * The audit viewer (M12.6): each service's hash-chained audit log, newest first, with a badge that recomputes the
 * whole chain. Any change to a past entry breaks every hash after it, and the badge says where.
 */
export default function AuditPage() {
  const { user } = useSession();
  const visible = chains.filter((chain) => !chain.compliance || isCompliance(user));
  const [chainId, setChainId] = useState<Chain["id"]>("ledger");
  const chain = visible.find((entry) => entry.id === chainId) ?? chains[0]!;

  return (
    <div>
      <PageHeader title="Audit log" description="Tamper-evident logs: each entry's hash covers the one before it." />
      <div className="mb-4 flex flex-wrap gap-2">
        {visible.map((entry) => (
          <button
            key={entry.id}
            onClick={() => setChainId(entry.id)}
            className={cn("rounded-full px-3 py-1.5 text-sm font-medium", entry.id === chain.id ? "bg-brand-600 text-white" : "bg-white text-ink-700 ring-1 ring-ink-200 hover:bg-ink-50")}
          >
            {entry.name}
          </button>
        ))}
      </div>
      <ChainView key={chain.id} chain={chain} />
    </div>
  );
}

function ChainView({ chain }: { chain: Chain }) {
  const queryClient = useQueryClient();
  const verify = useQuery({ queryKey: reportKeys.verify(chain.id), queryFn: chain.verify });
  const rows = useInfiniteQuery({
    queryKey: reportKeys.audit(chain.id),
    queryFn: ({ pageParam }) => chain.page(pageParam),
    initialPageParam: undefined as number | undefined,
    getNextPageParam: (last) => last.next,
  });
  const [open, setOpen] = useState<string | null>(null);
  const all = rows.data?.pages.flatMap((page) => page.rows) ?? [];

  return (
    <div className="space-y-4">
      <Card className="flex flex-wrap items-center gap-4">
        {verify.data ? (
          verify.data.intact ? (
            <ShieldCheck className="h-10 w-10 shrink-0 text-brand-600" aria-hidden />
          ) : (
            <ShieldX className="h-10 w-10 shrink-0 text-red-600" aria-hidden />
          )
        ) : (
          <ShieldCheck className="h-10 w-10 shrink-0 text-ink-300" aria-hidden />
        )}
        <div className="min-w-0 flex-1">
          <p className="font-semibold">
            {chain.name} chain:{" "}
            {verify.isPending ? "checking…" : verify.data ? (
              <span className={verify.data.intact ? "text-brand-700" : "text-red-700"}>{verify.data.intact ? "intact" : "BROKEN"}</span>
            ) : (
              "unknown"
            )}
          </p>
          <p className="text-sm text-ink-600">{verify.data?.summary ?? chain.description}</p>
        </div>
        <Button
          size="sm"
          variant="outline"
          onClick={() => {
            void queryClient.invalidateQueries({ queryKey: reportKeys.verify(chain.id) });
            void queryClient.invalidateQueries({ queryKey: reportKeys.audit(chain.id) });
          }}
          loading={verify.isFetching}
        >
          <RefreshCw className="h-4 w-4" aria-hidden /> Verify again
        </Button>
      </Card>
      {(verify.error ?? rows.error) && <Alert>{(verify.error ?? rows.error)!.message}</Alert>}

      <Table head={["#", "When", "Actor", "Action", "Subject", "Hash"]} empty={rows.isSuccess && all.length === 0 && "Nothing logged yet."}>
        {all.map((row) => (
          <FragmentRow key={row.key} row={row} open={open === row.key} onToggle={() => setOpen(open === row.key ? null : row.key)} />
        ))}
      </Table>
      {rows.hasNextPage && (
        <div className="text-center">
          <Button size="sm" variant="outline" onClick={() => void rows.fetchNextPage()} loading={rows.isFetchingNextPage}>
            Older entries
          </Button>
        </div>
      )}
    </div>
  );
}

function FragmentRow({ row, open, onToggle }: { row: Row; open: boolean; onToggle: () => void }) {
  return (
    <>
      <tr onClick={onToggle} className={cn("cursor-pointer hover:bg-ink-50", open && "bg-brand-50/60")}>
        <td className="tabular px-4 py-2 text-ink-500">{row.position ?? "pending"}</td>
        <td className="px-4 py-2 text-ink-600">
          <When at={row.at} />
        </td>
        <td className="px-4 py-2 font-mono text-xs">{row.actor}</td>
        <td className="px-4 py-2 font-medium">{row.action}</td>
        <td className="max-w-56 truncate px-4 py-2 font-mono text-xs" title={row.subject}>
          {row.subject}
        </td>
        <td className="px-4 py-2 font-mono text-xs text-ink-500">{row.hash ? `${row.hash.slice(0, 10)}…` : "—"}</td>
      </tr>
      {open && (
        <tr>
          <td colSpan={6} className="bg-ink-50 px-4 py-3">
            <pre className="max-h-64 overflow-auto font-mono text-xs whitespace-pre-wrap text-ink-800">{pretty(row.details)}</pre>
            {row.hash && <p className="mt-2 font-mono text-[11px] break-all text-ink-500">sha256 {row.hash}</p>}
          </td>
        </tr>
      )}
    </>
  );
}

function pretty(details: string): string {
  try {
    return JSON.stringify(JSON.parse(details), null, 2);
  } catch {
    return details;
  }
}
