import { api, newIdempotencyKey } from "./api";
import type { TimelineStep } from "./payments";
import { toE164 } from "./phone";

// Shapes of the Top-up & Merchant service's top-up JSON (M8.1) and the calls the wallet makes.

/** TopupMerchant: NetworkAvailability (GET /api/topups/networks). */
export type Network = {
  code: "JAZZ" | "ZONG" | "UFONE" | "TELENOR";
  name: string;
  prefixes: string[];
  status: "Available" | "Degraded" | "Down";
  since: string | null;
};

/** A network's bundle (GET /api/topups/networks/{code}/bundles). */
export type Bundle = { code: string; name: string; pricePaisa: number; validityDays: number };

export type TopUpStatus = "Processing" | "UnderReview" | "Completed" | "Failed";

/** TopupMerchant: TopUpView. */
export type TopUp = {
  id: string;
  status: TopUpStatus;
  step: string;
  network: string;
  networkName: string;
  msisdn: string;
  amountPaisa: number;
  bundleCode: string | null;
  bundleName: string | null;
  telcoReference: string | null;
  failureCode: string | null;
  failureReason: string | null;
  requestedAt: string;
  updatedAt: string;
  completedAt: string | null;
  timeline: TimelineStep[] | null;
};

export const topUpsApi = {
  networks: () => api<Network[]>("/api/topups/networks"),
  bundles: (network: string) => api<Bundle[]>(`/api/topups/networks/${network}/bundles`),
  topUp: (body: { msisdn: string; amountPaisa: number; bundleCode?: string; bundleName?: string }, idempotencyKey: string = newIdempotencyKey()) =>
    api<TopUp>("/api/topups", { method: "POST", body, idempotencyKey }),
  topUpById: (id: string) => api<TopUp>(`/api/topups/${id}`),
  list: (limit = 10) => api<TopUp[]>(`/api/topups?limit=${limit}`),
};

export const topUpKeys = {
  networks: ["topups", "networks"] as const,
  bundles: (network: string) => ["topups", "bundles", network] as const,
  list: ["topups", "list"] as const,
  topUp: (id: string) => ["topups", id] as const,
};

/** The same prefixes the server uses (Pakistan has no number portability between these here). */
const prefixes: Record<Network["code"], [number, number][]> = {
  JAZZ: [[300, 309], [320, 329]],
  ZONG: [[310, 319]],
  UFONE: [[330, 339]],
  TELENOR: [[340, 349]],
};

/** The network of a typed number ("0300 1234567"), or null while it is not a full number on one of them. */
export function networkOf(input: string): Network["code"] | null {
  const e164 = toE164(input);
  if (!e164) return null;
  const prefix = Number(e164.slice(3, 6));
  for (const [code, ranges] of Object.entries(prefixes) as [Network["code"], [number, number][]][]) {
    if (ranges.some(([from, to]) => prefix >= from && prefix <= to)) return code;
  }
  return null;
}

/** Airtime: whole rupees from Rs. 20 to Rs. 5,000. */
export const airtimeLimits = { minPaisa: 2_000, maxPaisa: 500_000 } as const;

/** Why an airtime amount is refused, or null when it is fine. */
export function airtimeProblem(paisa: number | null): string | null {
  if (paisa === null) return "Enter an amount.";
  if (paisa % 100 !== 0) return "Airtime is whole rupees.";
  if (paisa < airtimeLimits.minPaisa || paisa > airtimeLimits.maxPaisa) return "Airtime is Rs. 20 to Rs. 5,000.";
  return null;
}

/** The amounts offered as one-tap buttons. */
export const quickAirtime = [10_000, 20_000, 50_000, 100_000] as const;

/** A top-up that will not change any more. */
export function isTopUpFinal(status: string): boolean {
  return status === "Completed" || status === "Failed";
}

/** "1 day", "7 days", "30 days". */
export function validity(days: number): string {
  return days === 1 ? "1 day" : `${days} days`;
}

/** Step names on the top-up tracker (they differ from a bank transfer's). */
export const topUpStepNames: Record<string, string> = {
  Reserving: "Reserving the money",
  ToppingUp: "Sending to the network",
  Retrying: "Network unavailable: retrying",
  Verifying: "Checking with the network",
  Settling: "Number topped up",
  Returning: "Returning the money",
  DeadLettered: "Being checked by our team",
  Completed: "Done",
  Failed: "Not topped up",
};
