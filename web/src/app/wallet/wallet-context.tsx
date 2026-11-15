"use client";

import { createContext, useContext } from "react";
import type { LiveState } from "@/lib/use-wallet-hub";

export type WalletShellContext = { live: LiveState; freshSequences: Set<number> };

export const WalletContext = createContext<WalletShellContext>({ live: "offline", freshSequences: new Set() });

/** Live connection state and the entries that just arrived (to highlight them). */
export function useWalletShell(): WalletShellContext {
  return useContext(WalletContext);
}
