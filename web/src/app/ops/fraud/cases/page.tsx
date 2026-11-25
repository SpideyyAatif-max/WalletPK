"use client";

import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { X } from "lucide-react";
import { useSearchParams } from "next/navigation";
import { Suspense, useState, type FormEvent } from "react";
import { ComplianceOnly, PageHeader, SeverityBadge, StateBadge, Table, When } from "@/components/ops/ui";
import { Button } from "@/components/ui/button";
import { Alert, Card } from "@/components/ui/card";
import { Input } from "@/components/ui/input";
import { opsApi, opsKeys } from "@/lib/ops";
import { cn } from "@/lib/utils";

const statuses = [
  { id: "Open", label: "Open" },
  { id: "InReview", label: "In review" },
  { id: "Closed", label: "Closed" },
  { id: "", label: "All" },
];

export default function CasesPage() {
  return (
    <ComplianceOnly>
      <Suspense>
        <Cases />
      </Suspense>
    </ComplianceOnly>
  );
}

/** Fraud cases (M14.C4, M10.7): alerts grouped per customer, worked by compliance and closed with a resolution. */
function Cases() {
  const params = useSearchParams();
  const [status, setStatus] = useState(params.get("id") ? "" : "Open");
  const [openId, setOpenId] = useState<string | null>(params.get("id"));
  const cases = useQuery({ queryKey: opsKeys.cases(status), queryFn: () => opsApi.cases(status || undefined), refetchInterval: 10_000 });

  return (
    <div>
      <PageHeader title="Cases" description="Opened automatically for blocks and mule patterns, or by hand. Take one, add notes, and close it as fraud or a false positive." />
      <div className="mb-4 flex flex-wrap gap-2">
        {statuses.map((entry) => (
          <button
            key={entry.id || "all"}
            onClick={() => setStatus(entry.id)}
            className={cn("rounded-full px-3 py-1.5 text-sm font-medium", entry.id === status ? "bg-brand-600 text-white" : "bg-white text-ink-700 ring-1 ring-ink-200 hover:bg-ink-50")}
          >
            {entry.label}
          </button>
        ))}
      </div>
      {cases.error && <Alert className="mb-4">{cases.error.message}</Alert>}
      <div className={cn("grid gap-4", openId && "xl:grid-cols-2")}>
        <Table head={["Case", "Title", "Status", "Assigned to", "Opened"]} empty={cases.isSuccess && cases.data.length === 0 && "No cases."}>
          {cases.data?.map((fraudCase) => (
            <tr key={fraudCase.id} onClick={() => setOpenId(fraudCase.id)} className={cn("cursor-pointer hover:bg-ink-50", fraudCase.id === openId && "bg-brand-50/60")}>
              <td className="tabular px-4 py-2.5 font-semibold">#{fraudCase.number}</td>
              <td className="px-4 py-2.5">{fraudCase.title}</td>
              <td className="px-4 py-2.5">
                <StateBadge state={fraudCase.resolution ?? fraudCase.status} completed={fraudCase.status === "Closed"} />
              </td>
              <td className="px-4 py-2.5 text-ink-600">{fraudCase.assignedTo ?? "—"}</td>
              <td className="px-4 py-2.5 text-ink-600">
                <When at={fraudCase.createdAt} />
              </td>
            </tr>
          ))}
        </Table>
        {openId && <CasePanel id={openId} onClose={() => setOpenId(null)} />}
      </div>
    </div>
  );
}

