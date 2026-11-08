namespace WalletPK.Common.Primitives;

/// <summary>
/// An ISO 4217 currency and the number of digits in its minor unit
/// (PKR has 2: 1 rupee = 100 paisa).
/// </summary>
public sealed record Currency
{
    /// <summary>Pakistani rupee, the platform's home currency.</summary>
    public static readonly Currency Pkr = new("PKR", 2, "Rs.");

    /// <summary>US dollar (used for inbound remittances).</summary>
    public static readonly Currency Usd = new("USD", 2, "$");

    /// <summary>UAE dirham (used for inbound remittances).</summary>
    public static readonly Currency Aed = new("AED", 2, "AED");

    /// <summary>Saudi riyal (used for inbound remittances).</summary>
    public static readonly Currency Sar = new("SAR", 2, "SAR");

    private static readonly Dictionary<string, Currency> _byCode =
        new(StringComparer.OrdinalIgnoreCase)
        {
            [Pkr.Code] = Pkr,
            [Usd.Code] = Usd,
            [Aed.Code] = Aed,
            [Sar.Code] = Sar,
        };

    private Currency(string code, int minorUnitDigits, string symbol)
    {
        Code = code;
        MinorUnitDigits = minorUnitDigits;
        Symbol = symbol;
        MinorUnitsPerMajorUnit = (long)Math.Pow(10, minorUnitDigits);
    }

    /// <summary>Three-letter ISO 4217 code, for example "PKR".</summary>
    public string Code { get; }

    /// <summary>Number of decimal places in the minor unit.</summary>
    public int MinorUnitDigits { get; }

    /// <summary>Display symbol, for example "Rs.".</summary>
    public string Symbol { get; }

    /// <summary>How many minor units make one major unit (100 for PKR).</summary>
    public long MinorUnitsPerMajorUnit { get; }

    /// <summary>Looks up a supported currency by its ISO code.</summary>
    public static bool TryFromCode(string? code, out Currency currency)
    {
        if (code is not null && _byCode.TryGetValue(code, out var found))
        {
            currency = found;
            return true;
        }

        currency = Pkr;
        return false;
    }

    /// <summary>Gets a supported currency by its ISO code, or throws if it is unknown.</summary>
    public static Currency FromCode(string code) =>
        TryFromCode(code, out var currency)
            ? currency
            : throw new ArgumentException($"Unsupported currency code '{code}'.", nameof(code));

    public override string ToString() => Code;
}
