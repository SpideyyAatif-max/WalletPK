"use client";

import { HubConnectionBuilder, HubConnectionState, LogLevel, type HubConnection } from "@microsoft/signalr";
import { useQueryClient, type InfiniteData } from "@tanstack/react-query";
import { useEffect, useEffectEvent, useState } from "react";
import { publicApiUrl } from "./config";
import { hasGap, mergeBalance, mergeHistory } from "./live";
import { formatMoney } from "./money";
import { useSession } from "./session";
import type { LiveWalletEntry, StatementPage, WalletBalance } from "./types";
import { queryKeys } from "./wallet-api";

export type LiveState = "connecting" | "live" | "reconnecting" | "offline";

/**
 * Keeps one SignalR connection to /hubs/wallet (through the gateway) while signed in, and folds every
 * pushed entry into the TanStack Query cache: the balance, the top of the history, and a refetch of
 * the authoritative wallet view (held and available). After a reconnect, or when a push shows that
 * entries were missed, it refetches instead of guessing.
 */
export function useWalletHub(onEntry?: (entry: LiveWalletEntry) => void): LiveState {
  const { status, getAccessToken } = useSession();
  const queryClient = useQueryClient();
  const [state, setState] = useState<LiveState>("connecting");
  const notify = useEffectEvent((live: LiveWalletEntry) => onEntry?.(live));

  useEffect(() => {
    if (status !== "signedIn") return;

    const connection: HubConnection = new HubConnectionBuilder()
      .withUrl(`${publicApiUrl}/hubs/wallet`, { accessTokenFactory: async () => (await getAccessToken()) ?? "" })
      .withAutomaticReconnect([0, 2_000, 5_000, 10_000, 20_000, 30_000])
      .configureLogging(LogLevel.Warning)
      .build();

    const resync = () => queryClient.invalidateQueries({ queryKey: queryKeys.wallet });

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

/** "Received Rs. 500.00 from +92300*****67" for an incoming push; null for money out (the sender already knows). */
export function describeIncoming(live: LiveWalletEntry): string | null {
  if (live.entry.direction !== "In") return null;
  return `${live.entry.title} · ${formatMoney(live.entry.amount)}`;
}
