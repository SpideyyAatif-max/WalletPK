import { api, newIdempotencyKey } from "./api";

// Shapes of the Payments service's JSON (M6) and the calls the wallet makes.

export type TimelineStep = { sequence: number; state: string; detail: string; at: string };

/** Payments: BankAvailability (GET /api/payments/banks). */
export type Bank = { code: string; name: string; circuit: string; status: "Available" | "Degraded" | "Down"; since: string | null };

/** Payments: Beneficiary (POST /api/payments/beneficiaries/lookup). */
export type Beneficiary = { iban: string; maskedIban: string; bank: string; bankName: string; title: string; accountStatus: string; canReceive: boolean };

/** Payments: BankTransferView. */
export type BankTransfer = {
  id: string;
  status: "Processing" | "Queued" | "Completed" | "Failed";
  step: string;
  amountPaisa: number;
  toIban: string;
  toBank: string;
  toBankName: string;
  beneficiaryTitle: string | null;
  note: string | null;
  raastReference: string | null;
  failureCode: string | null;
  failureReason: string | null;
  requestedAt: string;
  updatedAt: string;
  completedAt: string | null;
  timeline: TimelineStep[] | null;
};

/** Payments: P2PTransferView. */
export type P2PTransfer = {
  id: string;
  status: "Processing" | "Completed" | "Failed";
  step: string;
  toWalletNumber: string;
  amountPaisa: number;
  feePaisa: number;
  taxPaisa: number;
  note: string | null;
  failureCode: string | null;
  failureReason: string | null;
  requestedAt: string;
  updatedAt: string;
  completedAt: string | null;
  timeline: TimelineStep[] | null;
};

export type RequestStatus = "Pending" | "Paying" | "Paid" | "Declined" | "Cancelled" | "Expired" | "NeedsReview";

/** Payments: MoneyRequestView. */
export type MoneyRequest = {
  id: string;
  status: RequestStatus;
  direction: "incoming" | "outgoing";
  requesterWalletNumber: string;
  payerWalletNumber: string;
  amountPaisa: number;
  note: string | null;
  expiresAt: string;
  splitId: string | null;
  splitTitle: string | null;
  lastFailureCode: string | null;
  lastFailureReason: string | null;
  declineReason: string | null;
  feePaisa: number;
  taxPaisa: number;
  requestedAt: string;
  updatedAt: string;
  completedAt: string | null;
  timeline: TimelineStep[] | null;
};

/** Payments: SplitView. */
export type Split = {
  id: string;
  title: string;
  totalPaisa: number;
  ownSharePaisa: number;
  collectedPaisa: number;
  outstandingPaisa: number;
  paidCount: number;
  shares: { requestId: string; walletNumber: string; amountPaisa: number; status: RequestStatus; paidAt: string | null }[];
  createdAt: string;
};

export const paymentsApi = {
  banks: () => api<Bank[]>("/api/payments/banks"),
  lookup: (target: { iban?: string; raastId?: string }) =>
    api<Beneficiary>("/api/payments/beneficiaries/lookup", { method: "POST", body: target }),
  sendToBank: (body: { toIban: string; amountPaisa: number; beneficiaryTitle?: string; note?: string }, idempotencyKey: string) =>
    api<BankTransfer>("/api/payments/bank-transfers", { method: "POST", body, idempotencyKey }),
  bankTransfer: (id: string) => api<BankTransfer>(`/api/payments/bank-transfers/${id}`),
  bankTransfers: (limit = 10) => api<BankTransfer[]>(`/api/payments/bank-transfers?limit=${limit}`),

  send: (body: { toWalletNumber: string; amountPaisa: number; note?: string }, idempotencyKey: string) =>
    api<P2PTransfer>("/api/payments/p2p-transfers", { method: "POST", body, idempotencyKey }),
  p2pTransfer: (id: string) => api<P2PTransfer>(`/api/payments/p2p-transfers/${id}`),

  requests: (direction: "incoming" | "outgoing") => api<MoneyRequest[]>(`/api/payments/requests?direction=${direction}&limit=30`),
  request: (id: string) => api<MoneyRequest>(`/api/payments/requests/${id}`),
  ask: (body: { fromWalletNumber: string; amountPaisa: number; note?: string }) =>
    api<MoneyRequest>("/api/payments/requests", { method: "POST", body, idempotencyKey: newIdempotencyKey() }),
  pay: (id: string) => api<MoneyRequest>(`/api/payments/requests/${id}/pay`, { method: "POST" }),
  decline: (id: string, reason?: string) => api<MoneyRequest>(`/api/payments/requests/${id}/decline`, { method: "POST", body: { reason } }),
  cancel: (id: string) => api<MoneyRequest>(`/api/payments/requests/${id}/cancel`, { method: "POST" }),

  splits: () => api<Split[]>("/api/payments/splits"),
  split: (body: { title: string; totalPaisa: number; participants: { walletNumber: string }[]; includeMe: boolean }) =>
    api<Split>("/api/payments/splits", { method: "POST", body, idempotencyKey: newIdempotencyKey() }),
};

export const paymentKeys = {
  banks: ["payments", "banks"] as const,
  bankTransfers: ["payments", "bank-transfers"] as const,
  bankTransfer: (id: string) => ["payments", "bank-transfers", id] as const,
  p2pTransfer: (id: string) => ["payments", "p2p", id] as const,
  requests: (direction: "incoming" | "outgoing") => ["payments", "requests", direction] as const,
  splits: ["payments", "splits"] as const,
};

/** A transfer or request that will not change any more (stop polling). */
export function isFinal(status: string): boolean {
  return ["Completed", "Failed", "Paid", "Declined", "Cancelled", "Expired"].includes(status);
}

/** The steps a bank transfer goes through, for the tracker; the ones it skips are left out. */
export const bankTransferSteps = ["Reserving", "Sending", "AwaitingBank", "Settling", "Completed"] as const;

const stepNames: Record<string, string> = {
  Reserving: "Reserving the money",
  Sending: "Sending to the bank",
  Queued: "Queued: the bank is unavailable",
  Verifying: "Checking with Raast",
  AwaitingBank: "Waiting for the bank",
  Settling: "Bank credited the account",
  Returning: "Returning the money",
  NeedsReview: "Being checked by our team",
  Completed: "Delivered",
  Failed: "Not sent",
  Pending: "Waiting for payment",
  Paying: "Paying",
  Retrying: "Biller unavailable: retrying",
  PendingVerification: "Waiting for the biller to confirm",
  DeadLettered: "Being checked by our team",
  Paid: "Paid",
  Declined: "Declined",
  Cancelled: "Cancelled",
  Expired: "Expired",
};

export function stepName(state: string): string {
  return stepNames[state] ?? state;
}

/** Equal shares of a bill, as the server splits it: the odd paisa go to the first people. */
export function equalShares(totalPaisa: number, people: number, includeMe: boolean): { each: number[]; mine: number } {
  const parts = people + (includeMe ? 1 : 0);
  if (people < 1 || parts < 1) return { each: [], mine: 0 };
  const base = Math.floor(totalPaisa / parts);
  const left = totalPaisa - base * parts;
  const each = Array.from({ length: people }, (_, index) => base + (index < left ? 1 : 0));
  return { each, mine: totalPaisa - each.reduce((sum, share) => sum + share, 0) };
}
