"use client";

import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { ArrowLeft, Camera, CheckCircle2, ChevronRight, Loader2, Store, XCircle } from "lucide-react";
import { useEffect, useRef, useState, useSyncExternalStore, type FormEvent } from "react";
import { StatusPill } from "@/components/payment-status";
import { Button } from "@/components/ui/button";
import { Alert, Card } from "@/components/ui/card";
import { Field } from "@/components/ui/input";
import { formatDay, formatTime } from "@/lib/dates";
import { countdown, isQrPayload, merchantKeys, merchantsApi, secondsLeft, type QrPreview } from "@/lib/merchants";
import { formatMoney, formatPaisa } from "@/lib/money";
import { queryKeys } from "@/lib/wallet-api";
import { useBalance } from "@/lib/wallet-queries";

/**
 * Scan & pay (M8.4): scan a shop's QR code (or paste it), see who and how much, pay. The code is signed,
 * so its amount cannot be changed, and it can be paid once, before it expires.
 */
export default function ScanAndPay() {
  const [openId, setOpenId] = useState<string | null>(null);
  return openId ? <Receipt id={openId} onBack={() => setOpenId(null)} /> : <Scan onPaid={setOpenId} />;
}

function Scan({ onPaid }: { onPaid: (id: string) => void }) {
  const balance = useBalance();
  const recent = useQuery({ queryKey: merchantKeys.payments, queryFn: () => merchantsApi.payments(5) });
  const [text, setText] = useState("");
  const [scanning, setScanning] = useState(false);
  const [preview, setPreview] = useState<{ payload: string; qr: QrPreview } | null>(null);

  const check = useMutation({
    mutationFn: (payload: string) => merchantsApi.preview(payload),
    onSuccess: (qr, payload) => setPreview({ payload, qr }),
  });
  const pay = useMutation({
    mutationFn: (payload: string) => merchantsApi.pay(payload),
    onSuccess: (payment) => onPaid(payment.id),
  });

  // A link such as /wallet/pay?qr=WPKQR1... opens straight on the payment.
  useEffect(() => {
    const fromLink = new URLSearchParams(window.location.search).get("qr");
    if (fromLink && isQrPayload(fromLink)) check.mutate(fromLink.trim());
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  function scanned(payload: string) {
    setScanning(false);
    setText(payload);
    check.mutate(payload.trim());
  }

  function submit(event: FormEvent) {
    event.preventDefault();
    if (!isQrPayload(text)) return;
    check.mutate(text.trim());
  }

  return (
    <div className="space-y-5">
      <Card className="space-y-5">
        <div>
          <h1 className="text-xl font-bold">Scan &amp; pay</h1>
          <p className="mt-1 text-sm text-ink-600">
            Pay a shop by its WalletPK QR code. Balance: <span className="tabular font-medium">{balance.data ? formatMoney(balance.data.balance) : "…"}</span>
          </p>
        </div>

        {preview ? (
          <Confirm
            preview={preview.qr}
            paying={pay.isPending}
            error={pay.error?.message}
            onPay={() => pay.mutate(preview.payload)}
            onCancel={() => {
              setPreview(null);
              setText("");
              pay.reset();
              check.reset();
            }}
          />
        ) : (
          <>
            {scanning ? <CameraScanner onScan={scanned} onClose={() => setScanning(false)} /> : <ScanButton onClick={() => setScanning(true)} />}
            <form onSubmit={submit} className="space-y-3" noValidate>
              <Field label="Or paste the code" htmlFor="payload">
                <textarea
                  id="payload"
                  rows={3}
                  value={text}
                  onChange={(event) => setText(event.target.value)}
                  placeholder="WPKQR1…"
                  className="w-full rounded-xl border border-ink-200 px-3 py-2 font-mono text-xs focus:border-brand-500 focus:ring-2 focus:ring-brand-100 focus:outline-none"
                />
              </Field>
              {text && !isQrPayload(text) && <p className="text-sm text-red-600">This is not a WalletPK QR code.</p>}
              {check.error && <Alert>{check.error.message}</Alert>}
              <Button type="submit" variant="secondary" className="w-full" loading={check.isPending} disabled={!isQrPayload(text)}>
                Continue
              </Button>
            </form>
          </>
        )}
      </Card>

      {recent.data && recent.data.length > 0 && (
        <Card className="p-2">
          <h2 className="px-3 pt-2 pb-1 text-sm font-semibold text-ink-700">Recent shop payments</h2>
          <ul>
            {recent.data.map((payment) => (
              <li key={payment.id}>
                <button onClick={() => onPaid(payment.id)} className="flex w-full items-center gap-3 rounded-xl px-3 py-2.5 text-left hover:bg-ink-50">
                  <span className="grid h-9 w-9 shrink-0 place-items-center rounded-full bg-brand-50 text-brand-700">
                    <Store className="h-5 w-5" aria-hidden />
                  </span>
                  <div className="min-w-0 flex-1">
                    <p className="truncate text-sm font-medium">{payment.merchantName}</p>
                    <p className="text-xs text-ink-500">
                      {payment.reference ? `${payment.reference} · ` : ""}
                      {formatDay(payment.requestedAt)}
                    </p>
                  </div>
                  <div className="text-right">
                    <p className="tabular text-sm font-semibold">{formatPaisa(payment.amountPaisa)}</p>
                    <StatusPill status={payment.status} />
                  </div>
                  <ChevronRight className="h-4 w-4 text-ink-400" aria-hidden />
                </button>
              </li>
            ))}
          </ul>
        </Card>
      )}
    </div>
  );
}

function ScanButton({ onClick }: { onClick: () => void }) {
  // false while rendering on the server; the browser's answer once in it.
  const supported = useSyncExternalStore(noSubscription, canScan, () => false);
  if (!supported) {
    return <Alert tone="info">This browser cannot scan QR codes. Paste the code below, or open this page in Chrome on your phone.</Alert>;
  }
  return (
    <Button size="lg" className="w-full" onClick={onClick}>
      <Camera className="h-5 w-5" aria-hidden /> Scan a QR code
    </Button>
  );
}

const noSubscription = () => () => {};

function canScan(): boolean {
  return "BarcodeDetector" in window && !!navigator.mediaDevices?.getUserMedia;
}

type Detector = { detect(source: HTMLVideoElement): Promise<{ rawValue: string }[]> };
type DetectorConstructor = new (options: { formats: string[] }) => Detector;

/** The camera, read by the browser's own QR detector until it finds a WalletPK code. */
function CameraScanner({ onScan, onClose }: { onScan: (payload: string) => void; onClose: () => void }) {
  const video = useRef<HTMLVideoElement>(null);
  const found = useRef(onScan);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    found.current = onScan;
  }, [onScan]);

  useEffect(() => {
    let stream: MediaStream | null = null;
    let stopped = false;
    const Constructor = (window as unknown as { BarcodeDetector: DetectorConstructor }).BarcodeDetector;
    const detector = new Constructor({ formats: ["qr_code"] });

    async function start() {
      try {
        stream = await navigator.mediaDevices.getUserMedia({ video: { facingMode: "environment" } });
        if (stopped || !video.current) return;
        video.current.srcObject = stream;
        await video.current.play();
        while (!stopped) {
          const codes = await detector.detect(video.current).catch(() => []);
          const code = codes.find((candidate) => isQrPayload(candidate.rawValue));
          if (code) return found.current(code.rawValue);
          await new Promise((resolve) => setTimeout(resolve, 250));
        }
      } catch {
        setError("The camera could not be opened. Allow camera access, or paste the code below.");
      }
    }

    void start();
    return () => {
      stopped = true;
      stream?.getTracks().forEach((track) => track.stop());
    };
  }, []);

  return (
    <div className="space-y-2">
      {error ? <Alert>{error}</Alert> : <video ref={video} className="aspect-square w-full rounded-xl bg-ink-900 object-cover" muted playsInline />}
      <Button variant="ghost" size="sm" onClick={onClose}>
        Stop scanning
      </Button>
    </div>
  );
}

