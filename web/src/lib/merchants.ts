import { api, newIdempotencyKey } from "./api";

// Shapes of the Top-up & Merchant service's merchant JSON (M8.2-M8.5) and the calls the wallet makes.

export type FeePlan = { code: string; name: string; basisPoints: number; minFeePaisa: number; maxFeePaisa: number };

export type MerchantCategory = { code: string; name: string; feePlan: string };

export type FeePlans = { plans: FeePlan[]; categories: MerchantCategory[]; taxBasisPoints: number };

/** TopupMerchant: MerchantView. */
export type Merchant = {
  id: string;
  code: string;
  name: string;
  category: string;
  city: string | null;
  status: "Pending" | "Active";
  feePlan: FeePlan;
  unsettledPayments: number;
  unsettledGrossPaisa: number;
  unsettledNetPaisa: number;
  createdAt: string;
};

export type QrStatus = "Open" | "Paying" | "Paid" | "Expired";

/** TopupMerchant: QrView. */
export type Qr = {
  id: string;
  payload: string;
  amountPaisa: number;
  reference: string | null;
  expiresAt: string;
  status: QrStatus;
  paymentId: string | null;
  paidAt: string | null;
  netPaisa: number | null;
};

/** What a scanned code asks for (POST /api/merchants/qr/preview). */
export type QrPreview = {
  qrId: string;
  merchantCode: string;
  merchantName: string;
  category: string;
  amountPaisa: number;
  reference: string | null;
  expiresAt: string;
};

/** TopupMerchant: MerchantPaymentView. */
export type MerchantPayment = {
  id: string;
  status: "Processing" | "Completed" | "Failed";
  merchantCode: string;
  merchantName: string;
  amountPaisa: number;
  feePaisa: number;
  taxPaisa: number;
  netPaisa: number;
  reference: string | null;
  payerWalletNumber: string | null;
  failureCode: string | null;
  failureReason: string | null;
  requestedAt: string;
  paidAt: string | null;
};

/** TopupMerchant: SettlementView. */
export type Settlement = {
  id: string;
  businessDate: string;
  paymentCount: number;
  grossPaisa: number;
  feePaisa: number;
  taxPaisa: number;
  netPaisa: number;
  status: "Pending" | "Settled" | "Failed";
  failureReason: string | null;
  settledAt: string | null;
};

export const merchantsApi = {
  feePlans: () => api<FeePlans>("/api/merchants/fee-plans"),
  register: (body: { name: string; category: string; city?: string }) => api<Merchant>("/api/merchants", { method: "POST", body }),
  me: () => api<Merchant>("/api/merchants/me"),
  createQr: (body: { amountPaisa: number; reference?: string; expiresInSeconds?: number }) => api<Qr>("/api/merchants/me/qr", { method: "POST", body }),
  qr: (id: string) => api<Qr>(`/api/merchants/me/qr/${id}`),
  sales: (limit = 20) => api<MerchantPayment[]>(`/api/merchants/me/payments?limit=${limit}`),
  settlements: (limit = 30) => api<Settlement[]>(`/api/merchants/me/settlements?limit=${limit}`),
  preview: (payload: string) => api<QrPreview>("/api/merchants/qr/preview", { method: "POST", body: { payload } }),
  pay: (payload: string, idempotencyKey: string = newIdempotencyKey()) =>
    api<MerchantPayment>("/api/merchants/qr/pay", { method: "POST", body: { payload }, idempotencyKey }),
  payment: (id: string) => api<MerchantPayment>(`/api/merchants/payments/${id}`),
  payments: (limit = 10) => api<MerchantPayment[]>(`/api/merchants/payments?limit=${limit}`),
};

export const merchantKeys = {
  feePlans: ["merchants", "fee-plans"] as const,
  me: ["merchants", "me"] as const,
  qr: (id: string) => ["merchants", "qr", id] as const,
  sales: ["merchants", "sales"] as const,
  settlements: ["merchants", "settlements"] as const,
  payments: ["merchants", "payments"] as const,
  payment: (id: string) => ["merchants", "payments", id] as const,
};

/** The fee and tax on a payment, as the server works them out (half up, to whole paisa). */
export function splitFee(plan: Pick<FeePlan, "basisPoints" | "minFeePaisa" | "maxFeePaisa">, amountPaisa: number, taxBasisPoints = 1_600) {
  const round = (numerator: number) => Math.floor((numerator + 5_000) / 10_000);
  const fee = Math.min(Math.max(round(amountPaisa * plan.basisPoints), plan.minFeePaisa), plan.maxFeePaisa);
  const tax = round(fee * taxBasisPoints);
  return { amountPaisa, feePaisa: fee, taxPaisa: tax, netPaisa: amountPaisa - fee - tax };
}

/** "1% (min Rs. 1, max Rs. 500)". */
export function describePlan(plan: FeePlan): string {
  const percent = `${plan.basisPoints / 100}%`;
  const rupees = (paisa: number) => `Rs. ${(paisa / 100).toLocaleString("en-PK")}`;
  return plan.minFeePaisa > 0 ? `${percent} (min ${rupees(plan.minFeePaisa)}, max ${rupees(plan.maxFeePaisa)})` : `${percent} (max ${rupees(plan.maxFeePaisa)})`;
}

/** Whether text looks like a WalletPK QR payload (the server checks its signature). */
export function isQrPayload(text: string): boolean {
  return /^WPKQR1\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+$/.test(text.trim());
}

/** Seconds left until `expiresAt` (never below zero). */
export function secondsLeft(expiresAt: string, now: number = Date.now()): number {
  return Math.max(0, Math.floor((Date.parse(expiresAt) - now) / 1000));
}

/** 75 → "1:15". */
export function countdown(seconds: number): string {
  return `${Math.floor(seconds / 60)}:${String(seconds % 60).padStart(2, "0")}`;
}

/** QR amounts: Rs. 10 to Rs. 500,000. */
export const qrLimits = { minPaisa: 1_000, maxPaisa: 50_000_000 } as const;
