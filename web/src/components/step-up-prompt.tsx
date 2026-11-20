"use client";

import { useMutation } from "@tanstack/react-query";
import { ShieldCheck } from "lucide-react";
import { useEffect, useRef, useState, type FormEvent } from "react";
import { DevSmsPanel } from "@/components/dev-sms-panel";
import { Button } from "@/components/ui/button";
import { Alert, Card } from "@/components/ui/card";
import { Field, Input } from "@/components/ui/input";
import { useSession } from "@/lib/session";
import { stepUpApi } from "@/lib/wallet-api";

/** How long the confirmation takes to reach the fraud service (identity.events) before the payment is sent again. */
const SETTLE_MS = 1_200;

/**
 * Step-up OTP (M10.4): the fraud engine wants this payment confirmed. Texts a code to the user's phone,
 * checks it, then either sends the payment again (onConfirmed) or tells the user to.
 */
export function StepUpPrompt({ reason, onConfirmed, onCancel }: { reason: string | null; onConfirmed?: () => void; onCancel?: () => void }) {
  const { user } = useSession();
  const [code, setCode] = useState("");
  const [confirmed, setConfirmed] = useState(false);
  const asked = useRef(false);

  const start = useMutation({ mutationFn: stepUpApi.start });
  const verify = useMutation({
    mutationFn: (value: string) => stepUpApi.verify(value),
    onSuccess: () => {
      setConfirmed(true);
      if (onConfirmed) setTimeout(onConfirmed, SETTLE_MS);
    },
  });

  // One code when the prompt opens; "Send a new code" asks again.
  useEffect(() => {
    if (asked.current) return;
    asked.current = true;
    start.mutate();
  }, [start]);

  function submit(event: FormEvent) {
    event.preventDefault();
    const clean = code.replace(/\D/g, "");
    if (clean.length !== 6) return;
    verify.mutate(clean);
  }

  if (confirmed) {
    return (
      <Card className="space-y-3 text-center">
        <ShieldCheck className="mx-auto h-12 w-12 text-brand-600" aria-hidden />
        <p className="font-semibold">Payment confirmed</p>
        <p className="text-sm text-ink-600">{onConfirmed ? "Sending it again…" : "Send the payment again within 5 minutes from this device."}</p>
      </Card>
    );
  }

  return (
    <Card className="space-y-4">
      <div className="flex items-start gap-3">
        <ShieldCheck className="mt-0.5 h-6 w-6 shrink-0 text-brand-600" aria-hidden />
        <div>
          <h2 className="font-semibold">Confirm this payment</h2>
          <p className="mt-1 text-sm text-ink-600">{reason ?? "This payment needs an extra check."}</p>
          <p className="mt-1 text-sm text-ink-600">We sent a 6-digit code to your phone. No money has left your wallet.</p>
        </div>
      </div>
      <form onSubmit={submit} className="space-y-3" noValidate>
        <Field label="Code" htmlFor="step-up-code">
          <Input
            id="step-up-code"
            inputMode="numeric"
            autoComplete="one-time-code"
            maxLength={6}
            placeholder="123456"
            value={code}
            onChange={(event) => setCode(event.target.value)}
            className="tabular text-center text-lg tracking-widest"
            autoFocus
          />
        </Field>
        {start.error && <Alert>{start.error.message}</Alert>}
        {verify.error && <Alert>{verify.error.message}</Alert>}
        <div className="grid grid-cols-2 gap-3">
          <Button type="button" variant="outline" onClick={() => (onCancel ? onCancel() : start.mutate())} disabled={verify.isPending}>
            {onCancel ? "Cancel" : "Send a new code"}
          </Button>
          <Button type="submit" loading={verify.isPending} disabled={code.replace(/\D/g, "").length !== 6}>
            Confirm
          </Button>
        </div>
        {onCancel && (
          <button type="button" onClick={() => start.mutate()} className="w-full text-center text-sm font-medium text-brand-700 hover:underline" disabled={start.isPending}>
            Send a new code
          </button>
        )}
      </form>
      {user?.phoneNumber && <DevSmsPanel phoneNumber={user.phoneNumber} onUseCode={setCode} />}
    </Card>
  );
}
