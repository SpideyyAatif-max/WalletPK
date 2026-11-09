using System.Diagnostics.CodeAnalysis;

namespace WalletPK.Common.Primitives;

/// <summary>
/// Normalises a Pakistani mobile number to E.164 (+923001234567) whatever the user typed:
/// "0300 1234567", "0300-1234567", "+92 300 1234567", "923001234567" and "00923001234567" are all
/// the same number. Shared by Identity (accounts) and the Ledger (a wallet's number is its owner's mobile).
/// </summary>
public static class PakistaniMobileNumber
{
    public const string CountryCode = "+92";

    public static bool TryNormalize([NotNullWhen(true)] string? input, [NotNullWhen(true)] out string? e164)
    {
        e164 = null;
        if (string.IsNullOrWhiteSpace(input))
        {
            return false;
        }

        // Keep digits only (drops +, spaces, dashes and brackets).
        var digits = new string(input.Where(char.IsAsciiDigit).ToArray());

        if (digits.StartsWith("0092", StringComparison.Ordinal) && digits.Length == 14)
        {
            digits = digits[4..];
        }
        else if (digits.StartsWith("92", StringComparison.Ordinal) && digits.Length == 12)
        {
            digits = digits[2..];
        }
        else if (digits.StartsWith('0') && digits.Length == 11)
        {
            digits = digits[1..];
        }

        // Pakistani mobile numbers: 3XX followed by 7 digits.
        if (digits.Length != 10 || digits[0] != '3')
        {
            return false;
        }

        e164 = CountryCode + digits;
        return true;
    }

    /// <summary>Safe for logs and screens, for example "+92300*****67".</summary>
    public static string Mask(string e164)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(e164);
        return e164.Length < 8 ? "***" : $"{e164[..6]}*****{e164[^2..]}";
    }
}
