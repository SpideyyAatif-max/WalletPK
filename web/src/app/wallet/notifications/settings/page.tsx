"use client";

import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { ArrowLeft, Mail, MessageSquare } from "lucide-react";
import Link from "next/link";
import { useState, type FormEvent } from "react";
import { Button } from "@/components/ui/button";
import { Alert, Card } from "@/components/ui/card";
import { Field, Input } from "@/components/ui/input";
import { ApiError } from "@/lib/api";
import { formatDay, formatTime } from "@/lib/dates";
import { notificationKeys, notificationsApi, type Language, type NotificationSettings } from "@/lib/notifications";
import { formatLocal } from "@/lib/phone";
import { cn } from "@/lib/utils";

const languages: { value: Language; label: string; hint: string }[] = [
  { value: "en", label: "English", hint: "Notifications in English" },
  { value: "ur", label: "اردو", hint: "Notifications in Urdu" },
];

/**
 * Notification settings (M11.3, M11.4): the language messages are written in, and whether payments are
 * texted and emailed. Security messages (a new device, a wallet put on hold) always come by SMS, and by
 * email when there is an address.
 */
export default function NotificationSettingsPage() {
  const settings = useQuery({ queryKey: notificationKeys.settings, queryFn: notificationsApi.settings });

  return (
    <div className="space-y-4">
      <div className="flex items-center gap-2">
        <Link href="/wallet/notifications" className="grid h-9 w-9 place-items-center rounded-full text-ink-600 hover:bg-ink-100" aria-label="Back to notifications">
          <ArrowLeft className="h-4.5 w-4.5" aria-hidden />
        </Link>
        <h1 className="text-xl font-semibold">Notification settings</h1>
      </div>
      {settings.isError && <Alert>{settings.error.message}</Alert>}
      {settings.data && <SettingsForm settings={settings.data} />}
      <SentMessages />
    </div>
  );
}

function SettingsForm({ settings }: { settings: NotificationSettings }) {
  const queryClient = useQueryClient();
  const [email, setEmail] = useState(settings.email ?? "");
  const save = useMutation({
    mutationFn: notificationsApi.updateSettings,
    onSuccess: (saved, changes) => {
      queryClient.setQueryData(notificationKeys.settings, saved);
      // The notification centre is written in the chosen language.
      if (changes.language) void queryClient.invalidateQueries({ queryKey: notificationKeys.list });
    },
  });

  function saveEmail(event: FormEvent) {
    event.preventDefault();
    save.mutate({ email: email.trim() });
  }

  const emailError = save.error instanceof ApiError && save.error.code === "notifications.invalid_email" ? save.error.message : null;

  return (
    <>
      <Card className="space-y-3">
        <h2 className="font-semibold">Language</h2>
        <div className="grid grid-cols-2 gap-3" role="radiogroup" aria-label="Notification language">
          {languages.map((language) => (
            <button
              key={language.value}
              type="button"
              role="radio"
              aria-checked={settings.language === language.value}
              onClick={() => settings.language !== language.value && save.mutate({ language: language.value })}
              className={cn(
                "rounded-xl border px-4 py-3 text-left",
                settings.language === language.value ? "border-brand-600 bg-brand-50" : "border-ink-200 hover:bg-ink-50",
              )}
            >
              <span lang={language.value} className="block text-base font-semibold">{language.label}</span>
              <span className="text-xs text-ink-500">{language.hint}</span>
            </button>
          ))}
        </div>
      </Card>

      <Card className="space-y-4">
        <h2 className="font-semibold">Where they go</h2>
        <Toggle
          icon={<MessageSquare className="h-5 w-5" aria-hidden />}
          label="Text messages"
          hint={settings.phoneNumber ? `To ${formatLocal(settings.phoneNumber)}, for payments, requests and bills` : "For payments, requests and bills"}
          checked={settings.smsEnabled}
          onChange={(value) => save.mutate({ smsEnabled: value })}
        />
        <Toggle
          icon={<Mail className="h-5 w-5" aria-hidden />}
          label="Emails"
          hint={settings.email ? `To ${settings.email}` : "Add an address below to get emails"}
          checked={settings.emailEnabled}
          onChange={(value) => save.mutate({ emailEnabled: value })}
        />
        <form onSubmit={saveEmail} className="space-y-2" noValidate>
          <Field label="Email address" htmlFor="email" hint="Leave empty to stop all emails." error={emailError}>
            <Input id="email" type="email" inputMode="email" autoComplete="email" placeholder="you@example.com" value={email} onChange={(event) => setEmail(event.target.value)} />
          </Field>
          <Button type="submit" variant="outline" size="sm" loading={save.isPending && save.variables?.email !== undefined} disabled={email.trim() === (settings.email ?? "")}>
            Save address
          </Button>
        </form>
        <p className="text-xs text-ink-500">Security messages, like a new device signing in or your wallet being put on hold, are always sent.</p>
        {save.error && !emailError && <Alert>{save.error.message}</Alert>}
      </Card>
    </>
  );
}

function Toggle({ icon, label, hint, checked, onChange }: { icon: React.ReactNode; label: string; hint: string; checked: boolean; onChange: (value: boolean) => void }) {
  return (
    <div className="flex items-center gap-3">
      <span className="text-ink-500">{icon}</span>
      <span className="min-w-0 flex-1">
        <span className="block text-sm font-medium">{label}</span>
        <span className="block text-xs text-ink-500">{hint}</span>
      </span>
      <button
        type="button"
        role="switch"
        aria-checked={checked}
        aria-label={label}
        onClick={() => onChange(!checked)}
        className={cn("relative h-6 w-11 shrink-0 rounded-full transition-colors", checked ? "bg-brand-600" : "bg-ink-300")}
      >
        <span className={cn("absolute top-0.5 h-5 w-5 rounded-full bg-white shadow transition-transform", checked ? "translate-x-5.5" : "translate-x-0.5")} />
      </button>
    </div>
  );
}

/** Development only: the texts and emails the simulated gateways received for you. Hidden elsewhere. */
function SentMessages() {
  const sent = useQuery({ queryKey: notificationKeys.sent, queryFn: notificationsApi.sent, retry: false, refetchInterval: 5_000 });
  if (!sent.data) return null;

  return (
    <Card className="space-y-3 border border-dashed border-amber-300 bg-amber-50/50">
      <h2 className="text-sm font-semibold text-amber-900">Development: messages sent to you</h2>
      {sent.data.length === 0 && <p className="text-xs text-ink-500">None yet.</p>}
      <ul className="space-y-2">
        {sent.data.slice(0, 10).map((message) => (
          <li key={message.id} className="rounded-lg bg-white px-3 py-2 text-xs">
            <div className="flex justify-between gap-2 text-ink-500">
              <span>
                {message.channel === "Sms" ? "SMS" : "Email"} to {message.to} · {message.status}
                {message.attempts > 1 ? ` after ${message.attempts} tries` : ""}
              </span>
              <span>{formatDay(message.createdAt)}, {formatTime(message.createdAt)}</span>
            </div>
            {message.subject && <p dir="auto" className="mt-1 font-medium">{message.subject}</p>}
            <p dir="auto" className="mt-1 whitespace-pre-line text-ink-800">{message.body}</p>
          </li>
        ))}
      </ul>
    </Card>
  );
}
