import { api, downloadFile } from "./api";
import type { components as FraudApi } from "./api-types/fraud";
import type { components as LedgerApi } from "./api-types/ledger";
import type { components as ReconApi } from "./api-types/reconciliation";
import { dayKey } from "./dates";

// Reconciliation, reports, statements and the audit viewer (M12). Types come from the services' OpenAPI documents.

export type ReconRun = ReconApi["schemas"]["ReconRun"];
export type ReconBreak = ReconApi["schemas"]["ReconBreak"];
export type RunDetail = ReconApi["schemas"]["RunDetail"];
export type BreakDetail = ReconApi["schemas"]["BreakDetail"];
export type DaySummary = ReconApi["schemas"]["DaySummary"];
export type DayOverview = ReconApi["schemas"]["DayOverview"];
export type ReconAuditEntry = ReconApi["schemas"]["AuditEntry"];
export type AuditVerification = ReconApi["schemas"]["AuditVerification"];
export type DailyReport = LedgerApi["schemas"]["DailyReport"];
export type DayTotals = LedgerApi["schemas"]["DayTotals"];
export type LedgerAuditRow = LedgerApi["schemas"]["AuditRow"];
export type LedgerAuditReport = LedgerApi["schemas"]["AuditChainReport"];
export type FraudAuditEntry = FraudApi["schemas"]["AuditEntry"];
export type MonthlyStatement = LedgerApi["schemas"]["MonthlyStatement"];

export const breakKinds = {
  MissingAtProvider: { label: "Missing at provider", hint: "Completed here, not in the provider's file" },
  UnknownToUs: { label: "Unknown to us", hint: "In the file, but WalletPK never sent it" },
  Duplicate: { label: "Duplicate", hint: "Listed more than once" },
  AmountMismatch: { label: "Amount mismatch", hint: "The file's amount differs from ours" },
  SettledButFailedHere: { label: "Settled, failed here", hint: "The provider paid out; the customer was refunded" },
  SettledButPendingHere: { label: "Settled, pending here", hint: "The provider paid out; still in flight here" },
} as const;

export type BreakKind = keyof typeof breakKinds;

export const resolutions = [
  { id: "Adjusted", label: "Adjusted", hint: "A correcting ledger entry was posted" },
  { id: "Recovered", label: "Recovered", hint: "The provider paid, refunded or corrected it" },
  { id: "WrittenOff", label: "Written off", hint: "Accepted as a loss" },
  { id: "NoActionNeeded", label: "No action needed", hint: "Explained, e.g. a timing difference" },
] as const;

export const breakKindLabel = (kind: string): string => breakKinds[kind as BreakKind]?.label ?? kind;

/** "bank:HBL" → "HBL (bank)". */
export function partyLabel(party: string): string {
  const [kind = "", code] = party.split(":");
  if (!code) return party;
  const kinds: Record<string, string> = { bank: "bank", biller: "biller", telco: "network" };
  return `${code} (${kinds[kind] ?? kind})`;
}

/** Pakistan's calendar day `days` before `now` ("2026-11-25"). */
export function pakistanDay(days = 0, now: Date = new Date()): string {
  return dayKey(new Date(now.getTime() - days * 86_400_000).toISOString());
}

/** The last `count` calendar months in Pakistan, newest first: { id: "2026-11", label: "November 2026" }. */
export function recentMonths(count = 12, now: Date = new Date()): { id: string; label: string }[] {
  const [year = 1970, month = 1] = pakistanDay(0, now).split("-").map(Number);
  return Array.from({ length: count }, (_, index) => {
    const date = new Date(Date.UTC(year, month - 1 - index, 1));
    const id = `${date.getUTCFullYear()}-${String(date.getUTCMonth() + 1).padStart(2, "0")}`;
    const label = new Intl.DateTimeFormat("en-GB", { month: "long", year: "numeric", timeZone: "UTC" }).format(date);
    return { id, label };
  });
}

