"use client";

import { useInfiniteQuery, useQuery } from "@tanstack/react-query";
import { Search, X } from "lucide-react";
import { useSearchParams } from "next/navigation";
import { Suspense, useState, type FormEvent } from "react";
import { JsonBlock, PageHeader, SagaTimeline, ShortId, StateBadge, Table, When } from "@/components/ops/ui";
import { Button } from "@/components/ui/button";
import { Alert, Card } from "@/components/ui/card";
import { Input } from "@/components/ui/input";
import { opsApi, opsKeys, sagaServices, type SagaFilter, type SagaService, type SagaSummary } from "@/lib/ops";
import { cn } from "@/lib/utils";

const pageSize = 50;
const uuid = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

export default function SagaExplorerPage() {
  return (
    <Suspense>
      <SagaExplorer />
    </Suspense>
  );
}

/**
 * Saga explorer (M14.C2): search one service's Sagas, open one to see its data and every step, each with a
 * link to the trace it ran in. Links from the dashboard and the live feed land here with filters set.
 */
function SagaExplorer() {
  const params = useSearchParams();
  const initialService = (sagaServices.find((service) => service.id === params.get("service"))?.id ?? "payments") as SagaService;

  const [service, setService] = useState<SagaService>(initialService);
  const [form, setForm] = useState({
    type: params.get("type") ?? "",
    state: params.get("state") ?? "",
    status: "all",
    stuck: "",
    userId: params.get("userId") ?? "",
    correlationId: "",
  });
  const [filter, setFilter] = useState<SagaFilter>(() => toFilter(form));
  const [error, setError] = useState<string | null>(null);
  const [openId, setOpenId] = useState<string | null>(null);

  const results = useInfiniteQuery({
    queryKey: opsKeys.sagas(service, filter),
    queryFn: ({ pageParam }) => opsApi.sagas(service, { ...filter, before: pageParam ?? undefined, limit: pageSize }),
    initialPageParam: null as string | null,
    getNextPageParam: (last: SagaSummary[]) => (last.length === pageSize ? (last.at(-1)?.updatedAt ?? null) : null),
    refetchInterval: 10_000,
  });
  const rows = results.data?.pages.flat() ?? [];
  const types = sagaServices.find((entry) => entry.id === service)?.types ?? [];

  function search(event: FormEvent) {
    event.preventDefault();
    if (form.userId && !uuid.test(form.userId.trim())) return setError("A user ID looks like 0193f1c4-…: 36 characters.");
    if (form.stuck && !/^\d+$/.test(form.stuck)) return setError("Stuck for: whole minutes.");
    setError(null);
    setOpenId(null);
    setFilter(toFilter(form));
  }

  function choose(next: SagaService) {
    setService(next);
    setOpenId(null);
    setForm((current) => ({ ...current, type: "" }));
    setFilter((current) => ({ ...current, type: undefined }));
  }

  return (
    <div>
      <PageHeader title="Saga explorer" description="Every payment is a Saga: a small state machine whose every step is recorded. Find one, see where it is and why." />

      <div className="mb-4 flex flex-wrap gap-2" role="tablist" aria-label="Service">
        {sagaServices.map((entry) => (
          <button
            key={entry.id}
            role="tab"
            aria-selected={entry.id === service}
            onClick={() => choose(entry.id)}
            className={cn("rounded-full px-3 py-1.5 text-sm font-medium", entry.id === service ? "bg-brand-600 text-white" : "bg-white text-ink-700 ring-1 ring-ink-200 hover:bg-ink-50")}
          >
            {entry.name}
          </button>
        ))}
      </div>

      <Card className="mb-4 p-4">
        <form onSubmit={search} className="grid gap-3 md:grid-cols-3 xl:grid-cols-7" noValidate>
          <Select label="Type" value={form.type} onChange={(type) => setForm({ ...form, type })} options={[["", "Any type"], ...types.map((type) => [type, type] as [string, string])]} />
          <Labeled label="State">
            <Input className="h-10" placeholder="DeadLettered" value={form.state} onChange={(event) => setForm({ ...form, state: event.target.value })} />
          </Labeled>
          <Select label="Status" value={form.status} onChange={(status) => setForm({ ...form, status })} options={[["all", "All"], ["running", "Running"], ["finished", "Finished"]]} />
          <Labeled label="Stuck for (min)">
            <Input className="h-10" inputMode="numeric" placeholder="15" value={form.stuck} onChange={(event) => setForm({ ...form, stuck: event.target.value })} />
          </Labeled>
          <Labeled label="User ID">
            <Input className="h-10 font-mono text-xs" placeholder="0193f1c4-…" value={form.userId} onChange={(event) => setForm({ ...form, userId: event.target.value })} />
          </Labeled>
          <Labeled label="Correlation ID">
            <Input className="h-10 font-mono text-xs" value={form.correlationId} onChange={(event) => setForm({ ...form, correlationId: event.target.value })} />
          </Labeled>
          <div className="flex items-end">
            <Button type="submit" className="h-10 w-full">
              <Search className="h-4 w-4" aria-hidden /> Search
            </Button>
          </div>
        </form>
        {error && <Alert className="mt-3">{error}</Alert>}
      </Card>

      {results.error && <Alert className="mb-4">{results.error.message}</Alert>}

      <div className={cn("grid gap-4", openId && "xl:grid-cols-2")}>
        <div className="space-y-3">
          <Table head={["Type", "State", "ID", "Steps", "Last change"]} empty={results.isSuccess && rows.length === 0 && "No Sagas match."}>
            {rows.map((saga) => (
              <tr
                key={saga.id}
                onClick={() => setOpenId(saga.id)}
                className={cn("cursor-pointer hover:bg-ink-50", saga.id === openId && "bg-brand-50/60")}
                aria-selected={saga.id === openId}
              >
                <td className="px-4 py-2.5 font-medium whitespace-nowrap">{saga.type}</td>
                <td className="px-4 py-2.5">
                  <StateBadge state={saga.state} completed={saga.isCompleted} />
                </td>
                <td className="px-4 py-2.5">
                  <ShortId id={saga.id} />
                </td>
                <td className="tabular px-4 py-2.5">{saga.steps}</td>
                <td className="px-4 py-2.5 text-ink-600">
                  <When at={saga.updatedAt} />
                </td>
              </tr>
            ))}
          </Table>
          {results.hasNextPage && (
            <Button variant="outline" className="w-full" onClick={() => void results.fetchNextPage()} loading={results.isFetchingNextPage}>
              Older
            </Button>
          )}
        </div>
        {openId && <SagaPanel service={service} id={openId} onClose={() => setOpenId(null)} />}
      </div>
    </div>
  );
}

