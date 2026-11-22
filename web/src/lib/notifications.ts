import type { InfiniteData, QueryKey } from "@tanstack/react-query";
import { agentKeys } from "./agents";
import { api } from "./api";
import { billKeys } from "./bills";
import { merchantKeys } from "./merchants";
import { paymentKeys } from "./payments";
import { topUpKeys } from "./topups";

// ---- Notifications service (M11): the notification centre, settings and live pushes ----

/** Notifications: NotificationView, in the user's language. */
export type AppNotification = {
  id: string;
  template: string;
  category: string;
  title: string;
  body: string;
  createdAt: string;
  read: boolean;
};

export type NotificationPage = { items: AppNotification[]; unreadCount: number; nextBefore: string | null };

/** Pushed on /hubs/wallet as "NotificationReceived". */
export type LiveNotification = { notification: AppNotification; unreadCount: number };

/** Pushed on /hubs/wallet as "PaymentChanged": one of the user's payments moved on. */
export type PaymentUpdate = { kind: string; id: string; status: string; step: string | null };

export type Language = "en" | "ur";

export type NotificationSettings = { language: Language; smsEnabled: boolean; emailEnabled: boolean; email: string | null; phoneNumber: string | null };

/** Development only: an SMS or email the service sent. */
export type SentMessage = { id: string; channel: "Sms" | "Email"; to: string; subject: string | null; body: string; status: string; attempts: number; createdAt: string; sentAt: string | null };

export const notificationKeys = {
  all: ["notifications"] as const,
  list: ["notifications", "list"] as const,
  unread: ["notifications", "unread"] as const,
  settings: ["notifications", "settings"] as const,
  sent: ["notifications", "sent"] as const,
};

export const notificationsApi = {
  page: (before: string | null, limit = 20) => {
    const query = new URLSearchParams({ limit: String(limit) });
    if (before) query.set("before", before);
    return api<NotificationPage>(`/api/notifications?${query}`);
  },
  unreadCount: async () => (await api<{ count: number }>("/api/notifications/unread-count")).count,
  markRead: async (id: string) => (await api<{ count: number }>(`/api/notifications/${id}/read`, { method: "POST" })).count,
  readAll: () => api<{ count: number }>("/api/notifications/read-all", { method: "POST" }),
  settings: () => api<NotificationSettings>("/api/notifications/settings"),
  updateSettings: (changes: Partial<Pick<NotificationSettings, "language" | "smsEnabled" | "emailEnabled">> & { email?: string }) =>
    api<NotificationSettings>("/api/notifications/settings", { method: "PUT", body: changes }),
  sent: () => api<SentMessage[]>("/api/notifications/dev/messages"),
};

/** Adds a pushed notification to the top of the cached list (once), and sets the unread count on every page. */
export function mergeNotification(
  current: InfiniteData<NotificationPage, string | null> | undefined,
  live: LiveNotification,
): InfiniteData<NotificationPage, string | null> | undefined {
  if (!current || current.pages.length === 0) return current;
  const known = current.pages.some((page) => page.items.some((item) => item.id === live.notification.id));
  const pages = current.pages.map((page, index) => ({
    ...page,
    unreadCount: live.unreadCount,
    items: index === 0 && !known ? [live.notification, ...page.items] : page.items,
  }));
  return { ...current, pages };
}

/** Marks one notification read in the cached list. */
export function markReadInPages(
  current: InfiniteData<NotificationPage, string | null> | undefined,
  id: string,
  unreadCount: number,
): InfiniteData<NotificationPage, string | null> | undefined {
  if (!current) return current;
  return {
    ...current,
    pages: current.pages.map((page) => ({
      ...page,
      unreadCount,
      items: page.items.map((item) => (item.id === id ? { ...item, read: true } : item)),
    })),
  };
}

/** The cached queries a payment update makes stale (its own screen and the lists it is in). */
export function queriesFor(update: PaymentUpdate): QueryKey[] {
  switch (update.kind) {
    case "p2p":
      return [paymentKeys.p2pTransfer(update.id)];
    case "bank":
      return [paymentKeys.bankTransfer(update.id), paymentKeys.bankTransfers];
    case "request":
      return [["payments", "requests"], paymentKeys.splits];
    case "bill":
      return [billKeys.payment(update.id), billKeys.payments];
    case "topup":
      return [topUpKeys.topUp(update.id), topUpKeys.list];
    case "merchant":
      return [merchantKeys.payment(update.id), merchantKeys.payments];
    case "agent":
      return [agentKeys.transaction(update.id), agentKeys.transactions, agentKeys.float, agentKeys.cashOut];
    default:
      return [];
  }
}

/** Notifications not worth a pop-up: the user just did these themselves. */
const quiet = new Set(["money_sent", "wallet_debited"]);

export function worthAToast(notification: AppNotification): boolean {
  return !quiet.has(notification.template);
}
