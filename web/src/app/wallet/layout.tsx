"use client";

import { History, Home, Loader2, LogOut, Receipt, ScanLine, Send, Smartphone } from "lucide-react";
import Link from "next/link";
import { usePathname, useRouter } from "next/navigation";
import { useCallback, useEffect, useMemo, useState, type ReactNode } from "react";
import { Brand } from "@/components/brand";
import { useToast } from "@/components/toast";
import { formatLocal } from "@/lib/phone";
import { useSession } from "@/lib/session";
import type { LiveWalletEntry } from "@/lib/types";
import { describeIncoming, useWalletHub } from "@/lib/use-wallet-hub";
import { cn } from "@/lib/utils";
import { WalletContext } from "./wallet-context";

const nav = [
  { href: "/wallet", label: "Home", icon: Home },
  { href: "/wallet/send", label: "Send", icon: Send },
  { href: "/wallet/pay", label: "Scan", icon: ScanLine },
  { href: "/wallet/topup", label: "Load", icon: Smartphone },
  { href: "/wallet/bills", label: "Bills", icon: Receipt },
  { href: "/wallet/history", label: "History", icon: History },
];

/** Everything under /wallet needs a session; this shell adds the header, the tabs and the live connection. */
export default function WalletLayout({ children }: { children: ReactNode }) {
  const session = useSession();
  const router = useRouter();

  useEffect(() => {
    if (session.status === "signedOut") router.replace("/login");
  }, [session.status, router]);

  if (session.status !== "signedIn") {
    return (
      <div className="grid min-h-dvh place-items-center">
        <Loader2 className="h-6 w-6 animate-spin text-brand-600" aria-label="Loading" />
      </div>
    );
  }

  return <WalletShell phoneNumber={session.user.phoneNumber} onLogout={session.logout}>{children}</WalletShell>;
}

function WalletShell({ children, phoneNumber, onLogout }: { children: ReactNode; phoneNumber: string | null; onLogout: () => Promise<void> }) {
  const pathname = usePathname();
  const toast = useToast();
  const [fresh, setFresh] = useState<Set<number>>(new Set());

  const onEntry = useCallback(
    (live: LiveWalletEntry) => {
      const incoming = describeIncoming(live);
      if (incoming) toast("Money received", incoming);
      setFresh((current) => new Set(current).add(live.entry.sequence));
      setTimeout(() => setFresh((current) => {
        const next = new Set(current);
        next.delete(live.entry.sequence);
        return next;
      }), 2_000);
    },
    [toast],
  );
  const live = useWalletHub(onEntry);
  const context = useMemo(() => ({ live, freshSequences: fresh }), [live, fresh]);

  return (
    <WalletContext.Provider value={context}>
      <div className="mx-auto flex min-h-dvh max-w-md flex-col bg-ink-50">
        <header className="flex items-center justify-between px-5 pt-5 pb-3">
          <Brand />
          <div className="flex items-center gap-2">
            {phoneNumber && <span className="text-xs text-ink-500">{formatLocal(phoneNumber)}</span>}
            <button onClick={() => void onLogout()} className="grid h-9 w-9 place-items-center rounded-full text-ink-600 hover:bg-ink-100" aria-label="Log out" title="Log out">
              <LogOut className="h-4.5 w-4.5" />
            </button>
          </div>
        </header>
        <main className="flex-1 px-5 pb-28">{children}</main>
        <nav className="fixed inset-x-0 bottom-0 z-40 border-t border-ink-100 bg-white/95 backdrop-blur">
          <ul className="mx-auto flex max-w-md">
            {nav.map(({ href, label, icon: Icon }) => {
              const active = href === "/wallet" ? pathname === href : pathname.startsWith(href);
              return (
                <li key={href} className="flex-1">
                  <Link href={href} className={cn("flex flex-col items-center gap-1 py-3 text-[11px] font-medium", active ? "text-brand-700" : "text-ink-500 hover:text-ink-800")} aria-current={active ? "page" : undefined}>
                    <Icon className="h-5 w-5" aria-hidden />
                    {label}
                  </Link>
                </li>
              );
            })}
          </ul>
        </nav>
      </div>
    </WalletContext.Provider>
  );
}
