"use client";

import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { Download, Play, RefreshCw, X } from "lucide-react";
import { useState, type FormEvent } from "react";
import { PageHeader, StatTile, Table, When } from "@/components/ops/ui";
import { Button } from "@/components/ui/button";
import { Alert, Card } from "@/components/ui/card";
import { Input } from "@/components/ui/input";
import { formatPaisa } from "@/lib/money";
import { breakKindLabel, breakKinds, countByKind, pakistanDay, partyLabel, reconApi, reportKeys, resolutions, type ReconBreak } from "@/lib/reports";
import { isFinance } from "@/lib/roles";
import { useSession } from "@/lib/session";
import { cn } from "@/lib/utils";

const statusFilters = [
  { id: "open", label: "Open" },
  { id: "Resolved", label: "Resolved" },
  { id: "Cleared", label: "Cleared" },
  { id: "", label: "All" },
];

const paymentKinds: Record<string, string> = { bank_transfer: "Bank transfer", bill_payment: "Bill payment", topup: "Top-up" };

function BreakStatus({ status }: { status: string }) {
  const tone =
    status === "Open"
      ? "bg-red-50 text-red-800 ring-red-200"
      : status === "Disputed"
        ? "bg-amber-50 text-amber-800 ring-amber-200"
        : "bg-brand-50 text-brand-800 ring-brand-200";
  return <span className={cn("inline-flex rounded-full px-2 py-0.5 text-xs font-medium ring-1 ring-inset", tone)}>{status}</span>;
}

/**
 * Reconciliation (M12.1–M12.3): every night each bank's, biller's and network's settlement file is matched against
 * what WalletPK sent them. Pick a day, see each party's run, and work the breaks (finance and admin).
 */
