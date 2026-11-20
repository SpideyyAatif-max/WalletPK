import { api, newIdempotencyKey } from "./api";
import type { TimelineStep } from "./payments";
import type { WalletView } from "./types";

// Shapes of the Agent Network service's JSON (M9) and the calls the wallet makes.

export type AgentStatus = "PendingApproval" | "Opening" | "Active" | "Suspended" | "Rejected";

/** Agents: AgentView. */
export type Agent = {
  id: string;
  code: string;
  shopName: string;
  city: string;
  area: string | null;
  status: AgentStatus;
  statusReason: string | null;
  lowFloatPaisa: number;
  unpaidTransactions: number;
  unpaidCommissionPaisa: number;
  createdAt: string;
};

/** An agent as customers see it. */
export type AgentListing = { code: string; shopName: string; city: string; area: string | null };

export type AgentTransactionKind = "CashIn" | "CashOut" | "FloatIn" | "FloatOut";

/** Agents: AgentTransactionView. */
export type AgentTransaction = {
  id: string;
  kind: AgentTransactionKind;
  status: "Processing" | "Completed" | "Failed" | "UnderReview";
  step: string;
  agentCode: string;
  agentName: string;
  customerWalletNumber: string | null;
  customerName: string | null;
  amountPaisa: number;
  feePaisa: number;
  taxPaisa: number;
  commissionPaisa: number;
  agentBalancePaisa: number | null;
  failureCode: string | null;
  failureReason: string | null;
  requestedAt: string;
  completedAt: string | null;
  timeline: TimelineStep[] | null;
};

export type CashOutStatus = "Open" | "Processing" | "Completed" | "Failed" | "Expired" | "Cancelled" | "Locked";

/** Agents: CashOutRequestView (code only in the answer that created it). */
export type CashOutRequest = {
  id: string;
  code: string | null;
  status: CashOutStatus;
  agentCode: string;
  agentName: string;
  amountPaisa: number;
  feePaisa: number;
  taxPaisa: number;
  totalPaisa: number;
  expiresAt: string;
  transactionId: string | null;
  failureReason: string | null;
};

export type AgentQuote = { amountPaisa: number; feePaisa: number; taxPaisa: number; commissionPaisa: number; totalPaisa: number };

export type Payout = {
  id: string;
  businessDate: string;
  transactionCount: number;
  grossPaisa: number;
  taxPaisa: number;
  netPaisa: number;
  status: "Pending" | "Paid" | "Failed";
  failureReason: string | null;
  paidAt: string | null;
};

export const agentsApi = {
  find: (city: string) => api<AgentListing[]>(`/api/agents/find?city=${encodeURIComponent(city)}`),
  byCode: (code: string) => api<AgentListing>(`/api/agents/by-code/${encodeURIComponent(code)}`),
  quote: (kind: "CashIn" | "CashOut", amountPaisa: number) => api<AgentQuote>(`/api/agents/quote?kind=${kind}&amountPaisa=${amountPaisa}`),
  apply: (body: { shopName: string; city: string; area?: string }) => api<Agent>("/api/agents", { method: "POST", body }),
  me: () => api<Agent>("/api/agents/me"),
  float: () => api<WalletView>("/api/wallets/me/agent"),
  setLowFloat: (lowFloatPaisa: number) => api<Agent>("/api/agents/me/low-float", { method: "PUT", body: { lowFloatPaisa } }),
  cashIn: (customerNumber: string, amountPaisa: number, idempotencyKey: string = newIdempotencyKey()) =>
    api<AgentTransaction>("/api/agents/me/cash-ins", { method: "POST", body: { customerNumber, amountPaisa }, idempotencyKey }),
  takeCashOut: (customerNumber: string, code: string, idempotencyKey: string = newIdempotencyKey()) =>
    api<AgentTransaction>("/api/agents/me/cash-outs", { method: "POST", body: { customerNumber, code }, idempotencyKey }),
  moveFloat: (direction: "ToFloat" | "ToWallet", amountPaisa: number, idempotencyKey: string = newIdempotencyKey()) =>
    api<AgentTransaction>("/api/agents/me/float", { method: "POST", body: { direction, amountPaisa }, idempotencyKey }),
  agentTransactions: (limit = 20) => api<AgentTransaction[]>(`/api/agents/me/transactions?limit=${limit}`),
  payouts: (limit = 14) => api<Payout[]>(`/api/agents/me/payouts?limit=${limit}`),
  transaction: (id: string) => api<AgentTransaction>(`/api/agents/transactions/${id}`),
  myTransactions: (limit = 10) => api<AgentTransaction[]>(`/api/agents/transactions?limit=${limit}`),
  requestCashOut: (agentCode: string, amountPaisa: number) => api<CashOutRequest>("/api/agents/cash-outs", { method: "POST", body: { agentCode, amountPaisa } }),
  currentCashOut: () => api<CashOutRequest>("/api/agents/cash-outs/current"),
  cancelCashOut: (id: string) => api<CashOutRequest>(`/api/agents/cash-outs/${id}/cancel`, { method: "POST" }),
};

