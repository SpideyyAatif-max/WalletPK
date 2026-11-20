using System.Globalization;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using StackExchange.Redis;
using WalletPK.Fraud.Application;
using WalletPK.Fraud.Domain;

namespace WalletPK.Fraud.Infrastructure.Redis;

/// <summary>
/// The feature windows in Redis (M10.1). Per user: a sorted set of allowed payments over 24 hours (velocity
/// and sums), the recipients paid (90 days), a one-hour flag after a hold or block, an unused step-up
/// confirmation (per device, a few minutes); per device: when it was
/// trusted; per recipient: a sorted set of who paid it in the last hour (fan-in, rings). One read is one
/// round trip (the commands are pipelined); writes are fire-and-forget, so scoring stays in milliseconds.
/// </summary>
internal sealed class RedisFeatureStore(IConnectionMultiplexer redis) : IFeatureStore
{
    private static readonly TimeSpan Day = TimeSpan.FromHours(24);
    private static readonly TimeSpan Hour = TimeSpan.FromHours(1);

    private IDatabase Database => redis.GetDatabase();

    private static string Payments(Guid userId) => $"fraud:payments:{userId:N}";

    private static string Recipients(Guid userId) => $"fraud:recipients:{userId:N}";

    private static string Flag(Guid userId) => $"fraud:flag:{userId:N}";

    private static string Device(Guid userId, string deviceId) => $"fraud:device:{userId:N}:{deviceId}";

    private static string Account(Guid userId) => $"fraud:account:{userId:N}";

    private static string Owner(string walletNumber) => $"fraud:owner:{walletNumber}";

    private static string FanIn(string recipient) => $"fraud:fanin:{recipient}";

    private static string Score(Guid transactionId) => $"fraud:score:{transactionId:N}";

    private static string Ring(string recipient) => $"fraud:ring:{recipient}";

    private static string StepUp(Guid userId, string? deviceId) => $"fraud:stepup:{userId:N}:{(string.IsNullOrEmpty(deviceId) ? "any" : deviceId)}";

    private static double Ms(DateTimeOffset at) => at.ToUnixTimeMilliseconds();

    private static DateTimeOffset At(double ms) => DateTimeOffset.FromUnixTimeMilliseconds((long)ms);

    public async Task<RiskResult?> CachedAsync(Guid transactionId, CancellationToken cancellationToken)
    {
        var cached = await Database.StringGetAsync(Score(transactionId));
        if (cached.IsNullOrEmpty)
        {
            return null;
        }

        // "action|score|rule,rule|reason"
        var parts = cached.ToString().Split('|', 4);
        return new RiskResult(
            int.Parse(parts[1], CultureInfo.InvariantCulture),
            Enum.Parse<RiskAction>(parts[0]),
            parts[2].Length == 0 ? [] : parts[2].Split(','),
            parts[3].Length == 0 ? null : parts[3]);
    }

    public async Task<FeatureSnapshot> ReadAsync(ScoreInput input, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        var database = Database;
        var payments = database.SortedSetRangeByScoreWithScoresAsync(Payments(input.UserId), Ms(now - Day), double.PositiveInfinity);
        var device = string.IsNullOrEmpty(input.DeviceId) ? Task.FromResult(Array.Empty<HashEntry>()) : database.HashGetAllAsync(Device(input.UserId, input.DeviceId));
        var registered = database.HashGetAsync(Account(input.UserId), "registeredAt");
        var flagged = database.KeyExistsAsync(Flag(input.UserId));
        var known = input.Counterparty is null ? Task.FromResult(true) : database.SetContainsAsync(Recipients(input.UserId), input.Counterparty);
        var senders = input.Counterparty is null ? Task.FromResult(0L) : database.SortedSetLengthAsync(FanIn(input.Counterparty), Ms(now - Hour), double.PositiveInfinity);
        var mine = input.Counterparty is null ? Task.FromResult<double?>(null) : database.SortedSetScoreAsync(FanIn(input.Counterparty), input.UserId.ToString("N"));
        var stepUp = database.KeyExistsAsync(StepUp(input.UserId, input.DeviceId));
        await Task.WhenAll(payments, device, registered, flagged, known, senders, mine, stepUp);

        var recent = (await payments)
            .Select(entry => (At(entry.Score), long.Parse(entry.Element.ToString().Split('|')[1], CultureInfo.InvariantCulture)))
            .ToList();
        var deviceFields = (await device).ToDictionary(entry => entry.Name.ToString(), entry => entry.Value);
        var others = (int)(await senders) - (await mine is { } at && at >= Ms(now - Hour) ? 1 : 0);
        return new FeatureSnapshot(
            recent,
            deviceFields.TryGetValue("boundAt", out var bound) ? At((double)bound) : null,
            deviceFields.TryGetValue("coolingOffUntil", out var cooling) && !cooling.IsNullOrEmpty ? At((double)cooling) : null,
            (await registered) is { IsNullOrEmpty: false } registeredAt ? At((double)registeredAt) : null,
            await known,
            Math.Max(0, others),
            await flagged,
            await stepUp);
    }

    public Task RecordAsync(ScoreInput input, RiskResult result, DateTimeOffset now, bool usedStepUp = false)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(result);
        var batch = Database.CreateBatch();
        var reason = (result.Reason ?? "").Replace('|', '/');
        if (result.Action != RiskAction.StepUp)
        {
            _ = batch.StringSetAsync(Score(input.TransactionId), $"{result.Action}|{result.Score}|{string.Join(',', result.Rules)}|{reason}", Day, flags: CommandFlags.FireAndForget);
        }

