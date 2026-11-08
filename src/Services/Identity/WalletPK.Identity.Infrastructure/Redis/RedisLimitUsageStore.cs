using System.Globalization;
using StackExchange.Redis;
using WalletPK.Common.Identifiers;
using WalletPK.Identity.Application.Abstractions;

namespace WalletPK.Identity.Infrastructure.Redis;

/// <summary>
/// Limit usage counters in Redis:
/// <list type="bullet">
/// <item><c>identity:limits:{user}:d:20261108</c>: paisa debited that Pakistan day (kept 2 days)</item>
/// <item><c>identity:limits:{user}:m:202611</c>: paisa debited that month (kept 35 days)</item>
/// <item><c>identity:limits:reservation:{transactionId}</c>: what a transaction counted, so it can be released or retried</item>
/// </list>
/// Reserve and release are Lua scripts: Redis runs each one atomically, so "check both limits, then
/// count" can never interleave with a parallel debit. Real systems reconcile these totals against
/// the ledger nightly (M12) in case Redis loses data.
/// </summary>
internal sealed class RedisLimitUsageStore(IConnectionMultiplexer redis) : ILimitUsageStore
{
    private static readonly TimeSpan _dailyKeyLifetime = TimeSpan.FromDays(2);
    private static readonly TimeSpan _monthlyKeyLifetime = TimeSpan.FromDays(35);
    private static readonly TimeSpan _reservationLifetime = TimeSpan.FromDays(35);

    // Returns {status, dailyUsed, monthlyUsed}; status 0 reserved, 1 already reserved, 2 over daily, 3 over monthly.
    private const string ReserveScript = """
        local function used(key) return tonumber(redis.call('GET', key) or '0') end
        if redis.call('EXISTS', KEYS[3]) == 1 then
          return {1, used(KEYS[1]), used(KEYS[2])}
        end
        local amount = tonumber(ARGV[1])
        local day = used(KEYS[1])
        local month = used(KEYS[2])
        if day + amount > tonumber(ARGV[2]) then return {2, day, month} end
        if month + amount > tonumber(ARGV[3]) then return {3, day, month} end
        day = redis.call('INCRBY', KEYS[1], amount)
        redis.call('EXPIRE', KEYS[1], ARGV[4])
        month = redis.call('INCRBY', KEYS[2], amount)
        redis.call('EXPIRE', KEYS[2], ARGV[5])
        redis.call('HSET', KEYS[3], 'amount', amount, 'day', KEYS[1], 'month', KEYS[2])
        redis.call('EXPIRE', KEYS[3], ARGV[6])
        return {0, day, month}
        """;

    // Returns 1 if a reservation was released, 0 if there was none. Counters never go below zero.
    private const string ReleaseScript = """
        local r = redis.call('HMGET', KEYS[1], 'amount', 'day', 'month')
        if not r[1] then return 0 end
        local amount = tonumber(r[1])
        for i = 2, 3 do
          local current = tonumber(redis.call('GET', r[i]) or '0')
          if current > 0 then redis.call('DECRBY', r[i], math.min(current, amount)) end
        end
        redis.call('DEL', KEYS[1])
        return 1
        """;

    private IDatabase Database => redis.GetDatabase();

    public async Task<LimitUsage> GetUsageAsync(UserId userId, DateOnly businessDate, CancellationToken cancellationToken)
    {
        var values = await Database.StringGetAsync([DailyKey(userId, businessDate), MonthlyKey(userId, businessDate)]);
        return new LimitUsage(ToLong(values[0]), ToLong(values[1]));
    }

    public async Task<ReservationOutcome> TryReserveAsync(LimitReservation reservation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reservation);

        var result = await Database.ScriptEvaluateAsync(
            ReserveScript,
            [DailyKey(reservation.UserId, reservation.BusinessDate), MonthlyKey(reservation.UserId, reservation.BusinessDate), ReservationKey(reservation.TransactionId)],
            [
                reservation.AmountPaisa,
                reservation.DailyLimitPaisa,
                reservation.MonthlyLimitPaisa,
                (long)_dailyKeyLifetime.TotalSeconds,
                (long)_monthlyKeyLifetime.TotalSeconds,
                (long)_reservationLifetime.TotalSeconds,
            ]);

        var values = (RedisResult[]?)result ?? throw new InvalidOperationException("The limit reservation script returned no result.");
        var status = (int)values[0] switch
        {
            0 => ReservationStatus.Reserved,
            1 => ReservationStatus.AlreadyReserved,
            2 => ReservationStatus.ExceedsDaily,
            _ => ReservationStatus.ExceedsMonthly,
        };

        return new ReservationOutcome(status, new LimitUsage((long)values[1], (long)values[2]));
    }

    public async Task<bool> ReleaseAsync(string transactionId, CancellationToken cancellationToken)
    {
        var result = await Database.ScriptEvaluateAsync(ReleaseScript, [ReservationKey(transactionId)]);
        return (int)result == 1;
    }

    private static RedisKey DailyKey(UserId userId, DateOnly date) =>
        $"identity:limits:{userId}:d:{date.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}";

    private static RedisKey MonthlyKey(UserId userId, DateOnly date) =>
        $"identity:limits:{userId}:m:{date.ToString("yyyyMM", CultureInfo.InvariantCulture)}";

    private static RedisKey ReservationKey(string transactionId) => $"identity:limits:reservation:{transactionId}";

    private static long ToLong(RedisValue value) => value.TryParse(out long parsed) ? parsed : 0;
}
