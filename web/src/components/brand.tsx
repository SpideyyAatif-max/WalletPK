import { Wallet } from "lucide-react";
import { cn } from "@/lib/utils";

export function Brand({ className, inverted = false }: { className?: string; inverted?: boolean }) {
  return (
    <span className={cn("inline-flex items-center gap-2 font-bold tracking-tight", inverted ? "text-white" : "text-ink-900", className)}>
      <span className={cn("grid h-8 w-8 place-items-center rounded-lg", inverted ? "bg-white/15" : "bg-brand-600 text-white")}>
        <Wallet className="h-4.5 w-4.5" aria-hidden />
      </span>
      WalletPK
    </span>
  );
}
