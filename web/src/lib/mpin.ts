import { ApiError, api } from "./api";
import type { components } from "./api-types/identity";

// MPIN confirmation of payments (proposal 12.1; see WalletPK.Web.Security.MpinConfirmation on the server).
// The app sends the MPIN and the payment's Idempotency-Key to Identity, gets a token valid for 5 minutes for
// this user, device and payment, and sends the payment with it in X-Mpin-Confirmation.

export const MPIN_HEADER = "X-Mpin-Confirmation";

/** The code a payment is refused with when it has no (valid) MPIN confirmation. */
export const MPIN_REQUIRED = "auth.mpin_confirmation_required";

/** Identity: MpinConfirmationResult. */
export type MpinConfirmation = components["schemas"]["MpinConfirmationResult"];

export const mpinApi = {
  /** Checks the MPIN (wrong ones count towards the login lockout) and confirms one payment. */
  confirm: (mpin: string, binding: string) =>
    api<MpinConfirmation>("/api/identity/mpin/confirm", { method: "POST", body: { mpin, idempotencyKey: binding } }),
};

/** What a money request is confirmed for (paying it has no Idempotency-Key of its own). */
export const moneyRequestBinding = (requestId: string): string => `money-request:${requestId}`;

export const mpinHeaders = (confirmation: string): Record<string, string> => ({ [MPIN_HEADER]: confirmation });

export const isValidMpin = (value: string): boolean => /^\d{5}$/.test(value);

/** The payment was refused because its MPIN confirmation is missing or has expired: ask again. */
export const needsMpin = (error: unknown): boolean => error instanceof ApiError && error.code === MPIN_REQUIRED;

/** The account is locked (too many wrong MPINs) or cannot pay: asking again will not help. */
export const isMpinLockout = (error: unknown): boolean =>
  error instanceof ApiError && (error.code === "identity.account_locked" || error.code === "identity.cannot_transact" || error.code === "identity.account_frozen");
