import { api } from "./api";
import type { components as BillsApi } from "./api-types/bills";
import type { components as FraudApi } from "./api-types/fraud";
import type { components as LedgerApi } from "./api-types/ledger";
import type { components as PaymentsApi } from "./api-types/payments";
import type { components as TopupsApi } from "./api-types/topups";
import { jaegerUrl } from "./config";

// The operations portal's calls. Types come from the services' OpenAPI documents (src/lib/api-types).

export type SagaSummary = PaymentsApi["schemas"]["SagaSummary"];
export type SagaDetail = PaymentsApi["schemas"]["SagaDetail"];
export type SagaTimelineStep = PaymentsApi["schemas"]["SagaTimelineStep"];
export type SagaStateCount = PaymentsApi["schemas"]["SagaStateCount"];
export type BillDeadLetter = BillsApi["schemas"]["DeadLetterView"];
export type TopUpDeadLetter = TopupsApi["schemas"]["TopUpDeadLetterView"];
export type FraudAlert = FraudApi["schemas"]["FraudAlert"];
export type FraudCase = FraudApi["schemas"]["FraudCase"];
export type CaseView = FraudApi["schemas"]["CaseView"];
export type FraudRule = FraudApi["schemas"]["RuleView"];
export type InvariantReport = LedgerApi["schemas"]["InvariantReport"];

/** Services that run Sagas, by their gateway prefix. */
export const sagaServices = [
  { id: "payments", name: "Payments", types: ["bank-transfer", "p2p-transfer", "money-request"] },
  { id: "bills", name: "Bills", types: ["bill-payment"] },
  { id: "topups", name: "Top-up & merchant", types: ["topup", "merchant-payment"] },
  { id: "agents", name: "Agents", types: ["agent-transaction"] },
] as const;

export type SagaService = (typeof sagaServices)[number]["id"];

export type SagaFilter = {
  type?: string;
  state?: string;
  active?: boolean;
  stuckForMinutes?: number;
  userId?: string;
  correlationId?: string;
  before?: string;
  limit?: number;
};

/** The query string for a Saga search: only the filters that are set. */
export function sagaQuery(filter: SagaFilter): string {
  const params = new URLSearchParams();
  for (const [key, value] of Object.entries(filter)) {
    if (value === undefined || value === null || value === "") continue;
    params.set(key, String(value));
  }
  const text = params.toString();
  return text ? `?${text}` : "";
}

/** A Saga step's trace in Jaeger. */
export const traceLink = (traceId: string): string => `${jaegerUrl}/trace/${traceId}`;

