import { describe, expect, it } from "vitest";
import { formatLocal, maskPhone, toE164 } from "./phone";

describe("toE164", () => {
  it.each(["03001234567", "0300 1234567", "0300-1234567", "+92 300 1234567", "+923001234567", "923001234567", "3001234567"])(
    "understands %s",
    (input) => {
      expect(toE164(input)).toBe("+923001234567");
    },
  );

  it.each(["", "0300123456", "042 1234567", "+923001234567 8", "04212345678", "+14155550100"])("refuses %j", (input) => {
    expect(toE164(input)).toBeNull();
  });
});

describe("display", () => {
  it("formats and masks like the API", () => {
    expect(formatLocal("+923001234567")).toBe("0300 1234567");
    expect(maskPhone("+923001234567")).toBe("+92300*****67");
  });
});