/** Groups break counts by kind, in the order the kinds are listed. */
export function countByKind(breaks: Pick<ReconBreak, "kind">[]): { kind: BreakKind; count: number }[] {
  return (Object.keys(breakKinds) as BreakKind[])
    .map((kind) => ({ kind, count: breaks.filter((item) => item.kind === kind).length }))
    .filter((entry) => entry.count > 0);
}

const query = (params: Record<string, string | number | undefined | null>): string => {
  const search = new URLSearchParams();
  for (const [key, value] of Object.entries(params)) if (value !== undefined && value !== null && value !== "") search.set(key, String(value));
  const text = search.toString();
  return text ? `?${text}` : "";
};

export const reconApi = {
  days: () => api<DayOverview[]>("/api/reconciliation/days?days=14"),
  summary: (date: string) => api<DaySummary>(`/api/reconciliation/summary${query({ date })}`),
  run: (id: string) => api<RunDetail>(`/api/reconciliation/runs/${id}`),
  start: (date: string, parties?: string[]) => api<ReconRun[]>("/api/reconciliation/runs", { method: "POST", body: { date, parties } }),
  breaks: (filter: { status?: string; kind?: string; party?: string; from?: string; to?: string }) =>
    api<ReconBreak[]>(`/api/reconciliation/breaks${query({ ...filter, limit: 500 })}`),
  break: (id: string) => api<BreakDetail>(`/api/reconciliation/breaks/${id}`),
  dispute: (id: string, note: string) => api<ReconBreak>(`/api/reconciliation/breaks/${id}/dispute`, { method: "POST", body: { note } }),
  resolve: (id: string, resolution: string, note: string) => api<ReconBreak>(`/api/reconciliation/breaks/${id}/resolve`, { method: "POST", body: { resolution, note } }),
  reopen: (id: string, note: string) => api<ReconBreak>(`/api/reconciliation/breaks/${id}/reopen`, { method: "POST", body: { note } }),
  file: (runId: string, party: string, date: string) => downloadFile(`/api/reconciliation/runs/${runId}/file`, `settlement-${party.replace(":", "-")}-${date}.csv`),
  audit: (before?: number) => api<ReconAuditEntry[]>(`/api/reconciliation/audit/recent${query({ before, limit: 50 })}`),
  verify: () => api<AuditVerification>("/api/reconciliation/audit/verify"),
};

export const reportsApi = {
  daily: (date: string) => api<DailyReport>(`/api/ledger/reports/daily${query({ date })}`),
  businessCsv: (from: string, to: string) => downloadFile(`/api/ledger/reports/business.csv${query({ from, to })}`, `walletpk-business-${from}-${to}.csv`),
  suspiciousCsv: (from: string, to: string, minSeverity: string) =>
    downloadFile(`/api/fraud/reports/suspicious.csv${query({ from, to, minSeverity })}`, `walletpk-suspicious-activity-${from}-${to}.csv`),
  caseReport: (caseId: string) => api<unknown>(`/api/fraud/cases/${caseId}/report`),
  ledgerAudit: (before?: number) => api<LedgerAuditRow[]>(`/api/ledger/audit/recent${query({ before, limit: 50 })}`),
  ledgerVerify: () => api<LedgerAuditReport>("/api/ledger/audit"),
  fraudAudit: (before?: number) => api<FraudAuditEntry[]>(`/api/fraud/audit/recent${query({ before, limit: 50 })}`),
  fraudVerify: () => api<{ intact: boolean; entries: number; brokenAt: number | null }>("/api/fraud/audit/verify"),
};

export const statementsApi = {
  get: (month: string) => api<MonthlyStatement>(`/api/wallets/me/statements/${month}`),
  pdf: (month: string) => downloadFile(`/api/wallets/me/statements/${month}.pdf`, `walletpk-statement-${month}.pdf`),
};

export const reportKeys = {
  days: ["recon", "days"] as const,
  summary: (date: string) => ["recon", "summary", date] as const,
  breaks: (filter: object) => ["recon", "breaks", filter] as const,
  break: (id: string) => ["recon", "break", id] as const,
  daily: (date: string) => ["reports", "daily", date] as const,
  audit: (chain: string) => ["audit", chain] as const,
  verify: (chain: string) => ["audit", "verify", chain] as const,
};
