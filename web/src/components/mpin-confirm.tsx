"use client";

import { LockKeyhole } from "lucide-react";
import { useState, type FormEvent } from "react";
import { Button } from "@/components/ui/button";
import { Alert } from "@/components/ui/card";
import { Field, Input } from "@/components/ui/input";
import { isMpinLockout, isValidMpin, mpinApi } from "@/lib/mpin";

/**
 * The MPIN on the confirmation screen (proposal 12.1). Confirms the payment(s) named by `bindings` (their
 * Idempotency-Keys, or "money-request:{id}") one after another, so a wrong MPIN counts once, then hands the
 * tokens to `onConfirmed` in the same order. The MPIN itself is never kept.
 */
export function MpinConfirm({
  bindings,
  action,
  onConfirmed,
  onCancel,
  busy = false,
  error,
}: {
  bindings: [string, ...string[]];
  /** The button's label, for example "Send Rs. 500". */
  action: string;
  onConfirmed: (tokens: [string, ...string[]]) => void;
  onCancel: () => void;
  /** The payment itself is being sent. */
  busy?: boolean;
  /** The payment's own error, if it failed after confirming. */
  error?: string | null;
}) {
  const [mpin, setMpin] = useState("");
  const [checking, setChecking] = useState(false);
  const [problem, setProblem] = useState<{ message: string; final: boolean } | null>(null);

  async function submit(event: FormEvent) {
    event.preventDefault();
    if (!isValidMpin(mpin)) return setProblem({ message: "Your MPIN is 5 digits.", final: false });
    setChecking(true);
    setProblem(null);
    try {
      const [first, ...rest] = bindings;
      const tokens: [string, ...string[]] = [(await mpinApi.confirm(mpin, first)).confirmationToken];
      for (const binding of rest) {
        tokens.push((await mpinApi.confirm(mpin, binding)).confirmationToken);
      }
      setMpin("");
      onConfirmed(tokens);
    } catch (caught) {
      setMpin("");
      setProblem({ message: caught instanceof Error ? caught.message : "Could not check your MPIN.", final: isMpinLockout(caught) });
    } finally {
      setChecking(false);
    }
  }

  return (
    <form onSubmit={submit} className="space-y-4 rounded-xl border border-brand-100 bg-brand-50/40 p-4" noValidate>
      <div className="flex items-start gap-3">
        <LockKeyhole className="mt-0.5 h-5 w-5 shrink-0 text-brand-700" aria-hidden />
        <div>
          <p className="font-semibold">Enter your MPIN</p>
          <p className="text-sm text-ink-600">To confirm this payment.</p>
        </div>
      </div>
      <Field label="MPIN" htmlFor="confirm-mpin">
        <Input
          id="confirm-mpin"
          type="password"
          inputMode="numeric"
          autoComplete="off"
          maxLength={5}
          value={mpin}
          onChange={(event) => setMpin(event.target.value.replace(/\D/g, ""))}
          className="text-center font-mono text-lg tracking-[0.6em]"
          disabled={problem?.final}
          autoFocus
        />
      </Field>
      {problem && <Alert>{problem.message}</Alert>}
      {!problem && error && <Alert>{error}</Alert>}
      <div className="grid grid-cols-2 gap-3">
        <Button type="button" variant="outline" onClick={onCancel} disabled={checking || busy}>
          Back
        </Button>
        <Button type="submit" loading={checking || busy} disabled={!isValidMpin(mpin) || problem?.final}>
          {action}
        </Button>
      </div>
    </form>
  );
}
