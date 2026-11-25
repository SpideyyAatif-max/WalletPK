"use client";

import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useState } from "react";
import { ComplianceOnly, PageHeader, When } from "@/components/ops/ui";
import { Button } from "@/components/ui/button";
import { Alert, Card } from "@/components/ui/card";
import { opsApi, opsKeys, type FraudRule, type RuleTestResult } from "@/lib/ops";
import { cn } from "@/lib/utils";

export default function RulesPage() {
  return (
    <ComplianceOnly>
      <Rules />
    </ComplianceOnly>
  );
}

/** JSON text → value, or the parse error to show. */
function parseJson(text: string): { ok: true; value: unknown } | { ok: false; error: string } {
  try {
    return { ok: true, value: JSON.parse(text) };
  } catch (error) {
    return { ok: false, error: error instanceof Error ? error.message : "Not valid JSON." };
  }
}

const sampleFeatures = `{
  "amount_paisa": 6000000,
  "transaction_type": "P2PTransfer",
  "hour_pkt": 2,
  "account_age_hours": 30,
  "recipient_known": false,
  "count_1h": 4
}`;

/**
 * Rule editor (M14.C4, M10.2): the fraud engine's rules as stored, switched on and off, edited as JSON and tried
 * on sample values before saving. Every change is versioned and written to the audit log.
 */
function Rules() {
  const rules = useQuery({ queryKey: opsKeys.rules, queryFn: opsApi.rules });
  const features = useQuery({ queryKey: ["ops", "fraud", "features"], queryFn: opsApi.features, staleTime: Infinity });
  const [openId, setOpenId] = useState<string | null>(null);

  return (
    <div>
      <PageHeader title="Rules" description="What the fraud engine scores. Changes apply within seconds, are versioned, and are written to the audit log." />
      {rules.error && <Alert className="mb-4">{rules.error.message}</Alert>}
      <div className="grid gap-4 xl:grid-cols-[minmax(0,3fr)_minmax(0,2fr)]">
        <div className="space-y-3">
          {rules.data?.map((rule) => <RuleCard key={rule.id} rule={rule} open={openId === rule.id} onToggleOpen={() => setOpenId(openId === rule.id ? null : rule.id)} />)}
        </div>
        <div className="space-y-4">
          <Tester />
          {features.data && (
            <Card className="p-4">
              <h2 className="mb-2 text-sm font-semibold">What rules can test</h2>
              <ul className="space-y-1.5 text-xs">
                {features.data.map((feature) => (
                  <li key={feature.name}>
                    <span className="font-mono font-semibold">{feature.name}</span> <span className="text-ink-400">({feature.kind})</span>
                    <span className="block text-ink-600">{feature.description}</span>
                  </li>
                ))}
              </ul>
            </Card>
          )}
        </div>
      </div>
    </div>
  );
}

function RuleCard({ rule, open, onToggleOpen }: { rule: FraudRule; open: boolean; onToggleOpen: () => void }) {
  const queryClient = useQueryClient();
  const [text, setText] = useState(() => pretty(rule.definition));
  const [problem, setProblem] = useState<string | null>(null);

  const save = useMutation({
    mutationFn: (body: { definition?: unknown; enabled?: boolean }) => opsApi.updateRule(rule.id, body),
    onSuccess: (updated) => {
      setText(pretty(updated.definition));
      void queryClient.invalidateQueries({ queryKey: opsKeys.rules });
    },
  });

  function saveDefinition() {
    const parsed = parseJson(text);
    if (!parsed.ok) return setProblem(parsed.error);
    setProblem(null);
    save.mutate({ definition: parsed.value });
  }

  return (
    <Card className={cn("p-4", !rule.enabled && "opacity-70")}>
      <div className="flex items-start justify-between gap-3">
        <button onClick={onToggleOpen} className="min-w-0 text-left">
          <p className="font-semibold">
            {rule.name} <span className="font-mono text-xs font-normal text-ink-500">{rule.id}</span>
          </p>
          <p className="text-sm text-ink-600">{rule.description}</p>
          <p className="mt-1 text-xs text-ink-400">
            v{rule.version} · {rule.builtIn ? "built in" : "custom"} · {rule.updatedBy}, <When at={rule.updatedAt} />
          </p>
        </button>
        <label className="flex shrink-0 cursor-pointer items-center gap-2 text-sm">
          <input type="checkbox" checked={rule.enabled} disabled={save.isPending} onChange={(event) => save.mutate({ enabled: event.target.checked })} aria-label={`${rule.name} on`} />
          {rule.enabled ? "On" : "Off"}
        </label>
      </div>
      {open && (
        <div className="mt-3 space-y-2">
          <textarea
            value={text}
            onChange={(event) => setText(event.target.value)}
            spellCheck={false}
            rows={Math.min(18, text.split("\n").length + 1)}
            className="w-full rounded-xl border border-ink-200 bg-ink-900 p-3 font-mono text-xs text-ink-100 focus:ring-2 focus:ring-brand-500/40 focus:outline-none"
            aria-label={`${rule.name} definition`}
          />
          {problem && <Alert>{problem}</Alert>}
          {save.error && <Alert>{save.error.message}</Alert>}
          {save.isSuccess && <Alert tone="success">Saved as version {save.data.version}.</Alert>}
          <div className="flex gap-2">
            <Button size="sm" onClick={saveDefinition} loading={save.isPending}>
              Save
            </Button>
            <Button size="sm" variant="outline" onClick={() => setText(pretty(rule.definition))}>
              Undo changes
            </Button>
          </div>
        </div>
      )}
    </Card>
  );
}

/** Tries the rules in force on some feature values (nothing is saved or scored for real). */
function Tester() {
  const [text, setText] = useState(sampleFeatures);
  const [problem, setProblem] = useState<string | null>(null);
  const test = useMutation({ mutationFn: (features: Record<string, unknown>) => opsApi.testRule({ features }) });

  function run() {
    const parsed = parseJson(text);
    if (!parsed.ok || typeof parsed.value !== "object" || parsed.value === null || Array.isArray(parsed.value)) {
      return setProblem(parsed.ok ? "Give an object of feature values." : parsed.error);
    }
    setProblem(null);
    test.mutate(parsed.value as Record<string, unknown>);
  }

  const result: RuleTestResult | undefined = test.data;
  return (
    <Card className="space-y-3 p-4">
      <h2 className="text-sm font-semibold">Try the rules</h2>
      <textarea
        value={text}
        onChange={(event) => setText(event.target.value)}
        spellCheck={false}
        rows={8}
        className="w-full rounded-xl border border-ink-200 bg-ink-900 p-3 font-mono text-xs text-ink-100 focus:ring-2 focus:ring-brand-500/40 focus:outline-none"
        aria-label="Feature values"
      />
      {problem && <Alert>{problem}</Alert>}
      {test.error && <Alert>{test.error.message}</Alert>}
      <Button size="sm" onClick={run} loading={test.isPending}>
        Score
      </Button>
      {result && (
        <div className="rounded-xl bg-ink-50 p-3 text-sm">
          <p>
            Score <span className="tabular font-bold">{result.score}</span> → <span className="font-semibold">{result.action}</span>
          </p>
          <p className="text-ink-600">{result.rules.length ? result.rules.join(", ") : "No rule matched."}</p>
          {result.reason && <p className="text-xs text-ink-500">{result.reason}</p>}
        </div>
      )}
    </Card>
  );
}

function pretty(definition: string): string {
  const parsed = parseJson(definition);
  return parsed.ok ? JSON.stringify(parsed.value, null, 2) : definition;
}