export const opsApi = {
  sagas: (service: SagaService, filter: SagaFilter) => api<SagaSummary[]>(`/api/${service}/ops/sagas${sagaQuery(filter)}`),
  saga: (service: SagaService, id: string) => api<SagaDetail>(`/api/${service}/ops/sagas/${id}`),
  sagaCounts: (service: SagaService, finishedWithinHours = 24) =>
    api<SagaStateCount[]>(`/api/${service}/ops/sagas/counts?finishedWithinHours=${finishedWithinHours}`),

  billDeadLetters: () => api<BillDeadLetter[]>("/api/bills/ops/dead-letters?limit=100"),
  replayBill: (id: string) => api<unknown>(`/api/bills/ops/payments/${id}/replay`, { method: "POST" }),
  refundBill: (id: string, reason: string) => api<unknown>(`/api/bills/ops/payments/${id}/refund`, { method: "POST", body: { reason } }),
  confirmBill: (id: string, billerReference: string) =>
    api<unknown>(`/api/bills/ops/payments/${id}/confirm`, { method: "POST", body: { billerReference } }),

  topUpDeadLetters: () => api<TopUpDeadLetter[]>("/api/topups/ops/dead-letters?limit=100"),
  replayTopUp: (id: string) => api<unknown>(`/api/topups/ops/${id}/replay`, { method: "POST" }),
  refundTopUp: (id: string, reason: string) => api<unknown>(`/api/topups/ops/${id}/refund`, { method: "POST", body: { reason } }),
  confirmTopUp: (id: string, telcoReference: string) =>
    api<unknown>(`/api/topups/ops/${id}/confirm`, { method: "POST", body: { telcoReference } }),

  invariants: () => api<InvariantReport>("/api/ledger/invariants"),

  alerts: (filter: { userId?: string; severity?: string } = {}) => api<FraudAlert[]>(`/api/fraud/alerts${sagaQuery({ ...filter, limit: 100 })}`),
  account: (userId: string) => api<FraudAccount>(`/api/fraud/accounts/${userId}`),
  freeze: (userId: string, reason: string) => api<unknown>(`/api/fraud/accounts/${userId}/freeze`, { method: "POST", body: { reason } }),
  unfreeze: (userId: string, reason: string) => api<unknown>(`/api/fraud/accounts/${userId}/unfreeze`, { method: "POST", body: { reason } }),
  cases: (status?: string) => api<FraudCase[]>(`/api/fraud/cases${sagaQuery({ status, limit: 100 } as SagaFilter)}`),
  case: (id: string) => api<CaseView>(`/api/fraud/cases/${id}`),
  openCase: (userId: string, title: string) => api<CaseView>("/api/fraud/cases", { method: "POST", body: { userId, title } }),
  assignCase: (id: string) => api<CaseView>(`/api/fraud/cases/${id}/assign`, { method: "POST" }),
  noteCase: (id: string, text: string) => api<CaseView>(`/api/fraud/cases/${id}/notes`, { method: "POST", body: { text } }),
  closeCase: (id: string, body: { resolution: "ConfirmedFraud" | "FalsePositive"; unfreeze: boolean; note?: string }) =>
    api<CaseView>(`/api/fraud/cases/${id}/close`, { method: "POST", body }),
  rules: () => api<FraudRule[]>("/api/fraud/rules"),
  updateRule: (id: string, body: { name?: string; description?: string; definition?: unknown; enabled?: boolean }) =>
    api<FraudRule>(`/api/fraud/rules/${id}`, { method: "PUT", body }),
  testRule: (body: { rule?: unknown; features: Record<string, unknown> }) => api<RuleTestResult>("/api/fraud/rules/test", { method: "POST", body }),
  features: () => api<{ name: string; kind: string; description: string }[]>("/api/fraud/features"),
};

/** GET /api/fraud/accounts/{userId} (an anonymous object on the server). */
export type FraudAccount = {
  userId: string;
  frozen: boolean;
  reason: string | null;
  changedBy: string | null;
  changedAt: string | null;
  ledgerConfirmed: boolean;
  alerts: FraudAlert[];
};

/** POST /api/fraud/rules/test. */
export type RuleTestResult = { score: number; action: string; rules: string[]; reason: string };

export const opsKeys = {
  sagas: (service: SagaService, filter: SagaFilter) => ["ops", "sagas", service, filter] as const,
  saga: (service: SagaService, id: string) => ["ops", "saga", service, id] as const,
  sagaCounts: (service: SagaService) => ["ops", "saga-counts", service] as const,
  billDeadLetters: ["ops", "dead-letters", "bills"] as const,
  topUpDeadLetters: ["ops", "dead-letters", "topups"] as const,
  invariants: ["ops", "invariants"] as const,
  alerts: (filter: object) => ["ops", "fraud", "alerts", filter] as const,
  account: (userId: string) => ["ops", "fraud", "account", userId] as const,
  cases: (status: string) => ["ops", "fraud", "cases", status] as const,
  case: (id: string) => ["ops", "fraud", "case", id] as const,
  rules: ["ops", "fraud", "rules"] as const,
};

/** Sums Saga counts across services into what the dashboard shows: running, stuck-looking, failed today. */
export function summarizeCounts(counts: SagaStateCount[]): { running: number; deadLettered: number; finished: number; failed: number } {
  let running = 0;
  let deadLettered = 0;
  let finished = 0;
  let failed = 0;
  for (const count of counts) {
    if (!count.isCompleted) running += count.count;
    if (count.state === "DeadLettered" || count.state === "NeedsReview") deadLettered += count.count;
    if (count.isCompleted) finished += count.count;
    if (count.isCompleted && count.state === "Failed") failed += count.count;
  }
  return { running, deadLettered, finished, failed };
}
