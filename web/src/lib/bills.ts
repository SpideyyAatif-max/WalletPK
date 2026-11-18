import { api, newIdempotencyKey } from "./api";
import type { TimelineStep } from "./payments";

// Shapes of the Bill Payment service's JSON (M7) and the calls the wallet makes.

/** Bills: BillerAvailability (GET /api/bills/billers). */
export type Biller = {
  code: string;
  name: string;
  category: string;
  consumerNumberLength: number;
  consumerNumberLabel: string;
  status: "Available" | "Degraded" | "Down";
  since: string | null;
};

/** Bills: BillInfo (POST /api/bills/inquiry). */
export type Bill = {
  biller: string;
  consumerNumber: string;
  customerName: string;
  billingMonth: string;
  dueDate: string;
  amountWithinDuePaisa: number;
  amountAfterDuePaisa: number;
  payablePaisa: number;
  status: "Unpaid" | "Paid";
};

export type BillPaymentStatus = "Processing" | "PendingVerification" | "UnderReview" | "Completed" | "Failed";

/** Bills: BillPaymentView. */
export type BillPayment = {
  id: string;
  status: BillPaymentStatus;
  step: string;
  biller: string;
  billerName: string;
  consumerNumber: string;
  customerName: string | null;
  amountPaisa: number;
  billingMonth: string | null;
  billerReference: string | null;
  failureCode: string | null;
  failureReason: string | null;
  requestedAt: string;
  updatedAt: string;
  completedAt: string | null;
  timeline: TimelineStep[] | null;
};

/** Bills: SavedBillerView. */
export type SavedBiller = {
  id: string;
  biller: string;
  billerName: string;
  category: string;
  consumerNumber: string;
  nickname: string | null;
  customerName: string | null;
  lastBillingMonth: string | null;
  lastDueDate: string | null;
  lastPayablePaisa: number | null;
  lastStatus: string | null;
  lastCheckedAt: string | null;
};

export const billsApi = {
  billers: () => api<Biller[]>("/api/bills/billers"),
  inquire: (biller: string, consumerNumber: string) => api<Bill>("/api/bills/inquiry", { method: "POST", body: { biller, consumerNumber } }),
  pay: (body: { biller: string; consumerNumber: string; amountPaisa: number; customerName?: string }, idempotencyKey: string = newIdempotencyKey()) =>
    api<BillPayment>("/api/bills/payments", { method: "POST", body, idempotencyKey }),
  payment: (id: string) => api<BillPayment>(`/api/bills/payments/${id}`),
  payments: (limit = 10) => api<BillPayment[]>(`/api/bills/payments?limit=${limit}`),
  saved: () => api<SavedBiller[]>("/api/bills/saved"),
  save: (body: { biller: string; consumerNumber: string; nickname?: string }) => api<SavedBiller>("/api/bills/saved", { method: "POST", body }),
  unsave: (id: string) => api<void>(`/api/bills/saved/${id}`, { method: "DELETE" }),
};

export const billKeys = {
  billers: ["bills", "billers"] as const,
  payments: ["bills", "payments"] as const,
  payment: (id: string) => ["bills", "payments", id] as const,
  saved: ["bills", "saved"] as const,
};

/** A bill payment that will not change any more. */
export function isBillFinal(status: string): boolean {
  return status === "Completed" || status === "Failed";
}

/** Digits only, as the biller's number is checked (spaces and dashes are fine to type). */
export function cleanConsumerNumber(input: string): string {
  return input.replace(/[\s-]/g, "");
}

/** Whether the number has the biller's format (the server checks again). */
export function isConsumerNumber(biller: Pick<Biller, "consumerNumberLength">, input: string): boolean {
  const digits = cleanConsumerNumber(input);
  return digits.length === biller.consumerNumberLength && /^\d+$/.test(digits);
}

/** Days from `today` (a "YYYY-MM-DD" Pakistan date) to the due date; negative when overdue. */
export function daysUntil(dueDate: string, today: string): number {
  return Math.round((Date.parse(`${dueDate}T00:00:00Z`) - Date.parse(`${today}T00:00:00Z`)) / 86_400_000);
}

/** "Due in 3 days", "Due today", "2 days overdue". */
export function dueLabel(dueDate: string, today: string): string {
  const days = daysUntil(dueDate, today);
  if (days === 0) return "Due today";
  if (days === 1) return "Due tomorrow";
  if (days > 1) return `Due in ${days} days`;
  return days === -1 ? "1 day overdue" : `${-days} days overdue`;
}
