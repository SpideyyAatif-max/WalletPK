"use client";

import { useInfiniteQuery, useMutation, useQueryClient, type InfiniteData } from "@tanstack/react-query";
import { Bell, CheckCheck, Settings } from "lucide-react";
import Link from "next/link";
import { Button } from "@/components/ui/button";
import { Alert, Card } from "@/components/ui/card";
import { formatDay, formatTime } from "@/lib/dates";
import { markReadInPages, notificationKeys, notificationsApi, type AppNotification, type NotificationPage } from "@/lib/notifications";
import { cn } from "@/lib/utils";

/**
 * The notification centre (M11.4): everything WalletPK told you, newest first, in the language you chose.
 * New ones arrive live; opening one marks it read.
 */
export default function NotificationsPage() {
  const queryClient = useQueryClient();
  const list = useInfiniteQuery({
    queryKey: notificationKeys.list,
    queryFn: ({ pageParam }) => notificationsApi.page(pageParam),
    initialPageParam: null as string | null,
    getNextPageParam: (last) => last.nextBefore,
  });

  const markRead = useMutation({
    mutationFn: (id: string) => notificationsApi.markRead(id),
    onSuccess: (unread, id) => {
      queryClient.setQueryData<number>(notificationKeys.unread, unread);
      queryClient.setQueryData<InfiniteData<NotificationPage, string | null> | undefined>(notificationKeys.list, (current) => markReadInPages(current, id, unread));
    },
  });

  const readAll = useMutation({
    mutationFn: notificationsApi.readAll,
    onSuccess: () => {
      queryClient.setQueryData<number>(notificationKeys.unread, 0);
      void queryClient.invalidateQueries({ queryKey: notificationKeys.list });
    },
  });

  const items = list.data?.pages.flatMap((page) => page.items) ?? [];
  const unread = list.data?.pages[0]?.unreadCount ?? 0;

  return (
    <div className="space-y-4">
      <div className="flex items-center justify-between">
        <h1 className="text-xl font-semibold">Notifications</h1>
        <div className="flex items-center gap-1">
          {unread > 0 && (
            <Button variant="ghost" size="sm" onClick={() => readAll.mutate()} loading={readAll.isPending}>
              <CheckCheck className="h-4 w-4" aria-hidden /> Mark all read
            </Button>
          )}
          <Link href="/wallet/notifications/settings" className="grid h-9 w-9 place-items-center rounded-full text-ink-600 hover:bg-ink-100" aria-label="Notification settings" title="Notification settings">
            <Settings className="h-4.5 w-4.5" aria-hidden />
          </Link>
        </div>
      </div>

      {list.isError && <Alert>{list.error.message}</Alert>}

      {list.isSuccess && items.length === 0 && (
        <Card className="space-y-2 py-10 text-center">
          <Bell className="mx-auto h-10 w-10 text-ink-300" aria-hidden />
          <p className="text-sm text-ink-500">Nothing yet. Payments, requests and account news will show up here.</p>
        </Card>
      )}

      {items.length > 0 && (
        <Card className="divide-y divide-ink-100 p-0">
          {items.map((item) => (
            <NotificationRow key={item.id} item={item} onOpen={() => !item.read && markRead.mutate(item.id)} />
          ))}
        </Card>
      )}

      {list.hasNextPage && (
        <Button variant="outline" className="w-full" onClick={() => void list.fetchNextPage()} loading={list.isFetchingNextPage}>
          Older notifications
        </Button>
      )}
    </div>
  );
}

function NotificationRow({ item, onOpen }: { item: AppNotification; onOpen: () => void }) {
  return (
    <button type="button" onClick={onOpen} className={cn("flex w-full items-start gap-3 px-4 py-3 text-left", !item.read && "bg-brand-50/60")}>
      <span className={cn("mt-2 h-2 w-2 shrink-0 rounded-full", item.read ? "bg-transparent" : "bg-brand-600")} aria-label={item.read ? undefined : "Unread"} />
      <span className="min-w-0 flex-1">
        <span className="flex items-baseline justify-between gap-2">
          <span dir="auto" className={cn("text-sm", item.read ? "font-medium text-ink-700" : "font-semibold text-ink-900")}>
            {item.title}
          </span>
          <span className="shrink-0 text-xs text-ink-400">
            {formatDay(item.createdAt)}, {formatTime(item.createdAt)}
          </span>
        </span>
        <span dir="auto" className="mt-0.5 block text-sm leading-relaxed text-ink-600">
          {item.body}
        </span>
      </span>
    </button>
  );
}
