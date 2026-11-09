using WalletPK.Common.Primitives;
using WalletPK.Ledger.Domain.Journals;

namespace WalletPK.Ledger.Application.Fees;

/// <summary>
/// One band of a fee schedule: applies to amounts up to <see cref="UpToRupees"/> (inclusive; null = no
/// upper end). The fee is <see cref="FlatRupees"/> plus <see cref="Percent"/> of the amount, then kept
/// between <see cref="MinRupees"/> and <see cref="MaxRupees"/>.
/// </summary>
public sealed class FeeBand
{
    public decimal? UpToRupees { get; set; }

    public decimal FlatRupees { get; set; }

    public decimal Percent { get; set; }

    public decimal MinRupees { get; set; }

    public decimal? MaxRupees { get; set; }
}

/// <summary>
/// Fee rules, from the "Fees" configuration section, so operations can change pricing without a deploy.
/// <code>
/// "Fees": {
///   "TaxPercent": 16,
///   "Schedules": {
///     "P2PTransfer": [ { "UpToRupees": 1000, "FlatRupees": 0 },
///                      { "Percent": 0.5, "MinRupees": 5, "MaxRupees": 50 } ]
///   }
/// }
/// </code>
/// A transaction type with no schedule is free.
/// </summary>
public sealed class FeeOptions
{
    public const string SectionName = "Fees";

    /// <summary>Federal excise duty charged on top of the fee (not on the amount sent).</summary>
    public decimal TaxPercent { get; set; } = 16;

    public Dictionary<string, List<FeeBand>> Schedules { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Bands must be ordered by UpToRupees, only the last may be open-ended, and no value may be negative.</summary>
    public bool IsValid() =>
        TaxPercent is >= 0 and <= 100 &&
        Schedules.All(schedule =>
            Enum.TryParse<JournalType>(schedule.Key, out _) &&
            schedule.Value.Count > 0 &&
            schedule.Value.All(band => band.FlatRupees >= 0 && band.Percent is >= 0 and <= 100 && band.MinRupees >= 0 && (band.MaxRupees is null || band.MaxRupees >= band.MinRupees)) &&
            schedule.Value.Take(schedule.Value.Count - 1).All(band => band.UpToRupees is not null) &&
            schedule.Value.Zip(schedule.Value.Skip(1)).All(pair => pair.Second.UpToRupees is null || pair.First.UpToRupees < pair.Second.UpToRupees));
}

/// <summary>What a transaction costs on top of the amount.</summary>
/// <param name="Amount">The amount sent.</param>
/// <param name="Fee">WalletPK's fee (credited to FEE_INCOME).</param>
/// <param name="Tax">Tax on the fee (credited to TAX_PAYABLE).</param>
public sealed record FeeQuote(Money Amount, Money Fee, Money Tax)
{
    /// <summary>What leaves the sender's wallet: amount + fee + tax.</summary>
    public Money TotalDebit => Amount + Fee + Tax;

    public bool IsFree => Fee.IsZero && Tax.IsZero;
}

/// <summary>
/// Calculates fees (M3.9). Money is rounded to the paisa, half away from zero, so a fee can never be
/// a fraction of a paisa and every journal still balances exactly.
/// </summary>
public sealed class FeeEngine(FeeOptions options)
{
    public FeeQuote Quote(JournalType type, Money amount)
    {
        if (!options.Schedules.TryGetValue(type.ToString(), out var bands) || bands.Count == 0)
        {
            return new FeeQuote(amount, Money.Zero(), Money.Zero());
        }

        var rupees = amount.ToMajorUnits();
        var band = bands.FirstOrDefault(candidate => candidate.UpToRupees is null || rupees <= candidate.UpToRupees) ?? bands[^1];

        var fee = Money.FromRupees(band.FlatRupees) + amount.Percent(band.Percent);
        fee = Money.Max(fee, Money.FromRupees(band.MinRupees));
        if (band.MaxRupees is { } max)
        {
            fee = Money.Min(fee, Money.FromRupees(max));
        }

        // A band that is free stays free (its minimum does not apply when it charges nothing at all).
        if (band.FlatRupees == 0 && band.Percent == 0)
        {
            fee = Money.Zero();
        }

        return new FeeQuote(amount, fee, fee.Percent(options.TaxPercent));
    }
}
