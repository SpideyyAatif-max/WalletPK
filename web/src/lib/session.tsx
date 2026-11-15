"use client";

import { createContext, useCallback, useContext, useEffect, useMemo, useState, type ReactNode } from "react";
import { ApiError, setTokenSource, toApiError } from "./api";
import { deviceName, installationId } from "./device";
import { readClaims, type AccessClaims } from "./jwt";
import type { SessionPayload } from "./server/session";
import type { DeviceChallenge } from "./types";

export type SessionUser = AccessClaims & { coolingOffUntil: string | null };

export type SessionState =
  | { status: "loading"; user: null }
  | { status: "signedOut"; user: null }
  | { status: "signedIn"; user: SessionUser };

export type LoginResult = { kind: "signedIn" } | { kind: "challenge"; challenge: DeviceChallenge };

type SessionContextValue = SessionState & {
  login(phoneNumber: string, mpin: string): Promise<LoginResult>;
  verifyDevice(challengeId: string, code: string): Promise<void>;
  logout(): Promise<void>;
  getAccessToken(): Promise<string | null>;
};

const SessionContext = createContext<SessionContextValue | null>(null);

// ---- Token state, shared by every component and the API client ----

let access: { token: string; expiresAt: number } | null = null;
let inflightRefresh: Promise<SessionPayload | null> | null = null;

async function postSession(path: string, body?: unknown): Promise<Response> {
  return fetch(path, {
    method: "POST",
    headers: body === undefined ? undefined : { "Content-Type": "application/json" },
    body: body === undefined ? undefined : JSON.stringify(body),
    credentials: "same-origin",
    cache: "no-store",
  });
}

/**
 * One refresh at a time: within this tab (React may mount twice) through a shared promise, and across
 * tabs through a Web Lock. Refresh tokens are single use, so two tabs must never send the same one.
 */
function refreshSession(): Promise<SessionPayload | null> {
  if (inflightRefresh) return inflightRefresh;
  const run = async () => {
    const response = await postSession("/session/refresh");
    if (response.status === 200) return (await response.json()) as SessionPayload;
    if (response.status >= 500) throw await toApiError(response);
    return null;
  };
  const locked = navigator.locks
    ? navigator.locks.request("walletpk-session-refresh", run).then((result) => result)
    : run();
  const pending = locked.finally(() => {
    inflightRefresh = null;
  });
  inflightRefresh = pending;
  return pending;
}

export function SessionProvider({ children }: { children: ReactNode }) {
  const [state, setState] = useState<SessionState>({ status: "loading", user: null });
  const [expiresAt, setExpiresAt] = useState<number | null>(null);

  const signOutLocally = useCallback(() => {
    access = null;
    setExpiresAt(null);
    setState({ status: "signedOut", user: null });
  }, []);

  // Applies a session payload (or its absence) and returns the access token.
  const adopt = useCallback(
    (payload: SessionPayload | null) => {
      if (!payload) {
        signOutLocally();
        return null;
      }
      const claims = readClaims(payload.accessToken);
      const expiry = claims?.expiresAt || Date.now() + payload.expiresIn * 1000;
      access = { token: payload.accessToken, expiresAt: expiry };
      setExpiresAt(expiry);
      setState({
        status: "signedIn",
        user: {
          ...(claims ?? { sub: payload.userId, phoneNumber: null, kycTier: null, roles: [], deviceId: payload.deviceId, expiresAt: expiry }),
          coolingOffUntil: payload.coolingOffUntil,
        },
      });
      return payload.accessToken;
    },
    [signOutLocally],
  );

  // Refresh a minute before the access token expires, so live connections and requests never see a 401.
  useEffect(() => {
    if (expiresAt === null) return;
    const timer = setTimeout(
      () => void refreshSession().then(adopt).catch(() => undefined),
      Math.max(5_000, expiresAt - Date.now() - 60_000),
    );
    return () => clearTimeout(timer);
  }, [expiresAt, adopt]);

  const getAccessToken = useCallback(async () => {
    if (access && access.expiresAt - Date.now() > 30_000) return access.token;
    if (!access) return null;
    return adopt(await refreshSession());
  }, [adopt]);

  // Restore the session on page load from the refresh cookie.
  useEffect(() => {
    let cancelled = false;
    refreshSession()
      .then((payload) => {
        if (!cancelled) adopt(payload);
      })
      .catch(() => {
        if (!cancelled) signOutLocally();
      });
    return () => {
      cancelled = true;
    };
  }, [adopt, signOutLocally]);

  // The API client asks the session for tokens.
  useEffect(() => {
    setTokenSource({
      getAccessToken,
      refreshAccessToken: async () => adopt(await refreshSession()),
    });
    return () => setTokenSource(null);
  }, [adopt, getAccessToken]);

  const login = useCallback(
    async (phoneNumber: string, mpin: string): Promise<LoginResult> => {
      const response = await postSession("/session/login", { phoneNumber, mpin, installationId: installationId(), deviceName: deviceName() });
      if (response.status === 202) return { kind: "challenge", challenge: (await response.json()) as DeviceChallenge };
      if (!response.ok) throw await toApiError(response);
      adopt((await response.json()) as SessionPayload);
      return { kind: "signedIn" };
    },
    [adopt],
  );

  const verifyDevice = useCallback(
    async (challengeId: string, code: string) => {
      const response = await postSession("/session/verify-device", { challengeId, code });
      if (!response.ok) throw await toApiError(response);
      adopt((await response.json()) as SessionPayload);
    },
    [adopt],
  );

  const logout = useCallback(async () => {
    await postSession("/session/logout").catch(() => undefined);
    signOutLocally();
  }, [signOutLocally]);

  const value = useMemo<SessionContextValue>(
    () => ({ ...state, login, verifyDevice, logout, getAccessToken }),
    [state, login, verifyDevice, logout, getAccessToken],
  );

  return <SessionContext.Provider value={value}>{children}</SessionContext.Provider>;
}

export function useSession(): SessionContextValue {
  const value = useContext(SessionContext);
  if (!value) throw new Error("useSession must be used inside <SessionProvider>.");
  return value;
}

export { ApiError };
