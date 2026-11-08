using WalletPK.Common.Results;

namespace WalletPK.Identity.Domain.Users;

/// <summary>
/// Rules for a wallet MPIN: exactly 5 digits, and not trivially guessable
/// (all the same digit, or a straight run up or down).
/// </summary>
public static class MpinPolicy
{
    public const int Length = 5;

    public static Result Validate(string? mpin)
    {
        if (mpin is null || mpin.Length != Length || !mpin.All(char.IsAsciiDigit))
        {
            return Error.Validation("mpin.invalid_format", $"MPIN must be exactly {Length} digits.");
        }

        if (mpin.Distinct().Count() == 1 || IsStraightRun(mpin, +1) || IsStraightRun(mpin, -1))
        {
            return Error.Validation("mpin.too_simple", "Choose an MPIN that is harder to guess (not 11111, 12345 or 54321).");
        }

        return Result.Success();
    }

    private static bool IsStraightRun(string mpin, int step)
    {
        for (var i = 1; i < mpin.Length; i++)
        {
            if (mpin[i] - mpin[i - 1] != step)
            {
                return false;
            }
        }

        return true;
    }
}
