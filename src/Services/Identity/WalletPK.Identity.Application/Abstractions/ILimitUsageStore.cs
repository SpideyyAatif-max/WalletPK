using WalletPK.Common.Identifiers;

namespace WalletPK.Identity.Application.Abstractions;

/// <summary>How much of the daily and monthly limits a user has used, in paisa.</summary>
public sealed record LimitUsage(long DailyUsedPaisa, long MonthlyUsedPaisa);

/// <summary>A request to count a debit against the limits.</summary>
/// <param name="TransactionId">The ledger transaction; one reservation per transaction.</param>
/// <param name="UserId">Whose limits.</param>
/// <param name="AmountPaisa">The debit amount.</param>
/// <param name="BusinessDate">Pakistan date the debit falls on (decides the day and month buckets).</param>
/// <param name="DailyLimitPaisa">The user's daily limit.</param>
/// <param name="MonthlyLimitPaisa">The user's monthly limit.</param>
public sealed record LimitReservation(
    string TransactionId,
    UserId UserId,
    long AmountPaisa,
    DateOnly BusinessDate,
    long DailyLimitPaisa,
    long MonthlyLimitPaisa);

public enum ReservationStatus
{
    Reserved = 0,

    /// <summary>This transaction was reserved before; nothing was counted twice.</summary>
    AlreadyReserved = 1,

    ExceedsDaily = 2,
    ExceedsMonthly = 3,
}

/// <param name="Status">What happened.</param>
/// <param name="Usage">Usage after the call (including this debit when reserved).</param>
public sealed record ReservationOutcome(ReservationStatus Status, LimitUsage Usage);

/// <summary>
/// Running totals of debits per user, per day and per month (Redis in production). Check-and-count
/// must be one atomic step, otherwise two parallel debits could both fit and together break the limit.
/// </summary>
public interface ILimitUsageStore
{
    Task<LimitUsage> GetUsageAsync(UserId userId, DateOnly businessDate, CancellationToken cancellationToken);

    /// <summary>Atomically: if the debit fits both limits, count it and remember the reservation.</summary>
    Task<ReservationOutcome> TryReserveAsync(LimitReservation reservation, CancellationToken cancellationToken);

    /// <summary>Un-counts a reservation (the debit failed). False if there was nothing to release.</summary>
    Task<bool> ReleaseAsync(string transactionId, CancellationToken cancellationToken);
}
