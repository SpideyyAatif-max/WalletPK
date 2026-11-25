import { describe, expect, it } from "vitest";
import { ApiError } from "./api";
import { MPIN_HEADER, isMpinLockout, isValidMpin, moneyRequestBinding, mpinHeaders, needsMpin } from "./mpin";

describe("MPIN confirmation", () => {
  it("binds a money request by its id, the way the server checks it", () => {
    expect(moneyRequestBinding("0193f1c4-0000-7000-8000-000000000001")).toBe("money-request:0193f1c4-0000-7000-8000-000000000001");
  });

  it("sends the token in X-Mpin-Confirmation", () => {
    expect(mpinHeaders("abc")).toEqual({ [MPIN_HEADER]: "abc" });
    expect(MPIN_HEADER).toBe("X-Mpin-Confirmation");
  });

  it("accepts exactly five digits", () => {
    expect(isValidMpin("13579")).toBe(true);
    expect(isValidMpin("1357")).toBe(false);
    expect(isValidMpin("135790")).toBe(false);
    expect(isValidMpin("1357a")).toBe(false);
  });

  it("tells a missing confirmation from a locked account", () => {
    expect(needsMpin(new ApiError(403, "auth.mpin_confirmation_required", "Confirm this payment with your MPIN."))).toBe(true);
    expect(needsMpin(new ApiError(403, "fraud.blocked", "No"))).toBe(false);
    expect(needsMpin(new Error("network"))).toBe(false);
    expect(isMpinLockout(new ApiError(403, "identity.account_locked", "Locked"))).toBe(true);
    expect(isMpinLockout(new ApiError(400, "identity.incorrect_mpin", "Incorrect MPIN. 4 attempt(s) left"))).toBe(false);
  });
});
