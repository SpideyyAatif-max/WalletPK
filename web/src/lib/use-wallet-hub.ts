"use client";

import { HubConnectionBuilder, HubConnectionState, LogLevel, type HubConnection } from "@microsoft/signalr";
import { useQueryClient, type InfiniteData } from "@tanstack/react-query";
import { useEffect, useEffectEvent, useState } from "react";
import { publicApiUrl } from "./config";
import { hasGap, mergeBalance, mergeHistory } from "./live";
import { mergeNotification, notificationKeys, queriesFor, type LiveNotification, type NotificationPage, type PaymentUpdate } from "./notifications";
import { useSession } from "./session";
import type { LiveWalletEntry, StatementPage, WalletBalance } from "./types";
import { queryKeys } from "./wallet-api";

export type LiveState = "connecting" | "live" | "reconnecting" | "offline";

/**
 * Keeps one SignalR connection to /hubs/wallet (the Notifications service, through the gateway) while
 * signed in, and folds what it pushes into the TanStack Query cache:
 * <ul>
 * <li>a new wallet entry: the balance, the top of the history, and a refetch of the authoritative wallet
 * view (held and available);</li>
 * <li>a new notification: the notification list and the unread count (M11);</li>
 * <li>a payment that changed: its screen and the lists it is in are refetched (M11).</li>
 * </ul>
 * After a reconnect, or when a push shows that entries were missed, it refetches instead of guessing.
 */
export function useWalletHub(onEntry?: (entry: LiveWalletEntry) => void, onNotification?: (live: LiveNotification) => void): LiveState {
  const { status, getAccessToken } = useSession();
  const queryClient = useQueryClient();
  const [state, setState] = useState<LiveState>("connecting");
  const notify = useEffectEvent((live: LiveWalletEntry) => onEntry?.(live));
  const announce = useEffectEvent((live: LiveNotification) => onNotification?.(live));

  useEffect(() => {
    if (status !== "signedIn") return;

    const connection: HubConnection = new HubConnectionBuilder()
      .withUrl(`${publicApiUrl}/hubs/wallet`, { accessTokenFactory: async () => (await getAccessToken()) ?? "" })
      .withAutomaticReconnect([0, 2_000, 5_000, 10_000, 20_000, 30_000])
      .configureLogging(LogLevel.Warning)
      .build();

    const resync = () =>
      Promise.all([
        queryClient.invalidateQueries({ queryKey: queryKeys.wallet }),
        queryClient.invalidateQueries({ queryKey: notificationKeys.all }),
      ]);

    connection.on("NotificationReceived", (live: LiveNotification) => {
      queryClient.setQueryData<number>(notificationKeys.unread, live.unreadCount);
      queryClient.setQueryData<InfiniteData<NotificationPage, string | null> | undefined>(notificationKeys.list, (current) => mergeNotification(current, live));
      announce(live);
    });

    connection.on("PaymentChanged", (update: PaymentUpdate) => {
      for (const queryKey of queriesFor(update)) void queryClient.invalidateQueries({ queryKey });
    });

    connection.on("EntryPosted", (live: LiveWalletEntry) => {
      if (live.walletType !== "CustomerWallet") return;

      const balance = queryClient.getQueryData<WalletBalance>(queryKeys.balance);
      if (hasGap(balance, live)) {
        void resync();
      } else {
        queryClient.setQueryData<WalletBalance | undefined>(queryKeys.balance, (current) => mergeBalance(current, live));
        queryClient.setQueryData<InfiniteData<StatementPage, number | null> | undefined>(queryKeys.history({}), (current) =>
          mergeHistory(current, live),
        );
        // Filtered history views and held/available come from the server.
        void queryClient.invalidateQueries({ queryKey: queryKeys.me });
        void queryClient.invalidateQueries({
          queryKey: ["wallet", "history"],
          predicate: (query) => query.queryKey.slice(2).some((part) => part !== ""),
        });
      }
      notify(live);
    });

    connection.onreconnecting(() => setState("reconnecting"));
    connection.onreconnected(() => {
      setState("live");
      void resync();
    });
    connection.onclose(() => setState("offline"));

    let stopped = false;
    const start = async () => {
      setState("connecting");
      try {
        await connection.start();
        if (!stopped) {
          setState("live");
          void resync(); // Anything posted while we were connecting.
        }
      } catch {
        if (!stopped) {
          setState("offline");
          setTimeout(() => {
            if (!stopped && connection.state === HubConnectionState.Disconnected) void start();
          }, 5_000);
        }
      }
    };
    void start();

    return () => {
      stopped = true;
      void connection.stop();
    };
  }, [status, getAccessToken, queryClient]);

  return status === "signedIn" ? state : "offline";
}

