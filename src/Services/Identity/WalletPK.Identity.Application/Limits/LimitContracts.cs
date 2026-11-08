using WalletPK.Common.Identifiers;
using WalletPK.Common.Primitives;
using WalletPK.Identity.Domain.Devices;
using WalletPK.Identity.Domain.Users;

namespace WalletPK.Identity.Application.Limits;

/// <summary>Kinds of debit. They all count against the same daily and monthly limits.</summary>
public enum DebitType
{
    P2PTransfer = 0,
    BankTransfer = 1,
    BillPayment = 2,
    MobileTopup = 3,
    MerchantPayment = 4,
    CashOut = 5,
}

public enum LimitDecision
{
    Allowed = 0,
    ExceedsPerTransaction = 1,
    ExceedsDaily = 2,
    ExceedsMonthly = 3,
    CoolingOff = 4,
    UserNotAllowed = 5,
}

/// <param name="UserId">Whose limits.</param>
/// <param name="Amount">The debit amount (positive).</param>
/// <param name="Type">What kind of debit.</param>
/// <param name="DeviceId">The device used, when known, for the cooling-off rule.</param>
public sealed record LimitRequest(UserId UserId, Money Amount, DebitType Type, DeviceId? DeviceId);

/// <param name="Decision">Allowed, or which limit would be broken.</param>
/// <param name="Reason">Customer-facing explanation.</param>
/// <param name="KycTier">The user's tier, when the user exists.</param>
/// <param name="PerTransactionLimit">Largest single debit allowed right now (lower during cooling-off).</param>
/// <param name="DailyRemaining">What is left of today's limit (after this debit, when it was reserved).</param>
/// <param name="MonthlyRemaining">What is left of this month's limit (after this debit, when it was reserved).</param>
public sealed record LimitResult(
    LimitDecision Decision,
    string Reason,
    KycTier? KycTier,
    Money PerTransactionLimit,
    Money DailyRemaining,
    Money MonthlyRemaining)
{
    public bool IsAllowed => Decision == LimitDecision.Allowed;
}

/// <summary>One limit with what has been used of it.</summary>
public sealed record LimitUsageView(Money Limit, Money Used, Money Remaining);

/// <summary>The signed-in user's limits (GET /api/identity/limits).</summary>
/// <param name="KycTier">The user's tier.</param>
/// <param name="PerTransaction">Largest single debit (lower while the device cools off).</param>
/// <param name="Daily">Today's limit, resets at midnight Pakistan time.</param>
/// <param name="Monthly">This month's limit, resets on the 1st.</param>
/// <param name="MaxBalance">Highest wallet balance allowed at this tier.</param>
/// <param name="CoolingOffUntil">Set while this device is in its cooling-off period.</param>
public sealed record LimitsSummary(
    KycTier KycTier,
    Money PerTransaction,
    LimitUsageView Daily,
    LimitUsageView Monthly,
    Money MaxBalance,
    DateTimeOffset? CoolingOffUntil);
