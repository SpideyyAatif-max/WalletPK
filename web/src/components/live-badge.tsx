import type { LiveState } from "@/lib/use-wallet-hub";
import { cn } from "@/lib/utils";

const labels: Record<LiveState, string> = { connecting: "Connecting", live: "Live", reconnecting: "Reconnecting", offline: "Offline" };

/** Whether balance updates are arriving live over SignalR. */
export function LiveBadge({ state, className }: { state: LiveState; className?: string }) {
  return (
    <span className={cn("inline-flex items-center gap-1.5 rounded-full bg-white/15 px-2.5 py-1 text-xs font-medium text-white", className)} title="Balance updates over SignalR">
      <span
        className={cn(
          "h-2 w-2 rounded-full",
          state === "live" && "bg-brand-300 shadow-[0_0_0_3px_rgba(108,233,166,0.35)]",
          (state === "connecting" || state === "reconnecting") && "animate-pulse bg-amber-300",
          state === "offline" && "bg-red-400",
        )}
        aria-hidden
      />
      {labels[state]}
    </span>
  );
}