function CasePanel({ id, onClose }: { id: string; onClose: () => void }) {
  const queryClient = useQueryClient();
  const view = useQuery({ queryKey: opsKeys.case(id), queryFn: () => opsApi.case(id) });
  const [note, setNote] = useState("");
  const [closing, setClosing] = useState<{ resolution: "ConfirmedFraud" | "FalsePositive"; unfreeze: boolean; note: string } | null>(null);

  const done = (next: unknown) => {
    queryClient.setQueryData(opsKeys.case(id), next);
    void queryClient.invalidateQueries({ queryKey: ["ops", "fraud"] });
  };
  const assign = useMutation({ mutationFn: () => opsApi.assignCase(id), onSuccess: done });
  const addNote = useMutation({
    mutationFn: () => opsApi.noteCase(id, note.trim()),
    onSuccess: (next) => {
      setNote("");
      done(next);
    },
  });
  const close = useMutation({
    mutationFn: () => opsApi.closeCase(id, { resolution: closing!.resolution, unfreeze: closing!.unfreeze, note: closing!.note.trim() || undefined }),
    onSuccess: (next) => {
      setClosing(null);
      done(next);
    },
  });

  const data = view.data;
  const error = view.error ?? assign.error ?? addNote.error ?? close.error;
  function submitNote(event: FormEvent) {
    event.preventDefault();
    if (note.trim()) addNote.mutate();
  }

  return (
    <Card className="space-y-4 self-start p-5 xl:sticky xl:top-6">
      <div className="flex items-start justify-between gap-3">
        <div className="min-w-0">
          <p className="text-xs font-semibold tracking-wide text-ink-500 uppercase">Case {data ? `#${data.case.number}` : ""}</p>
          <p className="font-semibold">{data?.case.title ?? "…"}</p>
        </div>
        <button onClick={onClose} className="grid h-8 w-8 place-items-center rounded-full text-ink-500 hover:bg-ink-100" aria-label="Close">
          <X className="h-4 w-4" />
        </button>
      </div>
      {error && <Alert>{error.message}</Alert>}
      {data && (
        <>
          <dl className="grid grid-cols-2 gap-x-4 gap-y-2 rounded-xl bg-ink-50 p-4 text-sm">
            <div>
              <dt className="text-xs text-ink-500">Status</dt>
              <dd>
                <StateBadge state={data.case.resolution ?? data.case.status} completed={data.case.status === "Closed"} />
              </dd>
            </div>
            <div>
              <dt className="text-xs text-ink-500">Assigned to</dt>
              <dd>{data.case.assignedTo ?? "Nobody"}</dd>
            </div>
            <div className="col-span-2">
              <dt className="text-xs text-ink-500">Customers</dt>
              <dd className="font-mono text-xs break-all">{data.case.subjects.join(", ")}</dd>
            </div>
          </dl>

          {data.case.status !== "Closed" && (
            <div className="flex flex-wrap gap-2">
              <Button size="sm" variant="outline" onClick={() => assign.mutate()} loading={assign.isPending}>
                Take it
              </Button>
              <Button size="sm" variant="danger" onClick={() => setClosing({ resolution: "ConfirmedFraud", unfreeze: false, note: "" })}>
                Close as fraud…
              </Button>
              <Button size="sm" variant="secondary" onClick={() => setClosing({ resolution: "FalsePositive", unfreeze: true, note: "" })}>
                Close as false positive…
              </Button>
            </div>
          )}

          {closing && (
            <form
              onSubmit={(event) => {
                event.preventDefault();
                close.mutate();
              }}
              className="space-y-3 rounded-xl border border-ink-200 p-4"
            >
              <p className="text-sm font-semibold">{closing.resolution === "ConfirmedFraud" ? "Confirmed fraud" : "False positive"}</p>
              {closing.resolution === "FalsePositive" && (
                <label className="flex items-center gap-2 text-sm">
                  <input type="checkbox" checked={closing.unfreeze} onChange={(event) => setClosing({ ...closing, unfreeze: event.target.checked })} />
                  Unfreeze the customers
                </label>
              )}
              <Input className="h-10" placeholder="Closing note" aria-label="Closing note" value={closing.note} onChange={(event) => setClosing({ ...closing, note: event.target.value })} />
              <div className="flex gap-2">
                <Button type="button" size="sm" variant="outline" onClick={() => setClosing(null)}>
                  Cancel
                </Button>
                <Button type="submit" size="sm" loading={close.isPending}>
                  Close case
                </Button>
              </div>
            </form>
          )}

          <div>
            <h3 className="mb-2 text-sm font-semibold">Alerts</h3>
            <ul className="divide-y divide-ink-100 text-sm">
              {data.alerts.map((alert) => (
                <li key={alert.id} className="flex items-start justify-between gap-3 py-2">
                  <div>
                    <p>{alert.summary}</p>
                    <p className="text-xs text-ink-500">
                      <When at={alert.createdAt} /> · score {alert.score} · {alert.rules.join(", ")}
                    </p>
                  </div>
                  <SeverityBadge severity={alert.severity} />
                </li>
              ))}
              {data.alerts.length === 0 && <li className="py-2 text-ink-500">No alerts linked.</li>}
            </ul>
          </div>

          <div>
            <h3 className="mb-2 text-sm font-semibold">Notes</h3>
            <ul className="space-y-2 text-sm">
              {data.notes.map((entry) => (
                <li key={entry.id} className="rounded-lg bg-ink-50 px-3 py-2">
                  <p>{entry.text}</p>
                  <p className="text-xs text-ink-500">
                    {entry.author}, <When at={entry.createdAt} />
                  </p>
                </li>
              ))}
            </ul>
            {data.case.status !== "Closed" && (
              <form onSubmit={submitNote} className="mt-2 flex gap-2" noValidate>
                <Input className="h-10" placeholder="Add a note" aria-label="Note" value={note} onChange={(event) => setNote(event.target.value)} />
                <Button type="submit" size="sm" className="h-10" disabled={!note.trim()} loading={addNote.isPending}>
                  Add
                </Button>
              </form>
            )}
          </div>
        </>
      )}
    </Card>
  );
}
