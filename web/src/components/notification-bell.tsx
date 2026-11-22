"use client";

import { useQuery } from "@tanstack/react-query";
import { Bell } from "lucide-react";
import Link from "next/link";
import { notificationKeys, notificationsApi } from "@/lib/notifications";
import { cn } from "@/lib/utils";

/** The header bell: the unread count, kept current by the live connection (and refreshed every minute). */
export function NotificationBell({ active }: { active: boolean }) {
  const unread = useQuery({ queryKey: notificationKeys.unread, queryFn: notificationsApi.unreadCount, refetchInterval: 60_000 });
  const count = unread.data ?? 0;
  const label = count > 0 ? `Notifications, ${count} unread` : "Notifications";

  return (
    <Link
      href="/wallet/notifications"
      className={cn("relative grid h-9 w-9 place-items-center rounded-full text-ink-600 hover:bg-ink-100", active && "bg-ink-100 text-brand-700")}
      aria-label={label}
      title={label}
      aria-current={active ? "page" : undefined}
    >
      <Bell className="h-4.5 w-4.5" aria-hidden />
      {count > 0 && (
        <span className="tabular absolute -top-0.5 -right-0.5 grid h-4.5 min-w-4.5 place-items-center rounded-full bg-red-600 px-1 text-[10px] font-bold text-white">
          {count > 99 ? "99+" : count}
        </span>
      )}
    </Link>
  );
}
