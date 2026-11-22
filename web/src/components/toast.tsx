"use client";

import { CheckCircle2, X } from "lucide-react";
import { createContext, useCallback, useContext, useMemo, useState, type ReactNode } from "react";

type Toast = { id: number; title: string; body?: string };

const ToastContext = createContext<((title: string, body?: string) => void) | null>(null);

let nextId = 1;

/** Small pop-ups at the top, for notifications that arrive while you look at the app. */
export function ToastProvider({ children }: { children: ReactNode }) {
  const [toasts, setToasts] = useState<Toast[]>([]);

  const dismiss = useCallback((id: number) => setToasts((all) => all.filter((toast) => toast.id !== id)), []);
  const show = useCallback(
    (title: string, body?: string) => {
      const id = nextId++;
      setToasts((all) => [...all.slice(-2), { id, title, body }]);
      setTimeout(() => dismiss(id), 5_000);
    },
    [dismiss],
  );

  const value = useMemo(() => show, [show]);

  return (
    <ToastContext.Provider value={value}>
      {children}
      <div aria-live="polite" className="pointer-events-none fixed inset-x-0 top-3 z-50 flex flex-col items-center gap-2 px-3">
        {toasts.map((toast) => (
          <div key={toast.id} className="pointer-events-auto flex w-full max-w-sm items-start gap-3 rounded-2xl bg-ink-900 px-4 py-3 text-white shadow-lg">
            <CheckCircle2 className="mt-0.5 h-5 w-5 shrink-0 text-brand-400" aria-hidden />
            <div className="min-w-0 flex-1">
              <p dir="auto" className="text-sm font-semibold">{toast.title}</p>
              {toast.body && <p dir="auto" className="text-sm text-ink-300">{toast.body}</p>}
            </div>
            <button onClick={() => dismiss(toast.id)} className="text-ink-400 hover:text-white" aria-label="Dismiss">
              <X className="h-4 w-4" />
            </button>
          </div>
        ))}
      </div>
    </ToastContext.Provider>
  );
}

export function useToast() {
  const show = useContext(ToastContext);
  if (!show) throw new Error("useToast must be used inside <ToastProvider>.");
  return show;
}