function SagaPanel({ service, id, onClose }: { service: SagaService; id: string; onClose: () => void }) {
  const saga = useQuery({
    queryKey: opsKeys.saga(service, id),
    queryFn: () => opsApi.saga(service, id),
    refetchInterval: (query) => (query.state.data?.saga.isCompleted ? false : 3_000),
  });
  const data = saga.data;

  return (
    <Card className="space-y-4 self-start p-5 xl:sticky xl:top-6">
      <div className="flex items-start justify-between gap-3">
        <div className="min-w-0">
          <p className="text-xs font-semibold tracking-wide text-ink-500 uppercase">{data?.saga.type ?? "Saga"}</p>
          <p className="font-mono text-sm break-all">{id}</p>
        </div>
        <button onClick={onClose} className="grid h-8 w-8 place-items-center rounded-full text-ink-500 hover:bg-ink-100" aria-label="Close">
          <X className="h-4 w-4" />
        </button>
      </div>
      {saga.error && <Alert>{saga.error.message}</Alert>}
      {data && (
        <>
          <dl className="grid grid-cols-2 gap-x-4 gap-y-2 rounded-xl bg-ink-50 p-4 text-sm">
            <Term label="State" value={<StateBadge state={data.saga.state} completed={data.saga.isCompleted} />} />
            <Term label="Steps" value={data.saga.steps} />
            <Term label="Started" value={<When at={data.saga.createdAt} />} />
            <Term label="Last change" value={<When at={data.saga.updatedAt} />} />
            {data.saga.wakeAt && <Term label="Next timer" value={<><When at={data.saga.wakeAt} /> ({data.saga.wakeReason})</>} />}
            {data.saga.correlationId && <Term label="Correlation" value={<span className="font-mono text-xs break-all">{data.saga.correlationId}</span>} />}
          </dl>
          <div>
            <h3 className="mb-2 text-sm font-semibold">Timeline</h3>
            <SagaTimeline steps={data.timeline} />
          </div>
          <div>
            <h3 className="mb-2 text-sm font-semibold">Data</h3>
            <JsonBlock value={data.data} />
          </div>
        </>
      )}
    </Card>
  );
}

function toFilter(form: { type: string; state: string; status: string; stuck: string; userId: string; correlationId: string }): SagaFilter {
  return {
    type: form.type || undefined,
    state: form.state.trim() || undefined,
    active: form.status === "all" ? undefined : form.status === "running",
    stuckForMinutes: form.stuck ? Number(form.stuck) : undefined,
    userId: form.userId.trim() || undefined,
    correlationId: form.correlationId.trim() || undefined,
  };
}

function Labeled({ label, children }: { label: string; children: React.ReactNode }) {
  return (
    <label className="block space-y-1 text-xs font-medium text-ink-600">
      <span>{label}</span>
      {children}
    </label>
  );
}

function Select({ label, value, onChange, options }: { label: string; value: string; onChange: (value: string) => void; options: [string, string][] }) {
  return (
    <Labeled label={label}>
      <select value={value} onChange={(event) => onChange(event.target.value)} className="h-10 w-full rounded-xl border border-ink-200 bg-white px-3 text-sm focus:border-brand-500 focus:ring-2 focus:ring-brand-500/30 focus:outline-none">
        {options.map(([optionValue, optionLabel]) => (
          <option key={optionValue} value={optionValue}>
            {optionLabel}
          </option>
        ))}
      </select>
    </Labeled>
  );
}

function Term({ label, value }: { label: string; value: React.ReactNode }) {
  return (
    <div className="min-w-0">
      <dt className="text-xs text-ink-500">{label}</dt>
      <dd className="tabular text-ink-900">{value}</dd>
    </div>
  );
}
