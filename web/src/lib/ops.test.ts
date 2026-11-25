import { describe, expect, it } from "vitest";
import { sagaQuery, summarizeCounts, traceLink, type SagaStateCount } from "./ops";
import { isCompliance, isStaff } from "./roles";
import { addEvent, type OpsEvent } from "./use-ops-hub";

const count = (state: string, isCompleted: boolean, n: number): SagaStateCount =>
  ({ type: "p2p-transfer", state, isCompleted, count: n, oldestUpdatedAt: "2026-11-26T00:00:00Z" }) as SagaStateCount;

describe("operations portal", () => {
  it("lets staff in, and compliance only to fraud", () => {
    expect(isStaff({ roles: ["ops"] })).toBe(true);
    expect(isStaff({ roles: ["finance"] })).toBe(true);
    expect(isStaff({ roles: ["customer"] })).toBe(false);
    expect(isStaff(null)).toBe(false);
    expect(isCompliance({ roles: ["ops"] })).toBe(false);
    expect(isCompliance({ roles: ["compliance"] })).toBe(true);
    expect(isCompliance({ roles: ["admin"] })).toBe(true);
  });

  it("builds Saga searches from the filters that are set", () => {
    expect(sagaQuery({})).toBe("");
    expect(sagaQuery({ type: "bill-payment", state: "", active: true, stuckForMinutes: 15, userId: undefined })).toBe("?type=bill-payment&active=true&stuckForMinutes=15");
    expect(traceLink("0af7651916cd43dd8448eb211c80319c")).toBe("http://localhost:16686/trace/0af7651916cd43dd8448eb211c80319c");
  });

  it("sums Saga counts for the dashboard", () => {
    expect(summarizeCounts([count("Sending", false, 3), count("DeadLettered", false, 2), count("Completed", true, 10), count("Failed", true, 4)])).toEqual({
      running: 5,
      deadLettered: 2,
      finished: 14,
      failed: 4,
    });
  });

  it("keeps the feed newest first, once each, and bounded", () => {
    const event = (id: string): OpsEvent => ({ id, kind: "FraudAlert", severity: "High", title: id, detail: null, userId: null, at: "2026-11-26T00:00:00Z" });
    let events: OpsEvent[] = [];
    events = addEvent(events, event("a"));
    events = addEvent(events, event("b"));
    events = addEvent(events, event("a"));
    expect(events.map((e) => e.id)).toEqual(["b", "a"]);
    for (let i = 0; i < 5; i++) events = addEvent(events, event(`x${i}`), 3);
    expect(events).toHaveLength(3);
  });
});
