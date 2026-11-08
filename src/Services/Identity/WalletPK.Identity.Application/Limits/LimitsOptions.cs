using WalletPK.Common.Primitives;
using WalletPK.Identity.Domain.Users;

namespace WalletPK.Identity.Application.Limits;

/// <summary>
/// Debit limits per KYC tier, in rupees, from the "Limits" configuration section. Modelled on
/// branchless-banking account levels: the more verified the customer, the more they can move.
/// Daily limits reset at midnight Pakistan time; monthly limits on the 1st.
/// </summary>
public sealed class LimitsOptions
{
    public const string SectionName = "Limits";

    public TierLimits L0 { get; set; } = new() { PerTransaction = 10_000, Daily = 25_000, Monthly = 100_000, MaxBalance = 100_000 };

    public TierLimits L1 { get; set; } = new() { PerTransaction = 50_000, Daily = 100_000, Monthly = 500_000, MaxBalance = 500_000 };

    public TierLimits L2 { get; set; } = new() { PerTransaction = 250_000, Daily = 500_000, Monthly = 2_000_000, MaxBalance = 3_000_000 };

    /// <summary>Largest single debit from a device still in its new-device cooling-off period.</summary>
    public decimal CoolingOffPerTransaction { get; set; } = 5_000;

    public TierLimits For(KycTier tier) => tier switch
    {
        KycTier.L0 => L0,
        KycTier.L1 => L1,
        KycTier.L2 => L2,
        _ => throw new ArgumentOutOfRangeException(nameof(tier), tier, "Unknown KYC tier."),
    };

    public Money CoolingOffPerTransactionMoney => Money.FromRupees(CoolingOffPerTransaction);

    /// <summary>True when every tier is internally consistent and higher tiers are never lower.</summary>
    public bool IsValid() =>
        L0.IsValid() && L1.IsValid() && L2.IsValid() &&
        L0.Daily <= L1.Daily && L1.Daily <= L2.Daily &&
        L0.Monthly <= L1.Monthly && L1.Monthly <= L2.Monthly &&
        CoolingOffPerTransaction > 0;
}

/// <summary>The limits of one tier, in rupees.</summary>
public sealed class TierLimits
{
    public decimal PerTransaction { get; set; }

    public decimal Daily { get; set; }

    public decimal Monthly { get; set; }

    /// <summary>Highest balance the wallet may hold (enforced by the Ledger on credits).</summary>
    public decimal MaxBalance { get; set; }

    public Money PerTransactionMoney => Money.FromRupees(PerTransaction);

    public Money DailyMoney => Money.FromRupees(Daily);

    public Money MonthlyMoney => Money.FromRupees(Monthly);

    public Money MaxBalanceMoney => Money.FromRupees(MaxBalance);

    public bool IsValid() =>
        PerTransaction > 0 && PerTransaction <= Daily && Daily <= Monthly && MaxBalance > 0;
}
