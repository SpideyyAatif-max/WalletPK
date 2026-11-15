using System.Globalization;
using System.Numerics;

namespace WalletPK.Payments.Domain;

/// <summary>
/// Pakistani IBANs (ISO 13616): "PK" + 2 check digits + 4-letter bank code + 16-digit account number,
/// 24 characters, e.g. PK36SCBL0000001123456702. The check digits catch almost every typo, so a wrong
/// IBAN is refused before any money moves.
/// </summary>
public static class Iban
{
    public const int Length = 24;

    /// <summary>The IBAN without spaces and in capitals, or null when it is not a valid Pakistani IBAN.</summary>
    public static string? Normalize(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return null;
        }

        var iban = input.Replace(" ", "", StringComparison.Ordinal).Replace("-", "", StringComparison.Ordinal).ToUpperInvariant();
        var wellFormed = iban.Length == Length
            && iban.StartsWith("PK", StringComparison.Ordinal)
            && iban[2..4].All(char.IsAsciiDigit)
            && iban[4..8].All(char.IsAsciiLetterUpper)
            && iban[8..].All(char.IsAsciiDigit);
        return wellFormed && Mod97(iban[4..] + iban[..4]) == 1 ? iban : null;
    }

    /// <summary>The 4-letter bank code of a normalized IBAN ("HABB").</summary>
    public static string BankCodeOf(string iban)
    {
        ArgumentNullException.ThrowIfNull(iban);
        return iban[4..8];
    }

    /// <summary>For display: "PK36 SCBL 0000 0011 2345 6702" with the middle digits hidden, "PK36 SCBL **** **** **** 6702".</summary>
    public static string Mask(string iban)
    {
        ArgumentNullException.ThrowIfNull(iban);
        return iban.Length == Length ? $"{iban[..4]} {iban[4..8]} **** **** **** {iban[^4..]}" : iban;
    }

    private static int Mod97(string value)
    {
        // Letters become two digits (A = 10 ... Z = 35), then the whole number mod 97.
        var digits = string.Concat(value.Select(c => char.IsAsciiLetter(c) ? (c - 'A' + 10).ToString(CultureInfo.InvariantCulture) : c.ToString()));
        return (int)(BigInteger.Parse(digits, CultureInfo.InvariantCulture) % 97);
    }
}
