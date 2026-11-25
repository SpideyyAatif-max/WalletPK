"use client";

import { AlertTriangle, CheckCircle2, Circle, ExternalLink, ShieldAlert, XCircle } from "lucide-react";
import type { ReactNode } from "react";
import { Card } from "@/components/ui/card";
import { formatDay, formatTime } from "@/lib/dates";
import { traceLink, type SagaTimelineStep } from "@/lib/ops";
import { isCompliance } from "@/lib/roles";
import { useSession } from "@/lib/session";
import { cn } from "@/lib/utils";

export function PageHeader({ title, description, actions }: { title: string; description?: string; actions?: ReactNode }) {
  return (
    <div className="mb-5 flex flex-wrap items-end justify-between gap-3">
      <div>
        <h1 className="text-2xl font-bold text-ink-900">{title}</h1>
        {description && <p className="mt-1 max-w-3xl text-sm text-ink-600">{description}</p>}
      </div>
      {actions && <div className="flex items-center gap-2">{actions}</div>}
    </div>
  );
}

export function StatTile({ label, value, tone = "neutral", hint }: { label: string; value: ReactNode; tone?: "neutral" | "good" | "warn" | "bad"; hint?: string }) {
  const tones = {
    neutral: "text-ink-900",
    good: "text-brand-700",
    warn: "text-amber-700",
    bad: "text-red-700",
  };
  return (
    <Card className="p-4">
      <p className="text-xs font-semibold tracking-wide text-ink-500 uppercase">{label}</p>
      <p className={cn("tabular mt-1 text-3xl font-bold", tones[tone])}>{value}</p>
      {hint && <p className="mt-1 text-xs text-ink-500">{hint}</p>}
    </Card>
  );
}

const finalBad = ["Failed", "Declined", "Expired", "Cancelled", "Refunded"];
const attention = ["DeadLettered", "NeedsReview", "Queued", "Retrying", "Returning", "PendingVerification", "Verifying"];

/** A Saga or payment state, coloured by what it means for operations. */
export function StateBadge({ state, completed }: { state: string; completed?: boolean }) {
  const tone = attention.includes(state)
    ? "bg-amber-50 text-amber-800 ring-amber-200"
    : finalBad.includes(state)
      ? "bg-red-50 text-red-800 ring-red-200"
      : completed || state === "Completed" || state === "Paid"
        ? "bg-brand-50 text-brand-800 ring-brand-200"
        : "bg-sky-50 text-sky-800 ring-sky-200";
  return <span className={cn("inline-flex rounded-full px-2 py-0.5 text-xs font-medium ring-1 ring-inset", tone)}>{state}</span>;
}

export function SeverityBadge({ severity }: { severity: string }) {
  const tone =
    severity === "Critical" || severity === "High"
      ? "bg-red-50 text-red-800 ring-red-200"
      : severity === "Medium"
        ? "bg-amber-50 text-amber-800 ring-amber-200"
        : "bg-ink-100 text-ink-700 ring-ink-200";
  return <span className={cn("inline-flex rounded-full px-2 py-0.5 text-xs font-medium ring-1 ring-inset", tone)}>{severity}</span>;
}

export function When({ at }: { at: string }) {
  return (
    <time dateTime={at} className="tabular whitespace-nowrap">
      {formatDay(at)}, {formatTime(at)}
    </time>
  );
}

/** Minutes, hours or days since `at`, for queues ("waiting 3 h"). */
export function since(at: string, now = Date.now()): string {
  const minutes = Math.max(0, Math.round((now - new Date(at).getTime()) / 60_000));
  if (minutes < 60) return `${minutes} min`;
  const hours = Math.round(minutes / 60);
  if (hours < 48) return `${hours} h`;
  return `${Math.round(hours / 24)} d`;
}

export function JsonBlock({ value }: { value: unknown }) {
  return <pre className="max-h-96 overflow-auto rounded-xl bg-ink-900 p-4 font-mono text-xs leading-relaxed text-ink-100">{JSON.stringify(value, null, 2)}</pre>;
}

export function ShortId({ id }: { id: string }) {
  return (
    <span className="font-mono text-xs" title={id}>
      {id.slice(0, 8)}…
    </span>
  );
}

/** A Saga's steps, each linked to the trace it ran in. */
export function SagaTimeline({ steps }: { steps: SagaTimelineStep[] }) {
  return (
    <ol className="space-y-0" aria-label="Timeline">
      {steps.map((step, index) => {
        const last = index === steps.length - 1;
        const icon = finalBad.includes(step.state) ? (
          <XCircle className="h-5 w-5 text-red-600" />
        ) : attention.includes(step.state) ? (
          <AlertTriangle className="h-5 w-5 text-amber-600" />
        ) : last ? (
          <CheckCircle2 className="h-5 w-5 text-brand-600" />
        ) : (
          <Circle className="h-5 w-5 fill-brand-600 text-brand-600" />
        );
        return (
          <li key={step.sequence} className="relative flex gap-3 pb-4 last:pb-0">
            {!last && <span aria-hidden className="absolute top-6 left-2.5 h-full w-px bg-ink-200" />}
            <span className="relative z-10 shrink-0 bg-white">{icon}</span>
            <div className="min-w-0 flex-1">
              <div className="flex flex-wrap items-baseline justify-between gap-2">
                <p className="text-sm font-semibold text-ink-900">
                  {step.state} <span className="font-normal text-ink-500">← {step.trigger}</span>
                </p>
                <span className="text-xs text-ink-400">
                  <When at={step.at} />
                </span>
              </div>
              <p className="text-sm text-ink-600">{step.detail}</p>
              {step.traceId && (
                <a href={traceLink(step.traceId)} target="_blank" rel="noreferrer" className="mt-0.5 inline-flex items-center gap-1 text-xs font-medium text-brand-700 hover:underline">
                  Trace {step.traceId.slice(0, 12)}… <ExternalLink className="h-3 w-3" aria-hidden />
                </a>
              )}
            </div>
          </li>
        );
      })}
    </ol>
  );
}

/** Fraud pages: compliance officers and admins only (the Fraud service refuses everyone else too). */
export function ComplianceOnly({ children }: { children: ReactNode }) {
  const { user } = useSession();
  if (isCompliance(user)) return <>{children}</>;
  return (
    <Card className="mx-auto max-w-lg space-y-2 text-center">
      <ShieldAlert className="mx-auto h-8 w-8 text-amber-600" aria-hidden />
      <p className="font-semibold">Compliance only</p>
      <p className="text-sm text-ink-600">Fraud alerts, cases and rules are for compliance officers and admins.</p>
    </Card>
  );
}

export function Table({ head, children, empty }: { head: string[]; children: ReactNode; empty?: string | false }) {
  return (
    <div className="overflow-x-auto rounded-2xl border border-ink-100 bg-white shadow-sm">
      <table className="w-full text-left text-sm">
        <thead className="border-b border-ink-100 bg-ink-50 text-xs font-semibold tracking-wide text-ink-500 uppercase">
          <tr>
            {head.map((label) => (
              <th key={label} scope="col" className="px-4 py-2.5">
                {label}
              </th>
            ))}
          </tr>
        </thead>
        <tbody className="divide-y divide-ink-100">{children}</tbody>
      </table>
      {empty && <p className="px-4 py-8 text-center text-sm text-ink-500">{empty}</p>}
    </div>
  );
}
