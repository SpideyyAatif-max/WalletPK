"use client";

import { Activity, ArrowLeft, Inbox, ListTree, Loader2, LogOut, ShieldAlert, SlidersHorizontal, Workflow } from "lucide-react";
import Link from "next/link";
import { usePathname, useRouter } from "next/navigation";
import { useEffect, type ReactNode } from "react";
import { Brand } from "@/components/brand";
import { Card } from "@/components/ui/card";
import { devToolsEnabled } from "@/lib/config";
import { formatLocal } from "@/lib/phone";
import { isCompliance, isStaff, roleNames } from "@/lib/roles";
import { useSession } from "@/lib/session";
import { cn } from "@/lib/utils";

const sections = [
  { href: "/ops", label: "Live dashboard", icon: Activity, compliance: false },
  { href: "/ops/sagas", label: "Saga explorer", icon: Workflow, compliance: false },
  { href: "/ops/dead-letters", label: "Dead letters", icon: Inbox, compliance: false },
  { href: "/ops/fraud", label: "Fraud alerts", icon: ShieldAlert, compliance: true },
  { href: "/ops/fraud/cases", label: "Cases", icon: ListTree, compliance: true },
  { href: "/ops/fraud/rules", label: "Rules", icon: SlidersHorizontal, compliance: true },
];

/**
 * The operations portal (M14.C): back-office staff only (ops, compliance, finance, admin). Fraud pages are for
 * compliance and admin; the services enforce the same roles, so a hidden link is a convenience, not the guard.
 */
export default function OpsLayout({ children }: { children: ReactNode }) {
  const session = useSession();
  const router = useRouter();
  const pathname = usePathname();

  useEffect(() => {
    if (session.status === "signedOut") router.replace(`/login?next=${encodeURIComponent(pathname)}`);
  }, [session.status, router, pathname]);

  if (session.status !== "signedIn") {
    return (
      <div className="grid min-h-dvh place-items-center">
        <Loader2 className="h-6 w-6 animate-spin text-brand-600" aria-label="Loading" />
      </div>
    );
  }

  const user = session.user;
  if (!isStaff(user)) {
    return (
      <div className="mx-auto grid min-h-dvh max-w-md place-items-center px-5">
        <Card className="space-y-3 text-center">
          <ShieldAlert className="mx-auto h-10 w-10 text-amber-600" aria-hidden />
          <h1 className="text-lg font-semibold">Staff only</h1>
          <p className="text-sm text-ink-600">The operations portal is for WalletPK operations, compliance and finance staff.</p>
          {devToolsEnabled && <p className="text-sm text-ink-500">Development: give yourself a staff role under Account &amp; limits.</p>}
          <Link href="/wallet" className="inline-block text-sm font-medium text-brand-700 hover:underline">
            Back to your wallet
          </Link>
        </Card>
      </div>
    );
  }

  const compliance = isCompliance(user);
  const role = user.roles.map((name) => roleNames[name] ?? name).join(", ");

  return (
    <div className="flex min-h-dvh bg-ink-50">
      <aside className="sticky top-0 flex h-dvh w-60 shrink-0 flex-col border-r border-ink-100 bg-white px-3 py-5">
        <div className="px-2">
          <Brand />
          <p className="mt-1 text-xs font-semibold tracking-wide text-ink-500 uppercase">Operations</p>
        </div>
        <nav className="mt-6 flex-1 space-y-1" aria-label="Operations">
          {sections
            .filter((section) => compliance || !section.compliance)
            .map(({ href, label, icon: Icon }) => {
              const active = href === "/ops" || href === "/ops/fraud" ? pathname === href : pathname.startsWith(href);
              return (
                <Link
                  key={href}
                  href={href}
                  aria-current={active ? "page" : undefined}
                  className={cn("flex items-center gap-2.5 rounded-lg px-3 py-2 text-sm font-medium", active ? "bg-brand-50 text-brand-800" : "text-ink-700 hover:bg-ink-50")}
                >
                  <Icon className="h-4 w-4" aria-hidden />
                  {label}
                </Link>
              );
            })}
        </nav>
        <div className="space-y-2 border-t border-ink-100 px-2 pt-4 text-xs text-ink-500">
          <p>
            {user.phoneNumber ? formatLocal(user.phoneNumber) : "Staff"} · {role}
          </p>
          <div className="flex items-center justify-between">
            <Link href="/wallet" className="inline-flex items-center gap-1 font-medium text-brand-700 hover:underline">
              <ArrowLeft className="h-3.5 w-3.5" aria-hidden /> Wallet
            </Link>
            <button onClick={() => void session.logout()} className="inline-flex items-center gap-1 hover:text-ink-800">
              <LogOut className="h-3.5 w-3.5" aria-hidden /> Log out
            </button>
          </div>
        </div>
      </aside>
      <main className="min-w-0 flex-1 p-6">{children}</main>
    </div>
  );
}
