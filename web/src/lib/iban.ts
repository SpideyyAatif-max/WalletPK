/**
 * Pakistani IBANs: "PK" + 2 check digits + 4-letter bank code + 16 digits (24 characters). Checked here
 * too (ISO 7064 mod 97), so a typo is caught before anything is sent; the server checks again.
 */
export function normalizeIban(input: string): string | null {
  const iban = input.replace(/[\s-]/g, "").toUpperCase();
  if (!/^PK\d{2}[A-Z]{4}\d{16}$/.test(iban)) return null;
  const rearranged = iban.slice(4) + iban.slice(0, 4);
  let remainder = 0;
  for (const char of rearranged) {
    const value = /[A-Z]/.test(char) ? char.charCodeAt(0) - 55 : Number(char);
    remainder = Number(`${remainder}${value}`) % 97;
  }
  return remainder === 1 ? iban : null;
}

/** "PK36SCBL0000001123456702" → "PK36 SCBL 0000 0011 2345 6702". */
export function formatIban(iban: string): string {
  return iban.replace(/(.{4})/g, "$1 ").trim();
}
