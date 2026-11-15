import type { ReactNode } from "react";
import { Brand } from "@/components/brand";

export default function AuthLayout({ children }: { children: ReactNode }) {
  return (
    <main className="flex min-h-dvh flex-col bg-gradient-to-b from-brand-700 to-brand-900">
      <header className="px-6 pt-8 pb-10 text-center">
        <Brand inverted className="text-xl" />
        <p className="mt-2 text-sm text-brand-100">Your money, instantly, anywhere in Pakistan.</p>
      </header>
      <div className="flex-1 rounded-t-3xl bg-ink-50 px-5 pt-8 pb-10">
        <div className="mx-auto w-full max-w-sm">{children}</div>
      </div>
    </main>
  );
}
