using System.Globalization;
using System.Text.Json.Serialization;
using WalletPK.Common.Results;

namespace WalletPK.Common.Primitives;

/// <summary>
/// An exact amount of money, stored as a whole number of minor units (paisa for PKR).
/// Floating point is never used: every operation is exact or rounds explicitly,
/// and arithmetic overflow throws instead of wrapping around.
/// </summary>
/// <remarks>
/// Money may be negative (for example a difference or a reversal). Rules such as
/// "a wallet balance can never go below zero" are enforced by the ledger, not here.
/// </remarks>
[JsonConverter(typeof(MoneyJsonConverter))]
public readonly struct Money : IEquatable<Money>, IComparable<Money>
{
    private readonly Currency? _currency;

    private Money(long minorUnits, Currency currency)
    {
        MinorUnits = minorUnits;
        _currency = currency;
    }

    /// <summary>The amount in minor units (paisa for PKR).</summary>
    public long MinorUnits { get; }

    /// <summary>The currency. A default-initialised Money is PKR.</summary>
    public Currency Currency => _currency ?? Currency.Pkr;

    public bool IsZero => MinorUnits == 0;

    public bool IsPositive => MinorUnits > 0;

    public bool IsNegative => MinorUnits < 0;

    // ------------------------------------------------------------------
    // Creation
    // ------------------------------------------------------------------

    /// <summary>Zero in the given currency (PKR by default).</summary>
    public static Money Zero(Currency? currency = null) => new(0, currency ?? Currency.Pkr);

    /// <summary>Creates money from minor units, for example 125050 paisa = Rs. 1,250.50.</summary>
    public static Money FromMinorUnits(long minorUnits, Currency? currency = null) =>
        new(minorUnits, currency ?? Currency.Pkr);

    /// <summary>Creates a PKR amount from paisa.</summary>
    public static Money FromPaisa(long paisa) => new(paisa, Currency.Pkr);

    /// <summary>Creates a PKR amount from rupees, for example 1250.50m. Throws if it has more than 2 decimals.</summary>
    public static Money FromRupees(decimal rupees) => FromMajorUnits(rupees, Currency.Pkr);

    /// <summary>
    /// Creates money from major units (rupees, dollars). Throws <see cref="ArgumentException"/> when the
    /// amount has more decimal places than the currency allows, and <see cref="OverflowException"/> when it is too large.
    /// </summary>
    public static Money FromMajorUnits(decimal amount, Currency? currency = null)
    {
        var result = TryFromMajorUnits(amount, currency);
        return result.IsSuccess
            ? result.Value
            : throw new ArgumentException(result.Error.Message, nameof(amount));
    }

    /// <summary>Safe version of <see cref="FromMajorUnits"/> for user input: returns a validation error instead of throwing.</summary>
    public static Result<Money> TryFromMajorUnits(decimal amount, Currency? currency = null)
    {
        var target = currency ?? Currency.Pkr;
        var scaled = amount * target.MinorUnitsPerMajorUnit;

        if (scaled != decimal.Truncate(scaled))
        {
            return Error.Validation(
                "money.invalid_precision",
                $"{target.Code} amounts can have at most {target.MinorUnitDigits} decimal places.");
        }

        if (scaled > long.MaxValue || scaled < long.MinValue)
        {
            return Error.Validation("money.out_of_range", "The amount is too large.");
        }

        return new Money((long)scaled, target);
    }

    // ------------------------------------------------------------------
    // Conversion and formatting
    // ------------------------------------------------------------------

    /// <summary>The amount in major units (rupees), for display and reporting only.</summary>
    public decimal ToMajorUnits() => (decimal)MinorUnits / Currency.MinorUnitsPerMajorUnit;

    /// <summary>Machine-friendly form, for example "PKR 1,250.50".</summary>
    public override string ToString() =>
        $"{Currency.Code} {ToMajorUnits().ToString("N" + Currency.MinorUnitDigits, CultureInfo.InvariantCulture)}";

    /// <summary>User-facing form, for example "Rs. 1,250.50".</summary>
    public string ToDisplayString() =>
        $"{Currency.Symbol} {ToMajorUnits().ToString("N" + Currency.MinorUnitDigits, CultureInfo.InvariantCulture)}";

    // ------------------------------------------------------------------
    // Arithmetic (always checked: overflow throws OverflowException)
    // ------------------------------------------------------------------

    public Money Add(Money other)
    {
        EnsureSameCurrency(other);
        return new Money(checked(MinorUnits + other.MinorUnits), Currency);
    }

    public Money Subtract(Money other)
    {
        EnsureSameCurrency(other);
        return new Money(checked(MinorUnits - other.MinorUnits), Currency);
    }

    public Money Negate() => new(checked(-MinorUnits), Currency);

    public Money Abs() => IsNegative ? Negate() : this;

    /// <summary>Multiplies by a whole number, for example a quantity.</summary>
    public Money Multiply(long factor) => new(checked(MinorUnits * factor), Currency);

    /// <summary>
    /// Multiplies by a rate and rounds to the nearest minor unit.
    /// Default rounding is half away from zero (2.5 paisa becomes 3 paisa).
    /// </summary>
    public Money MultiplyBy(decimal rate, MidpointRounding rounding = MidpointRounding.AwayFromZero)
    {
        var exact = MinorUnits * rate;
        var rounded = Math.Round(exact, 0, rounding);
        return new Money((long)rounded, Currency);
    }

    /// <summary>A percentage of this amount, for example a 1.5% fee: <c>amount.Percent(1.5m)</c>.</summary>
    public Money Percent(decimal percent, MidpointRounding rounding = MidpointRounding.AwayFromZero) =>
        MultiplyBy(percent / 100m, rounding);

    /// <summary>
    /// Splits the amount by ratios without losing or creating a single paisa.
    /// Any remainder is given one minor unit at a time to the first shares.
    /// Example: Rs. 100 split 1:1:1 gives Rs. 33.34, Rs. 33.33, Rs. 33.33.
    /// </summary>
    public IReadOnlyList<Money> Allocate(params int[] ratios)
    {
        ArgumentNullException.ThrowIfNull(ratios);
        if (ratios.Length == 0)
        {
            throw new ArgumentException("At least one ratio is required.", nameof(ratios));
        }

        if (ratios.Any(r => r < 0) || ratios.All(r => r == 0))
        {
            throw new ArgumentException("Ratios must be non-negative and not all zero.", nameof(ratios));
        }

        var sign = IsNegative ? -1 : 1;
        var total = (Int128)MinorUnits * sign;
        Int128 ratioSum = 0;
        foreach (var ratio in ratios)
        {
            ratioSum += ratio;
        }

        var shares = new long[ratios.Length];
        Int128 allocated = 0;
        for (var i = 0; i < ratios.Length; i++)
        {
            var share = total * ratios[i] / ratioSum;
            shares[i] = (long)share;
            allocated += share;
        }

        var remainder = (long)(total - allocated);
        for (var i = 0; remainder > 0; i = (i + 1) % shares.Length)
        {
            if (ratios[i] == 0)
            {
                continue;
            }

            shares[i]++;
            remainder--;
        }

        var currency = Currency;
        return shares.Select(s => new Money(s * sign, currency)).ToArray();
    }

    /// <summary>Splits the amount into equal parts without losing a paisa.</summary>
    public IReadOnlyList<Money> Split(int parts)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(parts);
        return Allocate(Enumerable.Repeat(1, parts).ToArray());
    }

    public static Money Min(Money left, Money right) => left.CompareTo(right) <= 0 ? left : right;

    public static Money Max(Money left, Money right) => left.CompareTo(right) >= 0 ? left : right;

    // ------------------------------------------------------------------
    // Equality and comparison
    // ------------------------------------------------------------------

    public bool Equals(Money other) =>
        MinorUnits == other.MinorUnits &&
        string.Equals(Currency.Code, other.Currency.Code, StringComparison.Ordinal);

    public override bool Equals(object? obj) => obj is Money other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(MinorUnits, Currency.Code);

    /// <summary>Compares two amounts of the same currency. Throws if the currencies differ.</summary>
    public int CompareTo(Money other)
    {
        EnsureSameCurrency(other);
        return MinorUnits.CompareTo(other.MinorUnits);
    }

    private void EnsureSameCurrency(Money other)
    {
        if (!string.Equals(Currency.Code, other.Currency.Code, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Cannot combine {Currency.Code} and {other.Currency.Code} amounts without an explicit conversion.");
        }
    }

    // ------------------------------------------------------------------
    // Operators
    // ------------------------------------------------------------------

    public static Money operator +(Money left, Money right) => left.Add(right);

    public static Money operator -(Money left, Money right) => left.Subtract(right);

    public static Money operator -(Money value) => value.Negate();

    public static Money operator *(Money left, long right) => left.Multiply(right);

    public static Money operator *(long left, Money right) => right.Multiply(left);

    public static bool operator ==(Money left, Money right) => left.Equals(right);

    public static bool operator !=(Money left, Money right) => !left.Equals(right);

    public static bool operator <(Money left, Money right) => left.CompareTo(right) < 0;

    public static bool operator >(Money left, Money right) => left.CompareTo(right) > 0;

    public static bool operator <=(Money left, Money right) => left.CompareTo(right) <= 0;

    public static bool operator >=(Money left, Money right) => left.CompareTo(right) >= 0;
}
