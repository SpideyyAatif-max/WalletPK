/** Money as the API sends it: an integer number of paisa (Rs. 1 = 100 paisa). */
export type Money = { minorUnits: number; currency: string };

const rupees = new Intl.NumberFormat("en-PK", { minimumFractionDigits: 2, maximumFractionDigits: 2 });

/** 150050 → "Rs. 1,500.50". With `signed`, a "+" or "−" in front (for statement lines). */
export function formatPaisa(paisa: number, options: { signed?: "in" | "out" } = {}): string {
  const text = `Rs. ${rupees.format(Math.abs(paisa) / 100)}`;
  if (options.signed === "in") return `+ ${text}`;
  if (options.signed === "out") return `− ${text}`;
  return paisa < 0 ? `− ${text}` : text;
}

export function formatMoney(money: Money, options: { signed?: "in" | "out" } = {}): string {
  return formatPaisa(money.minorUnits, options);
}

/**
 * What someone types in an amount box → paisa, or null if it is not a valid amount.
 * Accepts "1500", "1,500", "1500.5", "Rs 1,500.50"; at most two decimals; must be above zero.
 */
export function parseRupees(input: string): number | null {
  const cleaned = input.trim().replace(/^rs\.?\s*/i, "").replace(/,/g, "");
  if (!/^\d{1,9}(\.\d{1,2})?$/.test(cleaned)) return null;
  const [whole = "0", fraction = ""] = cleaned.split(".");
  const paisa = Number(whole) * 100 + Number(fraction.padEnd(2, "0"));
  return paisa > 0 ? paisa : null;
}
