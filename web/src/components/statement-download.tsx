"use client";

import { useMutation } from "@tanstack/react-query";
import { FileDown } from "lucide-react";
import { useState } from "react";
import { Button } from "@/components/ui/button";
import { Alert, Card } from "@/components/ui/card";
import { recentMonths, statementsApi } from "@/lib/reports";

/** Monthly statements as PDF (M12.5): pick a month, the Ledger renders it with the opening and closing balance. */
export function StatementDownload() {
  const months = recentMonths(12);
  const [month, setMonth] = useState(months[0]?.id ?? "");
  const download = useMutation({ mutationFn: () => statementsApi.pdf(month) });

  return (
    <Card className="space-y-3 p-4">
      <div className="flex flex-wrap items-center gap-2">
        <div className="mr-auto">
          <p className="font-semibold">Statements</p>
          <p className="text-sm text-ink-600">A PDF of a month, Pakistan time, with opening and closing balances.</p>
        </div>
        <label className="sr-only" htmlFor="statement-month">
          Month
        </label>
        <select id="statement-month" value={month} onChange={(event) => setMonth(event.target.value)} className="h-9 rounded-full bg-white px-3 text-sm text-ink-700 ring-1 ring-ink-200">
          {months.map((option, index) => (
            <option key={option.id} value={option.id}>
              {option.label}
              {index === 0 ? " (so far)" : ""}
            </option>
          ))}
        </select>
        <Button size="sm" variant="outline" onClick={() => download.mutate()} loading={download.isPending}>
          <FileDown className="h-4 w-4" aria-hidden /> Download PDF
        </Button>
      </div>
      {download.error && <Alert>{download.error.message}</Alert>}
      {download.isSuccess && (
        <Alert tone="success">Saved {download.data}.</Alert>
      )}
    </Card>
  );
}