        if (usedStepUp)
        {
            _ = batch.KeyDeleteAsync(StepUp(input.UserId, input.DeviceId), CommandFlags.FireAndForget);
        }

        if (result.Action == RiskAction.Allow)
        {
            var payments = Payments(input.UserId);
            _ = batch.SortedSetAddAsync(payments, $"{input.TransactionId:N}|{input.AmountPaisa}", Ms(now), CommandFlags.FireAndForget);
            _ = batch.SortedSetRemoveRangeByScoreAsync(payments, double.NegativeInfinity, Ms(now - Day), flags: CommandFlags.FireAndForget);
            _ = batch.KeyExpireAsync(payments, Day + Hour, CommandFlags.FireAndForget);
            if (input.Counterparty is not null)
            {
                _ = batch.SetAddAsync(Recipients(input.UserId), input.Counterparty, CommandFlags.FireAndForget);
                _ = batch.KeyExpireAsync(Recipients(input.UserId), TimeSpan.FromDays(90), CommandFlags.FireAndForget);
            }
        }
        else if (result.Action is RiskAction.Review or RiskAction.Block)
        {
            _ = batch.StringSetAsync(Flag(input.UserId), "1", Hour, flags: CommandFlags.FireAndForget);
        }

        if (input is { TransactionType: "P2PTransfer", Counterparty: { } recipient })
        {
            AddFanIn(batch, recipient, input.UserId, now);
        }

        batch.Execute();
        return Task.CompletedTask;
    }

    public Task SetDeviceAsync(Guid userId, Guid deviceId, DateTimeOffset boundAt, DateTimeOffset? coolingOffUntil)
    {
        var key = Device(userId, deviceId.ToString());
        var batch = Database.CreateBatch();
        _ = batch.HashSetAsync(key, [new("boundAt", Ms(boundAt)), new("coolingOffUntil", coolingOffUntil is { } until ? Ms(until) : RedisValue.EmptyString)], CommandFlags.FireAndForget);
        _ = batch.KeyExpireAsync(key, TimeSpan.FromDays(60), CommandFlags.FireAndForget);
        batch.Execute();
        return Task.CompletedTask;
    }

    public async Task ConfirmStepUpAsync(Guid userId, Guid? deviceId, DateTimeOffset validUntil)
    {
        var left = validUntil - DateTimeOffset.UtcNow;
        if (left > TimeSpan.Zero)
        {
            await Database.StringSetAsync(StepUp(userId, deviceId?.ToString()), "1", left);
        }
    }

    public async Task SetAccountAsync(Guid userId, string? walletNumber, DateTimeOffset registeredAt)
    {
        await Database.HashSetAsync(Account(userId), "registeredAt", Ms(registeredAt));
        if (walletNumber is not null)
        {
            await SetOwnerAsync(walletNumber, userId);
        }
    }

    public Task SetOwnerAsync(string walletNumber, Guid userId) => Database.StringSetAsync(Owner(walletNumber), userId.ToString("N"));

    public async Task<Guid?> OwnerOfAsync(string walletNumber) =>
        await Database.StringGetAsync(Owner(walletNumber)) is { IsNullOrEmpty: false } owner && Guid.TryParseExact(owner.ToString(), "N", out var id) ? id : null;

    public Task AddFanInAsync(string recipient, Guid senderUserId, DateTimeOffset at)
    {
        var batch = Database.CreateBatch();
        AddFanIn(batch, recipient, senderUserId, at);
        batch.Execute();
        return Task.CompletedTask;
    }

    public async Task<IReadOnlyList<Guid>> SendersAsync(string recipient, DateTimeOffset since) =>
        [.. (await Database.SortedSetRangeByScoreAsync(FanIn(recipient), Ms(since), double.PositiveInfinity)).Select(member => Guid.ParseExact(member.ToString(), "N"))];

    public async Task<IReadOnlyList<Guid>> FlaggedAsync(IReadOnlyList<Guid> userIds)
    {
        ArgumentNullException.ThrowIfNull(userIds);
        var database = Database;
        var checks = userIds.Select(user => (user, database.KeyExistsAsync(Flag(user)))).ToList();
        await Task.WhenAll(checks.Select(check => check.Item2));
        return [.. checks.Where(check => check.Item2.Result).Select(check => check.user)];
    }

    public Task<bool> ClaimRingAsync(string recipient, TimeSpan window) => Database.StringSetAsync(Ring(recipient), "1", window, When.NotExists);

    private static void AddFanIn(IBatch batch, string recipient, Guid senderUserId, DateTimeOffset at)
    {
        var key = FanIn(recipient);
        _ = batch.SortedSetAddAsync(key, senderUserId.ToString("N"), Ms(at), CommandFlags.FireAndForget);
        _ = batch.SortedSetRemoveRangeByScoreAsync(key, double.NegativeInfinity, Ms(at - Hour), flags: CommandFlags.FireAndForget);
        _ = batch.KeyExpireAsync(key, Hour + Hour, CommandFlags.FireAndForget);
    }
}

/// <summary>Readiness: Redis answers a PING.</summary>
internal sealed class RedisHealthCheck(IConnectionMultiplexer redis) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var latency = await redis.GetDatabase().PingAsync();
            return HealthCheckResult.Healthy($"PING {latency.TotalMilliseconds:0.0} ms");
        }
        catch (RedisException exception)
        {
            return HealthCheckResult.Unhealthy("Redis is not reachable.", exception);
        }
    }
}
