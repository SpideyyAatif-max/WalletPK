using System.Globalization;
using WalletPK.Common.Identifiers;
using WalletPK.Common.Primitives;
using WalletPK.Common.Results;
using WalletPK.Common.Time;
using WalletPK.Identity.Application.Abstractions;
using WalletPK.Identity.Application.Account;
using WalletPK.Identity.Domain.Devices;
using WalletPK.Identity.Domain.Users;

namespace WalletPK.Identity.Application.Limits;

/// <summary>
/// Decides whether a debit fits the user's limits. Rules, checked in this order:
/// <list type="number">
/// <item>The account must be able to transact (not frozen, not locked).</item>
/// <item>Per-transaction limit of the user's KYC tier.</item>
/// <item>A device in its cooling-off period may send at most Rs. 5,000 per transaction.</item>
/// <item>Daily and monthly totals (Pakistan calendar), checked and counted atomically in the usage store.</item>
/// </list>
/// The Ledger calls <see cref="ReserveAsync"/> before every debit and <see cref="ReleaseAsync"/> if the debit fails.
/// </summary>
public sealed class LimitService(
    IUserRepository users,
    IDeviceRepository devices,
    ILimitUsageStore usage,
    LimitsOptions options,
    TimeProvider timeProvider)
{
    public static Error AmountInvalid { get; } =
        Error.Validation("limits.amount_invalid", "The amount must be greater than zero.");

    public static Error TransactionIdRequired { get; } =
        Error.Validation("limits.transaction_id_required", "A transaction ID is required to reserve a limit.");

    /// <summary>Would this debit fit right now? Counts nothing.</summary>
    public async Task<Result<LimitResult>> CheckAsync(LimitRequest request, CancellationToken cancellationToken)
    {
        var context = await EvaluateStaticRulesAsync(request, cancellationToken);
        if (context.IsFailure)
        {
            return context.Error;
        }

        var (user, tierLimits, perTransactionLimit, earlyResult) = context.Value;
        if (earlyResult is not null)
        {
            return earlyResult;
        }

        var used = await usage.GetUsageAsync(user!.Id, timeProvider.GetBusinessDate(), cancellationToken);
        var amount = request.Amount.MinorUnits;

        var decision =
            used.DailyUsedPaisa + amount > tierLimits!.DailyMoney.MinorUnits ? LimitDecision.ExceedsDaily
            : used.MonthlyUsedPaisa + amount > tierLimits.MonthlyMoney.MinorUnits ? LimitDecision.ExceedsMonthly
            : LimitDecision.Allowed;

        return Build(decision, user, tierLimits, perTransactionLimit, used);
    }

    /// <summary>Checks and counts the debit in one atomic step. Retrying with the same transaction ID is safe.</summary>
    public async Task<Result<LimitResult>> ReserveAsync(string? transactionId, LimitRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(transactionId))
        {
            return TransactionIdRequired;
        }

        var context = await EvaluateStaticRulesAsync(request, cancellationToken);
        if (context.IsFailure)
        {
            return context.Error;
        }

        var (user, tierLimits, perTransactionLimit, earlyResult) = context.Value;
        if (earlyResult is not null)
        {
            return earlyResult;
        }

        var outcome = await usage.TryReserveAsync(
            new LimitReservation(
                transactionId.Trim(),
                user!.Id,
                request.Amount.MinorUnits,
                timeProvider.GetBusinessDate(),
                tierLimits!.DailyMoney.MinorUnits,
                tierLimits.MonthlyMoney.MinorUnits),
            cancellationToken);

        var decision = outcome.Status switch
        {
            ReservationStatus.ExceedsDaily => LimitDecision.ExceedsDaily,
            ReservationStatus.ExceedsMonthly => LimitDecision.ExceedsMonthly,
            _ => LimitDecision.Allowed,
        };

        return Build(decision, user, tierLimits, perTransactionLimit, outcome.Usage);
    }

    public Task<bool> ReleaseAsync(string? transactionId, CancellationToken cancellationToken) =>
        string.IsNullOrWhiteSpace(transactionId)
            ? Task.FromResult(false)
            : usage.ReleaseAsync(transactionId.Trim(), cancellationToken);

    /// <summary>The user's limits and how much of them is used (for the app's limits screen).</summary>
    public async Task<Result<LimitsSummary>> GetSummaryAsync(UserId userId, DeviceId? deviceId, CancellationToken cancellationToken)
    {
        var user = await users.GetByIdAsync(userId, cancellationToken);
        if (user is null)
        {
            return AccountErrors.UserNotFound;
        }

        var tierLimits = options.For(user.KycTier);
        var coolingOffUntil = await CoolingOffUntilAsync(userId, deviceId, cancellationToken);
        var used = await usage.GetUsageAsync(userId, timeProvider.GetBusinessDate(), cancellationToken);

        return new LimitsSummary(
            user.KycTier,
            coolingOffUntil is null ? tierLimits.PerTransactionMoney : Money.Min(tierLimits.PerTransactionMoney, options.CoolingOffPerTransactionMoney),
            View(tierLimits.DailyMoney, used.DailyUsedPaisa),
            View(tierLimits.MonthlyMoney, used.MonthlyUsedPaisa),
            tierLimits.MaxBalanceMoney,
            coolingOffUntil);
    }

    /// <summary>
    /// Rules that need no usage data. Returns the user and limits to continue with, or a finished
    /// result (a refusal) in <c>EarlyResult</c>.
    /// </summary>
    private async Task<Result<RuleContext>> EvaluateStaticRulesAsync(
        LimitRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!request.Amount.IsPositive || request.Amount.Currency.Code != Currency.Pkr.Code)
        {
            return AmountInvalid;
        }

        var user = await users.GetByIdAsync(request.UserId, cancellationToken);
        var now = timeProvider.GetUtcNow();
        if (user is null || !user.CanTransact(now))
        {
            var refusal = new LimitResult(
                LimitDecision.UserNotAllowed,
                "This account cannot make payments right now. Please contact WalletPK support.",
                user?.KycTier,
                Money.Zero(),
                Money.Zero(),
                Money.Zero());
            return new RuleContext(user, null, Money.Zero(), refusal);
        }

        var tierLimits = options.For(user.KycTier);
        var perTransactionLimit = tierLimits.PerTransactionMoney;

        if (request.Amount > perTransactionLimit)
        {
            return new RuleContext(user, tierLimits, perTransactionLimit, Build(LimitDecision.ExceedsPerTransaction, user, tierLimits, perTransactionLimit, null));
        }

        if (await CoolingOffUntilAsync(user.Id, request.DeviceId, cancellationToken) is not null)
        {
            perTransactionLimit = Money.Min(perTransactionLimit, options.CoolingOffPerTransactionMoney);
            if (request.Amount > perTransactionLimit)
            {
                return new RuleContext(user, tierLimits, perTransactionLimit, Build(LimitDecision.CoolingOff, user, tierLimits, perTransactionLimit, null));
            }
        }

        return new RuleContext(user, tierLimits, perTransactionLimit, null);
    }

    private async Task<DateTimeOffset?> CoolingOffUntilAsync(UserId userId, DeviceId? deviceId, CancellationToken cancellationToken)
    {
        if (deviceId is not { } id)
        {
            return null;
        }

        var device = await devices.GetByIdAsync(id, cancellationToken);
        return device is not null && device.UserId == userId
            ? device.ActiveCoolingOffUntil(timeProvider.GetUtcNow())
            : null;
    }

    private static LimitResult Build(LimitDecision decision, User user, TierLimits limits, Money perTransactionLimit, LimitUsage? used)
    {
        var dailyRemaining = used is null ? limits.DailyMoney : Remaining(limits.DailyMoney, used.DailyUsedPaisa);
        var monthlyRemaining = used is null ? limits.MonthlyMoney : Remaining(limits.MonthlyMoney, used.MonthlyUsedPaisa);

        var reason = decision switch
        {
            LimitDecision.Allowed => "Within your limits.",
            LimitDecision.ExceedsPerTransaction => $"The most you can send in one transaction is {Rupees(perTransactionLimit)}. Verify your CNIC to raise your limits.",
            LimitDecision.CoolingOff => $"This phone was added recently, so for 24 hours you can send at most {Rupees(perTransactionLimit)} at a time.",
            LimitDecision.ExceedsDaily => $"This would go over your daily limit. You can send {Rupees(dailyRemaining)} more today.",
            LimitDecision.ExceedsMonthly => $"This would go over your monthly limit. You can send {Rupees(monthlyRemaining)} more this month.",
            _ => "This account cannot make payments right now.",
        };

        return new LimitResult(decision, reason, user.KycTier, perTransactionLimit, dailyRemaining, monthlyRemaining);
    }

    private static LimitUsageView View(Money limit, long usedPaisa) =>
        new(limit, Money.FromPaisa(usedPaisa), Remaining(limit, usedPaisa));

    private static Money Remaining(Money limit, long usedPaisa) =>
        Money.FromPaisa(Math.Max(0, limit.MinorUnits - usedPaisa));

    private static string Rupees(Money amount) =>
        "Rs. " + amount.ToMajorUnits().ToString("#,##0.##", CultureInfo.InvariantCulture);

    /// <summary>Outcome of the rules that need no usage data: a refusal in <paramref name="EarlyResult"/>, or what to continue with.</summary>
    /// <param name="User">The user, when found.</param>
    /// <param name="Limits">The user's tier limits.</param>
    /// <param name="PerTransactionLimit">The per-transaction cap in force (lower during cooling-off).</param>
    /// <param name="EarlyResult">Set when a rule already refused the debit.</param>
    private sealed record RuleContext(User? User, TierLimits? Limits, Money PerTransactionLimit, LimitResult? EarlyResult);
}