function Confirm({ preview, paying, error, onPay, onCancel }: { preview: QrPreview; paying: boolean; error?: string; onPay: () => void; onCancel: () => void }) {
  const [left, setLeft] = useState(() => secondsLeft(preview.expiresAt));
  useEffect(() => {
    const timer = setInterval(() => setLeft(secondsLeft(preview.expiresAt)), 1_000);
    return () => clearInterval(timer);
  }, [preview.expiresAt]);

  return (
    <div className="space-y-4">
      <div className="space-y-1 rounded-xl bg-ink-50 p-4 text-center">
        <p className="text-sm text-ink-500">Pay</p>
        <p className="text-lg font-semibold">{preview.merchantName}</p>
        <p className="tabular text-3xl font-bold">{formatPaisa(preview.amountPaisa)}</p>
        {preview.reference && <p className="text-sm text-ink-600">Reference {preview.reference}</p>}
        <p className="text-xs text-ink-400">
          Merchant {preview.merchantCode} · {left > 0 ? `expires in ${countdown(left)}` : "expired"}
        </p>
      </div>
      {error && <Alert>{error}</Alert>}
      {left === 0 && <Alert>This code has expired. Ask the shop for a new one.</Alert>}
      <Button size="lg" className="w-full" onClick={onPay} loading={paying} disabled={left === 0}>
        Pay {formatPaisa(preview.amountPaisa)}
      </Button>
      <Button variant="ghost" className="w-full" onClick={onCancel}>
        Cancel
      </Button>
    </div>
  );
}

