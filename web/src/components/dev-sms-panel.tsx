"use client";

import { useQuery } from "@tanstack/react-query";
import { MessageSquareText } from "lucide-react";
import { useId } from "react";
import { devToolsEnabled } from "@/lib/config";
import { formatLocal } from "@/lib/phone";
import { devTools } from "@/lib/wallet-api";

/**
 * Development only: the text messages the stand-in SMS gateway "sent" to this number, so you can
 * finish sign-up and login without a phone. Hidden unless NEXT_PUBLIC_DEV_TOOLS=true; the API
 * answers 404 outside Development anyway.
 */
export function DevSmsPanel({ phoneNumber, onUseCode }: { phoneNumber: string; onUseCode?: (code: string) => void }) {
  // Keyed per panel and never cached: a panel shown for a new code must not show the previous one first.
  const panel = useId();
  const { data, isError } = useQuery({
    queryKey: ["dev-sms", phoneNumber, panel],
    queryFn: () => devTools.sms(phoneNumber),
    enabled: devToolsEnabled,
    refetchInterval: 2_000,
    gcTime: 0,
  });

  if (!devToolsEnabled) return null;

  const latest = data?.[0];
  const code = latest?.message.match(/\b(\d{6})\b/)?.[1];

  return (
    <div className="rounded-xl border border-dashed border-amber-300 bg-amber-50 p-3 text-sm text-amber-900">
      <p className="flex items-center gap-1.5 font-semibold">
        <MessageSquareText className="h-4 w-4" aria-hidden /> Dev SMS inbox · {formatLocal(phoneNumber)}
      </p>
      {isError ? (
        <p className="mt-1">The inbox is not reachable (are the services running in Development?).</p>
      ) : latest ? (
        <div className="mt-1 flex items-center justify-between gap-3">
          <p className="min-w-0">{latest.message}</p>
          {code && onUseCode && (
            <button type="button" onClick={() => onUseCode(code)} className="shrink-0 rounded-lg bg-amber-200 px-2.5 py-1 font-semibold hover:bg-amber-300">
              Use {code}
            </button>
          )}
        </div>
      ) : (
        <p className="mt-1">No messages yet…</p>
      )}
    </div>
  );
}
