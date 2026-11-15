// Times are shown in Pakistan time, whatever the browser's time zone.
const zone = "Asia/Karachi";

const time = new Intl.DateTimeFormat("en-PK", { timeZone: zone, hour: "numeric", minute: "2-digit" });
const day = new Intl.DateTimeFormat("en-PK", { timeZone: zone, day: "numeric", month: "short", year: "numeric" });
const key = new Intl.DateTimeFormat("en-CA", { timeZone: zone, year: "numeric", month: "2-digit", day: "2-digit" });

/** "3:45 pm". */
export function formatTime(iso: string): string {
  return time.format(new Date(iso));
}

/** "Today", "Yesterday" or "12 Nov 2026", relative to `now`. */
export function formatDay(iso: string, now: Date = new Date()): string {
  const date = new Date(iso);
  const today = key.format(now);
  const yesterday = key.format(new Date(now.getTime() - 86_400_000));
  const that = key.format(date);
  if (that === today) return "Today";
  if (that === yesterday) return "Yesterday";
  return day.format(date);
}

/** The calendar day in Pakistan, for grouping ("2026-11-15"). */
export function dayKey(iso: string): string {
  return key.format(new Date(iso));
}
