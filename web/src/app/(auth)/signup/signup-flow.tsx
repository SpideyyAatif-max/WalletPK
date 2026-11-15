"use client";

import { CheckCircle2 } from "lucide-react";
import Link from "next/link";
import { useState, type FormEvent } from "react";
import { DevSmsPanel } from "@/components/dev-sms-panel";
import { Button } from "@/components/ui/button";
import { Alert, Card } from "@/components/ui/card";
import { Field, Input } from "@/components/ui/input";
import { ApiError } from "@/lib/api";
import { formatLocal, toE164 } from "@/lib/phone";
import { registration } from "@/lib/wallet-api";

type Step = { name: "phone" } | { name: "otp"; phone: string } | { name: "mpin"; phone: string; token: string } | { name: "done"; phone: string };

/** Three steps, as Identity expects them: phone → OTP → MPIN. The wallet is opened automatically (identity.user_registered → Ledger). */
export function SignupFlow() {
  const [step, setStep] = useState<Step>({ name: "phone" });
  const [phone, setPhone] = useState("");
  const [code, setCode] = useState("");
  const [mpin, setMpin] = useState("");
  const [confirm, setConfirm] = useState("");
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  async function run(action: () => Promise<void>) {
    setBusy(true);
    setError(null);
    try {
      await action();
    } catch (caught) {
      setError(caught instanceof ApiError ? caught.message : "Something went wrong. Please try again.");
    } finally {
      setBusy(false);
    }
  }

  function submitPhone(event: FormEvent) {
    event.preventDefault();
    const e164 = toE164(phone);
    if (!e164) return setError("Enter a Pakistani mobile number, for example 0300 1234567.");
    void run(async () => {
      await registration.start(e164);
      setCode("");
      setStep({ name: "otp", phone: e164 });
    });
  }

  function submitOtp(event: FormEvent) {
    event.preventDefault();
    if (step.name !== "otp") return;
    void run(async () => {
      const ticket = await registration.verifyOtp(step.phone, code);
      setStep({ name: "mpin", phone: step.phone, token: ticket.registrationToken });
    });
  }

  function submitMpin(event: FormEvent) {
    event.preventDefault();
    if (step.name !== "mpin") return;
    if (!/^\d{5}$/.test(mpin)) return setError("Choose a 5-digit MPIN.");
    if (mpin !== confirm) return setError("The two MPINs do not match.");
    void run(async () => {
      await registration.setMpin(step.token, mpin, confirm);
      setStep({ name: "done", phone: step.phone });
    });
  }

  const stepNumber = { phone: 1, otp: 2, mpin: 3, done: 3 }[step.name];

  return (
    <Card className="space-y-5">
      <div>
        <p className="text-xs font-semibold tracking-wide text-brand-700 uppercase">Step {stepNumber} of 3</p>
        <h1 className="mt-1 text-xl font-bold">
          {step.name === "phone" && "Create your wallet"}
          {step.name === "otp" && "Verify your number"}
          {step.name === "mpin" && "Choose your MPIN"}
          {step.name === "done" && "You're all set"}
        </h1>
      </div>

      {step.name === "phone" && (
        <form onSubmit={submitPhone} className="space-y-4" noValidate>
          <Field label="Mobile number" htmlFor="phone" hint="Your wallet number. We'll text you a code.">
            <Input id="phone" inputMode="tel" autoComplete="tel" placeholder="0300 1234567" value={phone} onChange={(event) => setPhone(event.target.value)} autoFocus />
          </Field>
          {error && <Alert>{error}</Alert>}
          <Button type="submit" size="lg" className="w-full" loading={busy}>
            Send code
          </Button>
        </form>
      )}

      {step.name === "otp" && (
        <>
          <form onSubmit={submitOtp} className="space-y-4">
            <Field label={`Code sent to ${formatLocal(step.phone)}`} htmlFor="code">
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
              Verify
            </Button>
          </form>
          <DevSmsPanel phoneNumber={step.phone} onUseCode={setCode} />
        </>
      )}

      {step.name === "mpin" && (
        <form onSubmit={submitMpin} className="space-y-4" noValidate>
          <Field label="MPIN" htmlFor="mpin" hint="5 digits. Avoid 12345 or your birth year.">
            <Input id="mpin" type="password" inputMode="numeric" autoComplete="new-password" maxLength={5} value={mpin} onChange={(event) => setMpin(event.target.value.replace(/\D/g, ""))} className="font-mono tracking-[0.4em]" autoFocus />
          </Field>
          <Field label="Confirm MPIN" htmlFor="confirm">
            <Input id="confirm" type="password" inputMode="numeric" autoComplete="new-password" maxLength={5} value={confirm} onChange={(event) => setConfirm(event.target.value.replace(/\D/g, ""))} className="font-mono tracking-[0.4em]" />
          </Field>
          {error && <Alert>{error}</Alert>}
          <Button type="submit" size="lg" className="w-full" loading={busy}>
            Create account
          </Button>
        </form>
      )}

      {step.name === "done" && (
        <div className="space-y-4 text-center">
          <CheckCircle2 className="mx-auto h-12 w-12 text-brand-600" aria-hidden />
          <p className="text-sm text-ink-600">Your account and wallet for {formatLocal(step.phone)} are ready. Log in to start sending money.</p>
          <Link href={`/login?phone=${encodeURIComponent(formatLocal(step.phone))}`} className="inline-flex h-12 w-full items-center justify-center rounded-xl bg-brand-600 font-semibold text-white hover:bg-brand-700">
            Log in
          </Link>
        </div>
      )}

      {step.name !== "done" && (
        <p className="text-center text-sm text-ink-600">
          Already have an account?{" "}
          <Link href="/login" className="font-semibold text-brand-700 hover:underline">
            Log in
          </Link>
        </p>
      )}
    </Card>
  );
}
