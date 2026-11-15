"use client";

import { useInfiniteQuery, useQuery } from "@tanstack/react-query";
import { ApiError } from "./api";
import { queryKeys, walletApi, type HistoryFilter } from "./wallet-api";

/** A brand-new account's wallet is opened a moment after sign-up (identity.user_registered → Ledger): keep asking. */
const walletNotReady = (error: unknown) => error instanceof ApiError && error.code === "wallet.not_found";

export function useWallet() {
  return useQuery({
    queryKey: queryKeys.me,
    queryFn: walletApi.me,
    refetchInterval: (query) => (walletNotReady(query.state.error) ? 1_000 : false),
  });
}

/** The live balance (read model + pushes). */
export function useBalance() {
  return useQuery({
    queryKey: queryKeys.balance,
    queryFn: walletApi.balance,
    refetchInterval: (query) => (walletNotReady(query.state.error) ? 1_000 : false),
  });
}

export function useHistory(filter: HistoryFilter, pageSize = 20) {
  return useInfiniteQuery({
    queryKey: queryKeys.history(filter),
    queryFn: ({ pageParam }) => walletApi.history(filter, pageParam, pageSize),
    initialPageParam: null as number | null,
    getNextPageParam: (last) => last.nextBefore,
  });
}

export { walletNotReady };
