import { api } from "./api";
import type { components } from "./api-types/identity";

// KYC and limits (Identity, M5.5 and M5.3): GET/POST /api/identity/kyc, GET /api/identity/limits.

type Schemas = components["schemas"];

export type KycTier = Schemas["KycTier"];
export type KycStatus = Schemas["KycStatus"];
export type LimitUsage = Schemas["LimitUsageView"];
export type LimitsSummary = Schemas["LimitsSummary"];

export const identityApi = {
  kyc: () => api<KycStatus>("/api/identity/kyc"),
  /** CNIC + name + date of birth (YYYY-MM-DD) → NADRA → L1. */
  submitKyc: (body: { cnic: string; fullName: string; dateOfBirth: string }) =>
    api<KycStatus>("/api/identity/kyc", { method: "POST", body }),
  limits: () => api<LimitsSummary>("/api/identity/limits"),
  /** Development only: take another role (to try the operations portal); the next token carries it. */
  devRole: (role: string) => api<{ role: string }>("/api/identity/dev/role", { method: "PUT", body: { role } }),
};

export const identityKeys = {
  kyc: ["identity", "kyc"] as const,
  limits: ["identity", "limits"] as const,
};

/** "35202-1234567-1", "3520212345671" or with spaces → 13 digits, or null. */
export function normalizeCnic(input: string): string | null {
  const digits = input.replace(/[\s-]/g, "");
  return /^\d{13}$/.test(digits) ? digits : null;
}

/** 13 digits → "35202-1234567-1" (partial input is formatted as far as it goes). */
export function formatCnic(input: string): string {
  const digits = input.replace(/\D/g, "").slice(0, 13);
  if (digits.length <= 5) return digits;
  if (digits.length <= 12) return `${digits.slice(0, 5)}-${digits.slice(5)}`;
  return `${digits.slice(0, 5)}-${digits.slice(5, 12)}-${digits.slice(12)}`;
}

/** An adult's date of birth (18 or older on `today`), as YYYY-MM-DD; null if not a real date. */
export function checkDateOfBirth(value: string, today = new Date()): { ok: true } | { ok: false; reason: string } {
  const match = /^(\d{4})-(\d{2})-(\d{2})$/.exec(value);
  if (!match) return { ok: false, reason: "Enter your date of birth." };
  const [year, month, day] = [Number(match[1]), Number(match[2]), Number(match[3])];
  const date = new Date(Date.UTC(year, month - 1, day));
  if (date.getUTCFullYear() !== year || date.getUTCMonth() !== month - 1 || date.getUTCDate() !== day) {
    return { ok: false, reason: "That date does not exist." };
  }
  const eighteenth = new Date(Date.UTC(year + 18, month - 1, day));
  if (eighteenth > today) return { ok: false, reason: "You must be 18 or older to verify an account." };
  if (year < 1900) return { ok: false, reason: "Check the year." };
  return { ok: true };
}

/** Share of a limit used, 0-100, for the bars. */
export function usedPercent(usage: LimitUsage): number {
  if (usage.limit.minorUnits <= 0) return 100;
  return Math.min(100, Math.round((usage.used.minorUnits / usage.limit.minorUnits) * 100));
}

export const tierNames: Record<KycTier, string> = {
  L0: "Basic (L0)",
  L1: "Verified (L1)",
  L2: "Full (L2)",
};
