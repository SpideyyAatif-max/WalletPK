import type { Money } from "./money";

// Shapes of the API's JSON (see the C# contracts named in each comment).

/** Identity: AuthTokens. */
export type AuthTokens = {
  accessToken: string;
  tokenType: "Bearer";
  expiresIn: number;
  refreshToken: string;
  refreshTokenExpiresIn: number;
  userId: string;
  deviceId: string;
  coolingOffUntil: string | null;
};

/** Identity: DeviceVerificationChallenge (login from a new device). */
export type DeviceChallenge = { challengeId: string; phoneNumber: string; expiresInSeconds: number; resendAfterSeconds: number };

/** Identity: OtpChallenge (sign-up step 1). */
export type OtpChallenge = { phoneNumber: string; expiresInSeconds: number; resendAfterSeconds: number };

/** Identity: RegistrationTicket (sign-up step 2). */
export type RegistrationTicket = { registrationToken: string; expiresInSeconds: number };

/** Identity: RegisteredUser (sign-up step 3). */
export type RegisteredUser = { userId: string; phoneNumber: string; kycTier: string; registeredAt: string };

/** Ledger: WalletView (GET /api/wallets/me, authoritative). */
export type WalletView = {
  walletId: string;
  walletNumber: string | null;
  type: string;
  balance: Money;
  held: Money;
  available: Money;
  status: string;
  openedAt: string;
};

/** Ledger: WalletBalance (GET /api/wallets/me/balance, read model). */
export type WalletBalance = { walletId: string; walletNumber: string | null; balance: Money; sequence: number; asOf: string };

/** Ledger: StatementLine. */
export type StatementLine = {
  sequence: number;
  transactionId: string;
  type: string;
  title: string;
  direction: "In" | "Out";
  amount: Money;
  balanceAfter: Money;
  postedAt: string;
};

/** Ledger: StatementPage. */
export type StatementPage = { items: StatementLine[]; nextBefore: number | null };

/** Ledger: FeeQuote. */
export type FeeQuote = { amount: Money; fee: Money; tax: Money; totalDebit: Money; isFree: boolean };

/** Ledger: TransferReceipt. */
export type TransferReceipt = {
  transactionId: string;
  toWalletNumber: string;
  amount: Money;
  fee: Money;
  tax: Money;
  totalDebited: Money;
  balanceAfter: Money;
  postedAt: string;
};

/** Ledger: LiveWalletEntry, pushed on /hubs/wallet as "EntryPosted". */
export type LiveWalletEntry = { walletId: string; walletType: string; sequence: number; balance: Money; entry: StatementLine };

/** Identity (Development only): DevSms. */
export type DevSms = { to: string; message: string; sentAt: string };
