import { AlertTriangle, CheckCircle2, Circle, Clock, Loader2, XCircle } from "lucide-react";
import { formatDay, formatTime } from "@/lib/dates";
import { isFinal, stepName, type TimelineStep } from "@/lib/payments";
import { cn } from "@/lib/utils";

const tones: Record<string, string> = {
  Completed: "bg-brand-50 text-brand-800",
  Paid: "bg-brand-50 text-brand-800",
  Processing: "bg-sky-50 text-sky-800",
  Paying: "bg-sky-50 text-sky-800",
  Pending: "bg-amber-50 text-amber-800",
  Queued: "bg-amber-50 text-amber-800",
  Failed: "bg-red-50 text-red-700",
  Declined: "bg-red-50 text-red-700",
  Cancelled: "bg-ink-100 text-ink-600",
  Expired: "bg-ink-100 text-ink-600",
  NeedsReview: "bg-amber-50 text-amber-800",
};

/** A small coloured label for a transfer's or request's status. */
export function StatusPill({ status }: { status: string }) {
  return <span className={cn("inline-flex rounded-full px-2.5 py-0.5 text-xs font-semibold", tones[status] ?? "bg-ink-100 text-ink-700")}>{status === "NeedsReview" ? "Under review" : status}</span>;
}

/**
 * The live tracker: each step the Saga took, newest at the bottom, with a spinner on the current one
 * while the transfer is still moving.
 */
export function Timeline({ steps, status }: { steps: TimelineStep[]; status: string }) {
  const moving = !isFinal(status);
  return (
    <ol className="space-y-0" aria-label="Progress">
      {steps.map((step, index) => {
        const last = index === steps.length - 1;
        const icon = last && moving
          ? step.state === "Queued" ? <Clock className="h-5 w-5 text-amber-600" /> : <Loader2 className="h-5 w-5 animate-spin text-sky-600" />
          : ["Failed", "Declined", "Expired", "Cancelled"].includes(step.state) ? <XCircle className="h-5 w-5 text-red-600" />
          : ["Returning", "NeedsReview", "Queued"].includes(step.state) ? <AlertTriangle className="h-5 w-5 text-amber-600" />
          : last ? <CheckCircle2 className="h-5 w-5 text-brand-600" />
          : <Circle className="h-5 w-5 fill-brand-600 text-brand-600" />;
        return (
          <li key={step.sequence} className="relative flex gap-3 pb-4 last:pb-0">
            {!last && <span aria-hidden className="absolute top-6 left-2.5 h-full w-px bg-ink-200" />}
            <span className="relative z-10 shrink-0 bg-white">{icon}</span>
            <div className="min-w-0">
              <p className="text-sm font-semibold text-ink-900">{stepName(step.state)}</p>
              <p className="text-sm text-ink-600">{step.detail}</p>
              <p className="text-xs text-ink-400">
                {formatDay(step.at)}, {formatTime(step.at)}
              </p>
            </div>
          </li>
        );
      })}
    </ol>
  );
}
