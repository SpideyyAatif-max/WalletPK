import type { HTMLAttributes } from "react";
import { cn } from "@/lib/utils";

export function Card({ className, ...props }: HTMLAttributes<HTMLDivElement>) {
  return <div className={cn("rounded-2xl border border-ink-100 bg-white p-5 shadow-sm", className)} {...props} />;
}

export function Alert({ tone = "error", className, ...props }: HTMLAttributes<HTMLDivElement> & { tone?: "error" | "info" | "success" }) {
  const tones = {
    error: "border-red-200 bg-red-50 text-red-800",
    info: "border-sky-200 bg-sky-50 text-sky-900",
    success: "border-brand-200 bg-brand-50 text-brand-900",
  };
  return <div role={tone === "error" ? "alert" : "status"} className={cn("rounded-xl border px-4 py-3 text-sm", tones[tone], className)} {...props} />;
}
