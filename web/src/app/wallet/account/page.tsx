"use client";

import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { BadgeCheck, Clock, IdCard, ShieldAlert } from "lucide-react";
import { useState, type FormEvent } from "react";
import { Button } from "@/components/ui/button";
import { Alert, Card } from "@/components/ui/card";
import { Field, Input } from "@/components/ui/input";
import { devToolsEnabled } from "@/lib/config";
import { formatDay, formatTime } from "@/lib/dates";
import { checkDateOfBirth, formatCnic, identityApi, identityKeys, normalizeCnic, tierNames, usedPercent, type KycStatus, type LimitUsage } from "@/lib/identity";
import { formatMoney } from "@/lib/money";
import { formatLocal } from "@/lib/phone";
import { roleNames } from "@/lib/roles";
import { useSession } from "@/lib/session";
import { cn } from "@/lib/utils";

/**
 * Account and limits: the KYC tier, CNIC verification with NADRA (L0 → L1, M5.5), and how much of the
 * per-transaction, daily and monthly limits is used (M5.3). Limits apply to every payment the wallet makes.
 */
export default function Account() {
  const { user } = useSession();
  const kyc = useQuery({ queryKey: identityKeys.kyc, queryFn: identityApi.kyc });
  const limits = useQuery({ queryKey: identityKeys.limits, queryFn: identityApi.limits, refetchInterval: 30_000 });

  return (
    <div className="space-y-5">
      <Card className="space-y-4">
        <div className="flex items-start justify-between gap-3">
          <div>
            <h1 className="text-xl font-bold">Account</h1>
            {user?.phoneNumber && <p className="mt-1 text-sm text-ink-600">Wallet {formatLocal(user.phoneNumber)}</p>}
          </div>
          {kyc.data && <TierBadge status={kyc.data} />}
        </div>
        {kyc.error && <Alert>{kyc.error.message}</Alert>}
        {kyc.data?.verified && <Verified status={kyc.data} />}
        {kyc.data && !kyc.data.verified && <VerifyCnic />}
      </Card>

      <Card className="space-y-4">
        <div>
          <h2 className="text-lg font-semibold">Limits</h2>
          <p className="mt-1 text-sm text-ink-600">What you can send, pay and withdraw. Daily limits reset at midnight, monthly ones on the 1st.</p>
        </div>
        {limits.error && <Alert>{limits.error.message}</Alert>}
        {limits.data && (
          <>
            {limits.data.coolingOffUntil && (
              <Alert tone="info">
                <span className="inline-flex items-center gap-1.5 font-medium">
                  <Clock className="h-4 w-4" aria-hidden /> New device
                </span>
                <br />
                Single payments from this device are capped until {formatDay(limits.data.coolingOffUntil)}, {formatTime(limits.data.coolingOffUntil)}.
              </Alert>
            )}
            <dl className="space-y-2 rounded-xl bg-ink-50 p-4 text-sm">
              <Row label="Tier" value={tierNames[limits.data.kycTier]} />
              <Row label="Largest single payment" value={formatMoney(limits.data.perTransaction)} />
              <Row label="Highest balance" value={formatMoney(limits.data.maxBalance)} />
            </dl>
            <UsageBar label="Today" usage={limits.data.daily} />
            <UsageBar label="This month" usage={limits.data.monthly} />
            {limits.data.kycTier === "L0" && (
              <p className="text-sm text-ink-600">Verify your CNIC above to raise these limits.</p>
            )}
          </>
        )}
      </Card>

      {devToolsEnabled && <DevRole />}
    </div>
  );
}

/** Development only: switch role to try the operations portal (/ops). The services refuse it outside Development. */
function DevRole() {
  const { user, refresh } = useSession();
  const [role, setRole] = useState(user?.roles[0] ?? "customer");
  const change = useMutation({
    mutationFn: async (next: string) => {
      await identityApi.devRole(next);
      await refresh();
    },
  });
  return (
    <Card className="space-y-3 border-dashed">
      <div>
        <h2 className="font-semibold">Developer: role</h2>
        <p className="text-sm text-ink-600">Staff roles open the operations portal at /ops. Compliance and admin also see fraud; finance and admin work reconciliation breaks.</p>
      </div>
      <div className="flex gap-2">
        <select value={role} onChange={(event) => setRole(event.target.value)} aria-label="Role" className="h-11 flex-1 rounded-xl border border-ink-200 bg-white px-3 text-sm">
          {Object.entries(roleNames).map(([value, label]) => (
            <option key={value} value={value}>
              {label}
            </option>
          ))}
        </select>
        <Button variant="outline" onClick={() => change.mutate(role)} loading={change.isPending}>
          Switch
        </Button>
      </div>
      {change.error && <Alert>{change.error.message}</Alert>}
      {change.isSuccess && <Alert tone="success">You are now {roleNames[role] ?? role}.</Alert>}
    </Card>
  );
}

