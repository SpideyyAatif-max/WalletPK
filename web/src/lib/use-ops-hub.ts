"use client";

import { HubConnectionBuilder, LogLevel, type HubConnection } from "@microsoft/signalr";
import { useEffect, useState } from "react";
import { publicApiUrl } from "./config";
import type { LiveState } from "./use-wallet-hub";
import { useSession } from "./session";

/** Notifications: OpsEvent, pushed on /hubs/ops as "Event" (staff only). */
export type OpsEvent = {
  id: string;
  kind: string;
  severity: "Info" | "Low" | "Medium" | "High" | "Critical" | string;
  title: string;
  detail: string | null;
  userId: string | null;
  at: string;
};

/** Newest first, without duplicates (a reconnect can deliver an event twice), at most `keep`. */
export function addEvent(events: OpsEvent[], event: OpsEvent, keep = 100): OpsEvent[] {
  if (events.some((existing) => existing.id === event.id)) return events;
  return [event, ...events].slice(0, keep);
}

/**
 * The operations feed (M11, /hubs/ops through the gateway): fraud alerts, freezes, cases, stuck payments and
 * agents low on float, as they happen. Kept in memory for this tab only.
 */
export function useOpsHub(): { state: LiveState; events: OpsEvent[] } {
  const { status, getAccessToken } = useSession();
  const [state, setState] = useState<LiveState>("connecting");
  const [events, setEvents] = useState<OpsEvent[]>([]);

  useEffect(() => {
    if (status !== "signedIn") return;
    const connection: HubConnection = new HubConnectionBuilder()
      .withUrl(`${publicApiUrl}/hubs/ops`, { accessTokenFactory: async () => (await getAccessToken()) ?? "" })
      .withAutomaticReconnect([0, 2_000, 5_000, 10_000, 20_000, 30_000])
      .configureLogging(LogLevel.Warning)
      .build();

    connection.on("Event", (event: OpsEvent) => setEvents((current) => addEvent(current, event)));
    connection.onreconnecting(() => setState("reconnecting"));
    connection.onreconnected(() => setState("live"));
    connection.onclose(() => setState("offline"));

    let stopped = false;
    connection
      .start()
      .then(() => {
        if (!stopped) setState("live");
      })
      .catch(() => {
        if (!stopped) setState("offline");
      });

    return () => {
      stopped = true;
      void connection.stop();
    };
  }, [status, getAccessToken]);

  return { state, events };
}