/** The payment, refreshed every second until the Ledger has answered. */
function Receipt({ id, onBack }: { id: string; onBack: () => void }) {
  const queryClient = useQueryClient();
  const payment = useQuery({
    queryKey: merchantKeys.payment(id),
    queryFn: async () => {
      const current = await merchantsApi.payment(id);
      if (current.status !== "Processing") {
        void queryClient.invalidateQueries({ queryKey: queryKeys.wallet });
        void queryClient.invalidateQueries({ queryKey: merchantKeys.payments });
      }
      return current;
    },
    refetchInterval: (query) => (query.state.data && query.state.data.status !== "Processing" ? false : 1_000),
  });

  const data = payment.data;
  return (
    <div className="space-y-4">
      <button onClick={onBack} className="inline-flex items-center gap-1 text-sm font-medium text-brand-700 hover:underline">
        <ArrowLeft className="h-4 w-4" aria-hidden /> Scan &amp; pay
      </button>
      {payment.error && <Alert>{payment.error.message}</Alert>}
      {data && (
        <Card className="space-y-5 text-center">
          <div className="flex justify-center">
            {data.status === "Completed" ? (
              <CheckCircle2 className="h-14 w-14 text-brand-600" aria-hidden />
            ) : data.status === "Failed" ? (
              <XCircle className="h-14 w-14 text-red-600" aria-hidden />
            ) : (
              <Loader2 className="h-14 w-14 animate-spin text-sky-600" aria-hidden />
            )}
          </div>
          <div>
            <p className="text-sm text-ink-500">{data.status === "Completed" ? "Paid" : data.status === "Failed" ? "Not paid" : "Paying"}</p>
            <p className="text-lg font-semibold">{data.merchantName}</p>
            <p className="tabular text-3xl font-bold">{formatPaisa(data.amountPaisa)}</p>
          </div>
          {data.status === "Failed" && <Alert>{data.failureReason}</Alert>}
          <dl className="space-y-2 rounded-xl bg-ink-50 p-4 text-left text-sm">
            {data.reference && <Row label="Reference" value={data.reference} />}
            <Row label="Merchant" value={data.merchantCode} />
            <Row label="When" value={`${formatDay(data.paidAt ?? data.requestedAt)}, ${formatTime(data.paidAt ?? data.requestedAt)}`} />
            <Row label="Payment ID" value={<span className="font-mono text-xs">{data.id}</span>} />
          </dl>
        </Card>
      )}
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
