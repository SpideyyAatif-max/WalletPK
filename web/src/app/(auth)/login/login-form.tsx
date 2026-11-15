"use client";

import Link from "next/link";
import { useRouter, useSearchParams } from "next/navigation";
import { useEffect, useState, type FormEvent } from "react";
import { DevSmsPanel } from "@/components/dev-sms-panel";
import { Button } from "@/components/ui/button";
import { Alert, Card } from "@/components/ui/card";
import { Field, Input } from "@/components/ui/input";
import { ApiError } from "@/lib/api";
import { toE164 } from "@/lib/phone";
import { useSession } from "@/lib/session";
import type { DeviceChallenge } from "@/lib/types";

export function LoginForm() {
  const router = useRouter();
  const params = useSearchParams();
  const { status, login, verifyDevice } = useSession();

  const [phone, setPhone] = useState(params.get("phone") ?? "");
  const [mpin, setMpin] = useState("");
  const [challenge, setChallenge] = useState<DeviceChallenge | null>(null);
  const [code, setCode] = useState("");
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  useEffect(() => {
    if (status === "signedIn") router.replace("/wallet");
  }, [status, router]);

  const e164 = toE164(phone);

  async function submitCredentials(event: FormEvent) {
    event.preventDefault();
    if (!e164) return setError("Enter a Pakistani mobile number, for example 0300 1234567.");
    if (!/^\d{5}$/.test(mpin)) return setError("Your MPIN is 5 digits.");
    setBusy(true);
    setError(null);
    try {
      const result = await login(e164, mpin);
      if (result.kind === "challenge") setChallenge(result.challenge);
    } catch (caught) {
      setError(caught instanceof ApiError ? caught.message : "Could not log in. Please try again.");
    } finally {
      setBusy(false);
    }
  }

  async function submitCode(event: FormEvent) {
    event.preventDefault();
    if (!challenge) return;
    setBusy(true);
    setError(null);
    try {
      await verifyDevice(challenge.challengeId, code);
    } catch (caught) {
      setError(caught instanceof ApiError ? caught.message : "Could not verify the code.");
    } finally {
      setBusy(false);
    }
  }

  if (challenge && e164) {
    return (
      <Card className="space-y-5">
        <div>
          <h1 className="text-xl font-bold">New device</h1>
          <p className="mt-1 text-sm text-ink-600">
            For your safety, enter the 6-digit code we sent to {challenge.phoneNumber}. Next time this browser only needs your MPIN.
          </p>
        </div>
        <form onSubmit={submitCode} className="space-y-4">
          <Field label="Verification code" htmlFor="code">
            <Input
              id="code"
              inputMode="numeric"
              autoComplete="one-time-code"
              maxLength={6}
              value={code}
              onChange={(event) => setCode(event.target.value.replace(/\D/g, ""))}
              className="text-center font-mono text-2xl tracking-[0.5em]"
              autoFocus
            />
          </Field>
          {error && <Alert>{error}</Alert>}
          <Button type="submit" size="lg" className="w-full" loading={busy} disabled={code.length !== 6}>
            Verify and log in
          </Button>
        </form>
        <DevSmsPanel phoneNumber={e164} onUseCode={setCode} />
        <button type="button" className="text-sm font-medium text-ink-600 hover:text-ink-900" onClick={() => setChallenge(null)}>
          ← Back
        </button>
      </Card>
    );
  }

  return (
    <Card className="space-y-5">
      <div>
        <h1 className="text-xl font-bold">Log in</h1>
        <p className="mt-1 text-sm text-ink-600">Welcome back.</p>
      </div>
      <form onSubmit={submitCredentials} className="space-y-4" noValidate>
        <Field label="Mobile number" htmlFor="phone">
          <Input id="phone" inputMode="tel" autoComplete="tel" placeholder="0300 1234567" value={phone} onChange={(event) => setPhone(event.target.value)} autoFocus={!phone} />
        </Field>
        <Field label="MPIN" htmlFor="mpin">
          <Input
            id="mpin"
            type="password"
            inputMode="numeric"
            autoComplete="current-password"
            maxLength={5}
            value={mpin}
            onChange={(event) => setMpin(event.target.value.replace(/\D/g, ""))}
            className="font-mono tracking-[0.4em]"
            autoFocus={!!phone}
          />
        </Field>
        {error && <Alert>{error}</Alert>}
        <Button type="submit" size="lg" className="w-full" loading={busy}>
          Log in
        </Button>
      </form>
      <p className="text-center text-sm text-ink-600">
        New to WalletPK?{" "}
        <Link href="/signup" className="font-semibold text-brand-700 hover:underline">
          Create an account
        </Link>
      </p>
    </Card>
  );
}
