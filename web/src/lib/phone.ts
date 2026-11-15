/**
 * Pakistani mobile numbers. People type "0300 1234567", "0300-1234567", "+92 300 1234567" or
 * "923001234567"; the API stores E.164 ("+923001234567").
 */
export function toE164(input: string): string | null {
  const digits = input.replace(/[\s\-().]/g, "");
  let national: string | null = null;
  if (/^03\d{9}$/.test(digits)) national = digits.slice(1);
  else if (/^\+923\d{9}$/.test(digits)) national = digits.slice(3);
  else if (/^923\d{9}$/.test(digits)) national = digits.slice(2);
  else if (/^3\d{9}$/.test(digits)) national = digits;
  return national ? `+92${national}` : null;
}

/** "+923001234567" → "0300 1234567". */
export function formatLocal(e164: string): string {
  return /^\+923\d{9}$/.test(e164) ? `0${e164.slice(3, 6)} ${e164.slice(6)}` : e164;
}

/** "+923001234567" → "+92300*****67", the way the API masks numbers. */
export function maskPhone(e164: string): string {
  return e164.length < 8 ? "***" : `${e164.slice(0, 6)}*****${e164.slice(-2)}`;
}