export default function ReconciliationPage() {
  const { user } = useSession();
  const finance = isFinance(user);
  const queryClient = useQueryClient();
  const [date, setDate] = useState(() => pakistanDay(1));
  const [status, setStatus] = useState("open");
  const [kind, setKind] = useState("");
  const [openId, setOpenId] = useState<string | null>(null);

  const days = useQuery({ queryKey: reportKeys.days, queryFn: reconApi.days });
  const summary = useQuery({ queryKey: reportKeys.summary(date), queryFn: () => reconApi.summary(date) });
  const filter = { status, kind, from: date, to: date };
  const breaks = useQuery({ queryKey: reportKeys.breaks(filter), queryFn: () => reconApi.breaks(filter) });

  const run = useMutation({
    mutationFn: (parties?: string[]) => reconApi.start(date, parties),
    onSuccess: () => void queryClient.invalidateQueries({ queryKey: ["recon"] }),
  });
  const download = useMutation({ mutationFn: ({ id, party }: { id: string; party: string }) => reconApi.file(id, party, date) });

  const data = summary.data;
  const ran = data?.parties.filter((party) => party.run) ?? [];
  const failed = ran.filter((party) => party.run?.status === "Failed").length;
  const error = summary.error ?? breaks.error ?? run.error ?? download.error;
  const today = pakistanDay(0);

  return (
    <div>
      <PageHeader
        title="Reconciliation"
        description="Each party's settlement file for the day, matched against WalletPK's bank transfers, bill payments and top-ups. Runs every night at 00:30 for the day before."
        actions={
          <>
            <label className="sr-only" htmlFor="recon-date">
              Day
            </label>
            <Input id="recon-date" type="date" className="h-9 w-40" max={today} value={date} onChange={(event) => event.target.value && setDate(event.target.value)} />
            {finance && (
              <Button size="sm" onClick={() => run.mutate(undefined)} loading={run.isPending && run.variables === undefined}>
                <Play className="h-4 w-4" aria-hidden /> Run the day
              </Button>
            )}
          </>
        }
      />

      {days.data && days.data.length > 0 && (
        <div className="mb-4 flex flex-wrap gap-2" aria-label="Recent days">
          {days.data.map((day) => (
            <button
              key={day.date}
              onClick={() => setDate(day.date)}
              className={cn(
                "rounded-full px-3 py-1.5 text-sm font-medium",
                day.date === date ? "bg-brand-600 text-white" : "bg-white text-ink-700 ring-1 ring-ink-200 hover:bg-ink-50",
              )}
            >
              {day.date.slice(5)} · {day.breaks === 0 && day.failed === 0 ? "clean" : `${day.breaks} break${day.breaks === 1 ? "" : "s"}${day.failed ? `, ${day.failed} failed` : ""}`}
            </button>
          ))}
        </div>
      )}

      {error && <Alert className="mb-4">{error.message}</Alert>}
      {run.isSuccess && (
        <Alert tone="success" className="mb-4">
          Reconciled {run.data.length} {run.data.length === 1 ? "party" : "parties"} for {date}.
        </Alert>
      )}

      <div className="mb-5 grid gap-3 sm:grid-cols-2 xl:grid-cols-5">
        <StatTile label="Parties reconciled" value={data ? `${ran.length - failed}/${data.parties.length}` : "…"} tone={failed > 0 ? "warn" : "neutral"} hint={failed ? `${failed} could not be fetched` : undefined} />
        <StatTile label="Items matched" value={data ? `${data.matched}/${data.lines}` : "…"} tone="good" />
        <StatTile label="Open breaks" value={data?.openBreaks ?? "…"} tone={data && data.openBreaks > 0 ? "bad" : "good"} />
        <StatTile label="Providers settled" value={data ? formatPaisa(data.providerTotalPaisa) : "…"} />
        <StatTile label="WalletPK completed" value={data ? formatPaisa(data.ourTotalPaisa) : "…"} hint={data ? `difference ${formatPaisa(data.providerTotalPaisa - data.ourTotalPaisa)}` : undefined} />
      </div>

      <h2 className="mb-2 text-sm font-semibold tracking-wide text-ink-500 uppercase">Parties</h2>
      <Table head={["Party", "Run", "Lines", "Matched", "Breaks", "Provider total", "Our total", ""]}>
        {data?.parties.map(({ party, run: last, openBreaks }) => (
          <tr key={party}>
            <td className="px-4 py-2.5 font-medium">{partyLabel(party)}</td>
            <td className="px-4 py-2.5">
              {last ? (
                last.status === "Completed" ? (
                  <span className="text-ink-600">
                    <When at={last.finishedAt} />
                  </span>
                ) : (
                  <span className="text-red-700" title={last.problem ?? undefined}>
                    Failed: {last.problem}
                  </span>
                )
              ) : (
                <span className="text-ink-400">Not run</span>
              )}
            </td>
            <td className="tabular px-4 py-2.5">{last?.status === "Completed" ? last.lines : "—"}</td>
            <td className="tabular px-4 py-2.5">{last?.status === "Completed" ? last.matched : "—"}</td>
            <td className="tabular px-4 py-2.5">
              {last?.status === "Completed" ? (
                <span className={cn(last.breaks > 0 && "font-semibold text-red-700")}>
                  {last.breaks}
                  {openBreaks !== last.breaks && <span className="font-normal text-ink-500"> ({openBreaks} open)</span>}
                </span>
              ) : (
                "—"
              )}
            </td>
            <td className="tabular px-4 py-2.5">{last?.status === "Completed" ? formatPaisa(last.providerTotalPaisa) : "—"}</td>
            <td className="tabular px-4 py-2.5">{last?.status === "Completed" ? formatPaisa(last.ourTotalPaisa) : "—"}</td>
            <td className="px-4 py-2.5 text-right whitespace-nowrap">
              {last?.fileId && (
                <button onClick={() => download.mutate({ id: last.id, party })} className="mr-2 inline-flex items-center gap-1 text-xs font-medium text-brand-700 hover:underline" title="The file as fetched">
                  <Download className="h-3.5 w-3.5" aria-hidden /> File
                </button>
              )}
              {finance && (
                <button
                  onClick={() => run.mutate([party])}
                  disabled={run.isPending}
                  className="inline-flex items-center gap-1 text-xs font-medium text-brand-700 hover:underline disabled:opacity-50"
                  aria-label={`Run ${partyLabel(party)} again`}
                >
                  <RefreshCw className="h-3.5 w-3.5" aria-hidden /> Run
                </button>
              )}
            </td>
          </tr>
        ))}
      </Table>

      <div className="mt-6 mb-2 flex flex-wrap items-center gap-2">
        <h2 className="mr-2 text-sm font-semibold tracking-wide text-ink-500 uppercase">Breaks</h2>
        {statusFilters.map((entry) => (
          <button
            key={entry.id || "all"}
            onClick={() => setStatus(entry.id)}
            className={cn("rounded-full px-3 py-1 text-sm font-medium", entry.id === status ? "bg-ink-900 text-white" : "bg-white text-ink-700 ring-1 ring-ink-200 hover:bg-ink-50")}
          >
            {entry.label}
          </button>
        ))}
        <label className="sr-only" htmlFor="break-kind">
          Kind
        </label>
        <select id="break-kind" value={kind} onChange={(event) => setKind(event.target.value)} className="ml-auto h-9 rounded-full bg-white px-3 text-sm text-ink-700 ring-1 ring-ink-200">
          <option value="">Every kind</option>
          {Object.entries(breakKinds).map(([id, entry]) => (
            <option key={id} value={id}>
              {entry.label}
            </option>
          ))}
        </select>
      </div>
      {breaks.data && breaks.data.length > 0 && (
        <p className="mb-2 text-sm text-ink-600">
          {countByKind(breaks.data)
            .map((entry) => `${entry.count} ${breakKindLabel(entry.kind).toLowerCase()}`)
            .join(" · ")}
        </p>
      )}
      <div className={cn("grid gap-4", openId && "xl:grid-cols-[minmax(0,1fr)_400px]")}>
        <Table head={["Party", "Kind", "Reference", "Ours", "Provider", "At stake", "Status"]} empty={breaks.isSuccess && breaks.data.length === 0 && "No breaks."}>
          {breaks.data?.map((item) => (
            <tr key={item.id} onClick={() => setOpenId(item.id)} className={cn("cursor-pointer hover:bg-ink-50", item.id === openId && "bg-brand-50/60")}>
              <td className="px-4 py-2.5">{partyLabel(item.party)}</td>
              <td className="px-4 py-2.5" title={breakKinds[item.kind as keyof typeof breakKinds]?.hint}>
                {breakKindLabel(item.kind)}
                {item.kind === "Duplicate" && <span className="text-ink-500"> ×{item.linesInFile}</span>}
              </td>
              <td className="px-4 py-2.5 font-mono text-xs">{item.providerReference ?? item.key.slice(0, 8)}</td>
              <td className="tabular px-4 py-2.5">{item.ourAmountPaisa === null ? "—" : formatPaisa(item.ourAmountPaisa)}</td>
              <td className="tabular px-4 py-2.5">{item.providerAmountPaisa === null ? "—" : formatPaisa(item.providerAmountPaisa)}</td>
              <td className="tabular px-4 py-2.5 font-medium">{formatPaisa(item.atStakePaisa)}</td>
              <td className="px-4 py-2.5">
                <BreakStatus status={item.status} />
              </td>
            </tr>
          ))}
        </Table>
        {openId && <BreakPanel id={openId} finance={finance} onClose={() => setOpenId(null)} />}
      </div>
    </div>
  );
}

