using System.Globalization;
using System.Numerics;

namespace WalletPK.MockProviders.Banks;

/// <summary>
/// Pakistani IBANs (ISO 13616): "PK" + 2 check digits + 4-letter bank code + 16-digit account number,
/// 24 characters, e.g. PK36SCBL0000001123456702. The check digits make a typo almost always detectable.
/// </summary>
public static class Iban
{
    public const int Length = 24;

    /// <summary>The IBAN for an account number at a bank, with correct check digits.</summary>
    public static string For(string bankCode, string accountNumber)
    {
        if (bankCode is not { Length: 4 } || !bankCode.All(char.IsAsciiLetterUpper))
        {
            throw new ArgumentException("A bank code is four capital letters.", nameof(bankCode));
        }

        if (accountNumber is not { Length: 16 } || !accountNumber.All(char.IsAsciiDigit))
        {
            throw new ArgumentException("An account number is 16 digits.", nameof(accountNumber));
        }

        var check = 98 - Mod97(bankCode + accountNumber + "PK00");
        return $"PK{check:00}{bankCode}{accountNumber}";
    }

    /// <summary>The IBAN without spaces and in capitals, or null when it is not a valid Pakistani IBAN.</summary>
    public static string? Normalize(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return null;
        }

        var iban = input.Replace(" ", "", StringComparison.Ordinal).ToUpperInvariant();
        var wellFormed = iban.Length == Length
            && iban.StartsWith("PK", StringComparison.Ordinal)
            && iban[2..4].All(char.IsAsciiDigit)
            && iban[4..8].All(char.IsAsciiLetterUpper)
            && iban[8..].All(char.IsAsciiDigit);
        return wellFormed && Mod97(iban[4..] + iban[..4]) == 1 ? iban : null;
    }

    public static string BankCodeOf(string iban) => iban[4..8];

    public static string AccountNumberOf(string iban) => iban[8..];

    private static int Mod97(string value)
    {
        // Letters become two digits (A = 10 ... Z = 35), then the whole number mod 97.
        var digits = string.Concat(value.Select(c => char.IsAsciiLetter(c) ? (c - 'A' + 10).ToString(CultureInfo.InvariantCulture) : c.ToString()));
        return (int)(BigInteger.Parse(digits, CultureInfo.InvariantCulture) % 97);
    }
}
