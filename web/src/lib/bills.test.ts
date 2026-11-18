import { describe, expect, it } from "vitest";
import { cleanConsumerNumber, daysUntil, dueLabel, isBillFinal, isConsumerNumber } from "./bills";

describe("consumer numbers", () => {
  it("accept the biller's length with spaces or dashes", () => {
    expect(isConsumerNumber({ consumerNumberLength: 13 }, "04000 1234 5678")).toBe(true);
    expect(isConsumerNumber({ consumerNumberLength: 13 }, "0400-0123-45678")).toBe(true);
    expect(cleanConsumerNumber("0400-0123 45678")).toBe("0400012345678");
  });

  it("refuse other lengths and letters", () => {
    expect(isConsumerNumber({ consumerNumberLength: 13 }, "040001234567")).toBe(false);
    expect(isConsumerNumber({ consumerNumberLength: 11 }, "0400012345A")).toBe(false);
  });
});

describe("due dates", () => {
  it("count days to the due date", () => {
    expect(daysUntil("2026-11-20", "2026-11-17")).toBe(3);
    expect(daysUntil("2026-11-20", "2026-11-22")).toBe(-2);
  });

  it("say it in words", () => {
    expect(dueLabel("2026-11-20", "2026-11-20")).toBe("Due today");
    expect(dueLabel("2026-11-20", "2026-11-19")).toBe("Due tomorrow");
    expect(dueLabel("2026-11-20", "2026-11-15")).toBe("Due in 5 days");
    expect(dueLabel("2026-11-20", "2026-11-21")).toBe("1 day overdue");
    expect(dueLabel("2026-11-20", "2026-11-25")).toBe("5 days overdue");
  });

  it("know which payments are final", () => {
    expect(isBillFinal("Completed")).toBe(true);
    expect(isBillFinal("PendingVerification")).toBe(false);
  });
});
