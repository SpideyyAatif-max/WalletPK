import { ArrowDownLeft, ArrowUpRight, Banknote } from "lucide-react";
import { dayKey, formatDay, formatTime } from "@/lib/dates";
import { formatMoney } from "@/lib/money";
import type { StatementLine } from "@/lib/types";
import { cn } from "@/lib/utils";

function Icon({ line }: { line: StatementLine }) {
  if (line.type === "CashIn" || line.type === "CashOut") {
    return <Banknote className="h-5 w-5" aria-hidden />;
  }
  return line.direction === "In" ? <ArrowDownLeft className="h-5 w-5" aria-hidden /> : <ArrowUpRight className="h-5 w-5" aria-hidden />;
}

function Row({ line, fresh }: { line: StatementLine; fresh: boolean }) {
  const incoming = line.direction === "In";
  return (
    <li className={cn("flex items-center gap-3 px-1 py-3", fresh && "flash rounded-xl")}>
      <span className={cn("grid h-10 w-10 shrink-0 place-items-center rounded-full", incoming ? "bg-brand-50 text-brand-700" : "bg-ink-100 text-ink-700")}>
        <Icon line={line} />
      </span>
      <div className="min-w-0 flex-1">
        <p className="truncate text-sm font-medium text-ink-900">{line.title}</p>
        <p className="text-xs text-ink-500">
          {formatTime(line.postedAt)} · Balance {formatMoney(line.balanceAfter)}
        </p>
      </div>
      <p className={cn("tabular shrink-0 text-sm font-semibold", incoming ? "text-brand-700" : "text-ink-900")}>
        {formatMoney(line.amount, { signed: incoming ? "in" : "out" })}
      </p>
    </li>
  );
}

/** Statement lines, newest first, grouped by day. `freshSequences` flash briefly (they just arrived live). */
export function TransactionList({ lines, freshSequences = new Set<number>(), grouped = true }: { lines: StatementLine[]; freshSequences?: Set<number>; grouped?: boolean }) {
  if (!grouped) {
    return (
      <ul className="divide-y divide-ink-100">
        {lines.map((line) => (
          <Row key={line.sequence} line={line} fresh={freshSequences.has(line.sequence)} />
        ))}
      </ul>
    );
  }

  const groups: { key: string; label: string; lines: StatementLine[] }[] = [];
  for (const line of lines) {
    const groupKey = dayKey(line.postedAt);
    const last = groups.at(-1);
    if (last?.key === groupKey) last.lines.push(line);
    else groups.push({ key: groupKey, label: formatDay(line.postedAt), lines: [line] });
  }

  return (
    <div className="space-y-4">
      {groups.map((group) => (
        <section key={group.key}>
          <h3 className="px-1 text-xs font-semibold tracking-wide text-ink-500 uppercase">{group.label}</h3>
          <ul className="divide-y divide-ink-100">
            {group.lines.map((line) => (
              <Row key={line.sequence} line={line} fresh={freshSequences.has(line.sequence)} />
            ))}
          </ul>
        </section>
      ))}
    </div>
  );
}
