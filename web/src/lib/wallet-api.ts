import { api, newIdempotencyKey } from "./api";
import type {
  DevSms,
  FeeQuote,
  OtpChallenge,
  RegisteredUser,
  RegistrationTicket,
  StatementPage,
  TransferReceipt,
  WalletBalance,
  WalletView,
} from "./types";

// ---- Sign-up (no token; straight to the gateway) ----

export const registration = {
  start: (phoneNumber: string) =>
    api<OtpChallenge>("/api/identity/auth/register/", { method: "POST", body: { phoneNumber }, authenticated: false }),
  verifyOtp: (phoneNumber: string, code: string) =>
    api<RegistrationTicket>("/api/identity/auth/register/verify-otp", { method: "POST", body: { phoneNumber, code }, authenticated: false }),
  setMpin: (registrationToken: string, mpin: string, confirmMpin: string) =>
    api<RegisteredUser>("/api/identity/auth/register/set-mpin", {
      method: "POST",
      body: { registrationToken, mpin, confirmMpin },
      authenticated: false,
    }),
};

// ---- Wallet ----

export type HistoryFilter = { type?: string; direction?: "in" | "out" };

export const walletApi = {
  me: () => api<WalletView>("/api/wallets/me"),
  balance: () => api<WalletBalance>("/api/wallets/me/balance"),
  history: (filter: HistoryFilter, before: number | null, limit = 20) => {
    const query = new URLSearchParams({ limit: String(limit) });
    if (before !== null) query.set("before", String(before));
    if (filter.type) query.set("type", filter.type);
    if (filter.direction) query.set("direction", filter.direction);
    return api<StatementPage>(`/api/wallets/me/transactions?${query}`);
  },
  quote: (amountPaisa: number) => api<FeeQuote>(`/api/wallets/transfers/quote?amountPaisa=${amountPaisa}`),
  /** One key per tap of "Send": a retry of the same tap can never send twice. */
  transfer: (toWalletNumber: string, amountPaisa: number, idempotencyKey: string) =>
    api<TransferReceipt>("/api/wallets/transfers", { method: "POST", body: { toWalletNumber, amountPaisa }, idempotencyKey }),
};

// ---- Step-up OTP (M10.4): confirming a payment the fraud engine asked to step up ----

/** The error code a payment fails with when it needs a one-time code first. */
export const STEP_UP_REQUIRED = "fraud.step_up_required";

export const needsStepUp = (failureCode: string | null | undefined): boolean => failureCode === STEP_UP_REQUIRED;

export type StepUpCode = { expiresInSeconds: number; resendAfterSeconds: number };
export type StepUpConfirmation = { validUntil: string };

export const stepUpApi = {
  /** Texts a code to the signed-in user's phone. */
  start: () => api<StepUpCode>("/api/identity/step-up", { method: "POST" }),
  /** Checks it; the next payment from this device that needs a code then goes through (within 5 minutes). */
  verify: (code: string) => api<StepUpConfirmation>("/api/identity/step-up/verify", { method: "POST", body: { code } }),
};

// ---- Developer tools (services in Development only) ----

export const devTools = {
  sms: (phoneNumber: string) =>
    api<DevSms[]>(`/api/identity/dev/sms?phoneNumber=${encodeURIComponent(phoneNumber)}`, { authenticated: false }),
  topUp: (amountPaisa: number) =>
    api<WalletView>("/api/wallets/me/dev/top-up", { method: "POST", body: { amountPaisa }, idempotencyKey: newIdempotencyKey() }),
};

/** TanStack Query keys, in one place so live updates touch the same cache entries the screens read. */
export const queryKeys = {
  wallet: ["wallet"] as const,
  me: ["wallet", "me"] as const,
  balance: ["wallet", "balance"] as const,
  history: (filter: HistoryFilter) => ["wallet", "history", filter.type ?? "", filter.direction ?? ""] as const,
  quote: (amountPaisa: number) => ["wallet", "quote", amountPaisa] as const,
};