export const agentKeys = {
  me: ["agents", "me"] as const,
  float: ["agents", "float"] as const,
  transactions: ["agents", "transactions"] as const,
  payouts: ["agents", "payouts"] as const,
  transaction: (id: string) => ["agents", "transaction", id] as const,
  mine: ["agents", "mine"] as const,
  find: (city: string) => ["agents", "find", city] as const,
  cashOut: ["agents", "cash-out"] as const,
  quote: (kind: string, amountPaisa: number) => ["agents", "quote", kind, amountPaisa] as const,
};

/** Cash-in and cash-out: Rs. 100 to Rs. 50,000, whole rupees. */
export const cashLimits = { minPaisa: 10_000, maxPaisa: 5_000_000 } as const;

/** Why a cash amount is refused, or null when it is fine. */
export function cashProblem(paisa: number | null): string | null {
  if (paisa === null) return "Enter an amount.";
  if (paisa % 100 !== 0) return "Cash is whole rupees.";
  if (paisa < cashLimits.minPaisa || paisa > cashLimits.maxPaisa) return "Rs. 100 to Rs. 50,000.";
  return null;
}

/** The same commission plan as the server (half up, to whole paisa), for previews. */
export function quoteCashOut(amountPaisa: number): AgentQuote {
  const round = (numerator: number) => Math.floor((numerator + 5_000) / 10_000);
  const fee = Math.min(Math.max(round(amountPaisa * 100), 1_000), 30_000);
  const tax = round(fee * 1_600);
  return { amountPaisa, feePaisa: fee, taxPaisa: tax, commissionPaisa: round(fee * 6_000), totalPaisa: amountPaisa + fee + tax };
}

export function quoteCashIn(amountPaisa: number): AgentQuote {
  const commission = Math.min(Math.max(Math.floor((amountPaisa * 40 + 5_000) / 10_000), 200), 4_000);
  return { amountPaisa, feePaisa: 0, taxPaisa: 0, commissionPaisa: commission, totalPaisa: amountPaisa };
}

/** A request the customer still has to show, or wait on. */
export function isCashOutLive(status: CashOutStatus): boolean {
  return status === "Open" || status === "Processing";
}

/** "WPKA123456" from what the customer typed or scanned. */
export function normaliseAgentCode(input: string): string | null {
  const code = input.trim().toUpperCase().replace(/\s+/g, "");
  return /^WPKA\d{6}$/.test(code) ? code : null;
}

/** "123 456": easier to read out across a counter. */
export function spacedCode(code: string): string {
  return code.length === 6 ? `${code.slice(0, 3)} ${code.slice(3)}` : code;
}

export const kindNames: Record<AgentTransactionKind, string> = {
  CashIn: "Cash-in",
  CashOut: "Cash-out",
  FloatIn: "Float loaded",
  FloatOut: "Float to wallet",
};