type Action = { kind: "dispute" | "resolve" | "reopen"; resolution: string; note: string };

function BreakPanel({ id, finance, onClose }: { id: string; finance: boolean; onClose: () => void }) {
  const queryClient = useQueryClient();
  const detail = useQuery({ queryKey: reportKeys.break(id), queryFn: () => reconApi.break(id) });
  const [action, setAction] = useState<Action | null>(null);
  const act = useMutation({
    mutationFn: (next: Action) =>
      next.kind === "dispute" ? reconApi.dispute(id, next.note) : next.kind === "resolve" ? reconApi.resolve(id, next.resolution, next.note) : reconApi.reopen(id, next.note),
    onSuccess: () => {
      setAction(null);
      void queryClient.invalidateQueries({ queryKey: ["recon"] });
    },
  });

  const data = detail.data;
  const item: ReconBreak | undefined = data?.break;
  const open = item?.status === "Open" || item?.status === "Disputed";
  function submit(event: FormEvent) {
    event.preventDefault();
    if (action && action.note.trim().length >= 5) act.mutate({ ...action, note: action.note.trim() });
  }

  return (
    <Card className="space-y-4 self-start p-5 xl:sticky xl:top-6">
      <div className="flex items-start justify-between gap-3">
        <div className="min-w-0">
          <p className="text-xs font-semibold tracking-wide text-ink-500 uppercase">{item ? `${partyLabel(item.party)} · ${item.businessDate}` : "Break"}</p>
          <p className="font-semibold">{item ? breakKindLabel(item.kind) : "…"}</p>
        </div>
        <button onClick={onClose} className="grid h-8 w-8 place-items-center rounded-full text-ink-500 hover:bg-ink-100" aria-label="Close">
          <X className="h-4 w-4" />
        </button>
      </div>
      {(detail.error ?? act.error) && <Alert>{(detail.error ?? act.error)!.message}</Alert>}
      {item && data && (
        <>
          <p className="text-sm text-ink-700">{item.description}</p>
          <dl className="grid grid-cols-2 gap-x-4 gap-y-2 rounded-xl bg-ink-50 p-4 text-sm">
            <div>
              <dt className="text-xs text-ink-500">Status</dt>
              <dd>
                <BreakStatus status={item.status} />
                {item.resolution && <span className="ml-1 text-ink-600">{resolutions.find((entry) => entry.id === item.resolution)?.label ?? item.resolution}</span>}
              </dd>
            </div>
            <div>
              <dt className="text-xs text-ink-500">At stake</dt>
              <dd className="tabular font-semibold">{formatPaisa(item.atStakePaisa)}</dd>
            </div>
            <div>
              <dt className="text-xs text-ink-500">Provider reference</dt>
              <dd className="font-mono text-xs break-all">{item.providerReference ?? "—"}</dd>
            </div>
            <div>
              <dt className="text-xs text-ink-500">WalletPK payment</dt>
              <dd className="font-mono text-xs break-all">{item.ourId ?? "—"}</dd>
            </div>
            {data.payment && (
              <div className="col-span-2">
                <dt className="text-xs text-ink-500">Here</dt>
                <dd>
                  {paymentKinds[data.payment.kind] ?? data.payment.kind} of {formatPaisa(data.payment.amountPaisa)}, {data.payment.serviceStatus}
                  {data.payment.completedAt && (
                    <>
                      {" "}
                      on <When at={data.payment.completedAt} />
                    </>
                  )}
                </dd>
              </div>
            )}
            <div className="col-span-2">
              <dt className="text-xs text-ink-500">Found</dt>
              <dd>
                <When at={item.foundAt} />
              </dd>
            </div>
          </dl>

          {finance && !action && (
            <div className="flex flex-wrap gap-2">
              {item.status === "Open" && (
                <Button size="sm" variant="outline" onClick={() => setAction({ kind: "dispute", resolution: "", note: "" })}>
                  Dispute with provider…
                </Button>
              )}
              {open && (
                <Button size="sm" onClick={() => setAction({ kind: "resolve", resolution: "Adjusted", note: "" })}>
                  Resolve…
                </Button>
              )}
              {!open && (
                <Button size="sm" variant="outline" onClick={() => setAction({ kind: "reopen", resolution: "", note: "" })}>
                  Reopen…
                </Button>
              )}
            </div>
          )}

          {action && (
            <form onSubmit={submit} className="space-y-3 rounded-xl border border-ink-200 p-4" noValidate>
              <p className="text-sm font-semibold">{action.kind === "dispute" ? "Dispute with the provider" : action.kind === "resolve" ? "Resolve the break" : "Reopen the break"}</p>
              {action.kind === "resolve" && (
                <fieldset className="space-y-1.5">
                  <legend className="sr-only">Resolution</legend>
                  {resolutions.map((entry) => (
                    <label key={entry.id} className="flex items-start gap-2 text-sm">
                      <input type="radio" name="resolution" className="mt-1" checked={action.resolution === entry.id} onChange={() => setAction({ ...action, resolution: entry.id })} />
                      <span>
                        <span className="font-medium">{entry.label}</span> <span className="text-ink-500">— {entry.hint}</span>
                      </span>
                    </label>
                  ))}
                </fieldset>
              )}
              <Input className="h-10" placeholder="Note for the audit log (at least 5 characters)" aria-label="Note" value={action.note} onChange={(event) => setAction({ ...action, note: event.target.value })} />
              <div className="flex gap-2">
                <Button type="button" size="sm" variant="outline" onClick={() => setAction(null)}>
                  Cancel
                </Button>
                <Button type="submit" size="sm" disabled={action.note.trim().length < 5} loading={act.isPending}>
                  {action.kind === "dispute" ? "Mark disputed" : action.kind === "resolve" ? "Resolve" : "Reopen"}
                </Button>
              </div>
            </form>
          )}

          <div>
            <h3 className="mb-2 text-sm font-semibold">History</h3>
            <ul className="space-y-2 text-sm">
              {data.history.map((entry) => {
                const details = JSON.parse(entry.details) as { note?: string; to?: string };
                return (
                  <li key={entry.sequence} className="rounded-lg bg-ink-50 px-3 py-2">
                    <p className="font-medium">
                      {entry.action.replace("break.", "")}
                      {details.to && <span className="font-normal text-ink-500"> → {details.to}</span>}
                    </p>
                    {details.note && <p className="text-ink-700">{details.note}</p>}
                    <p className="text-xs text-ink-500">
                      {entry.actor}, <When at={entry.at} /> · #{entry.sequence}
                    </p>
                  </li>
                );
              })}
              {data.history.length === 0 && <li className="text-ink-500">Nothing done yet.</li>}
            </ul>
          </div>
        </>
      )}
    </Card>
  );
}