function TierBadge({ status }: { status: KycStatus }) {
  return (
    <span
      className={cn(
        "inline-flex items-center gap-1 rounded-full px-2.5 py-1 text-xs font-semibold",
        status.verified ? "bg-brand-50 text-brand-800" : "bg-amber-50 text-amber-800",
      )}
    >
      {status.verified ? <BadgeCheck className="h-3.5 w-3.5" aria-hidden /> : <ShieldAlert className="h-3.5 w-3.5" aria-hidden />}
      {tierNames[status.kycTier]}
    </span>
  );
}

function Verified({ status }: { status: KycStatus }) {
  return (
    <dl className="space-y-2 rounded-xl bg-ink-50 p-4 text-sm">
      <Row label="Name" value={status.fullName ?? "—"} />
      <Row label="CNIC" value={<span className="font-mono">{status.cnicMasked ?? "—"}</span>} />
      {status.verifiedAt && <Row label="Verified" value={`${formatDay(status.verifiedAt)}, ${formatTime(status.verifiedAt)}`} />}
    </dl>
  );
}

/** CNIC verification: NADRA checks the CNIC, name and date of birth; a match upgrades the wallet to L1. */
function VerifyCnic() {
  const { refresh } = useSession();
  const queryClient = useQueryClient();
  const [cnic, setCnic] = useState("");
  const [fullName, setFullName] = useState("");
  const [dateOfBirth, setDateOfBirth] = useState("");
  const [error, setError] = useState<string | null>(null);

  const submit = useMutation({
    mutationFn: identityApi.submitKyc,
    onSuccess: async (status) => {
      queryClient.setQueryData(identityKeys.kyc, status);
      await queryClient.invalidateQueries({ queryKey: identityKeys.limits });
      // The tier is in the access token: get a new one so every service sees L1 straight away.
      await refresh().catch(() => undefined);
    },
  });

  function verify(event: FormEvent) {
    event.preventDefault();
    const digits = normalizeCnic(cnic);
    if (!digits) return setError("Enter the 13-digit CNIC number, for example 35202-1234567-1.");
    if (fullName.trim().length < 3) return setError("Enter your full name exactly as on the CNIC.");
    const born = checkDateOfBirth(dateOfBirth);
    if (!born.ok) return setError(born.reason);
    setError(null);
    submit.mutate({ cnic: formatCnic(digits), fullName: fullName.trim(), dateOfBirth });
  }

  return (
    <form onSubmit={verify} className="space-y-4 rounded-xl border border-amber-200 bg-amber-50/40 p-4" noValidate>
      <div className="flex items-start gap-3">
        <IdCard className="mt-0.5 h-5 w-5 shrink-0 text-amber-700" aria-hidden />
        <div>
          <p className="font-semibold">Verify your CNIC</p>
          <p className="text-sm text-ink-600">We check it with NADRA. Verified wallets get higher limits.</p>
        </div>
      </div>
      <Field label="CNIC number" htmlFor="cnic">
        <Input id="cnic" inputMode="numeric" autoComplete="off" placeholder="35202-1234567-1" value={cnic} onChange={(event) => setCnic(formatCnic(event.target.value))} className="font-mono" />
      </Field>
      <Field label="Full name (as on the CNIC)" htmlFor="full-name">
        <Input id="full-name" autoComplete="name" value={fullName} onChange={(event) => setFullName(event.target.value)} />
      </Field>
      <Field label="Date of birth" htmlFor="dob">
        <Input id="dob" type="date" value={dateOfBirth} onChange={(event) => setDateOfBirth(event.target.value)} />
      </Field>
      {error && <Alert>{error}</Alert>}
      {submit.error && <Alert>{submit.error.message}</Alert>}
      <Button type="submit" className="w-full" loading={submit.isPending}>
        Verify
      </Button>
    </form>
  );
}

function UsageBar({ label, usage }: { label: string; usage: LimitUsage }) {
  const percent = usedPercent(usage);
  return (
    <div className="space-y-1.5">
      <div className="flex items-baseline justify-between text-sm">
        <span className="font-medium text-ink-800">{label}</span>
        <span className="tabular text-ink-600">
          {formatMoney(usage.used)} of {formatMoney(usage.limit)}
        </span>
      </div>
      <div className="h-2.5 overflow-hidden rounded-full bg-ink-100" role="progressbar" aria-label={`${label}: ${percent}% used`} aria-valuenow={percent} aria-valuemin={0} aria-valuemax={100}>
        <div className={cn("h-full rounded-full", percent >= 90 ? "bg-red-500" : percent >= 70 ? "bg-amber-500" : "bg-brand-500")} style={{ width: `${percent}%` }} />
      </div>
      <p className="tabular text-xs text-ink-500">{formatMoney(usage.remaining)} left</p>
    </div>
  );
}

function Row({ label, value }: { label: string; value: React.ReactNode }) {
  return (
    <div className="flex items-center justify-between gap-4">
      <dt className="text-ink-600">{label}</dt>
      <dd className="tabular text-right text-ink-900">{value}</dd>
    </div>
  );
}
