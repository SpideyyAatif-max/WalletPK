using System.Text.Json;
using StackExchange.Redis;
using WalletPK.Identity.Application.Abstractions;

namespace WalletPK.Identity.Infrastructure.Redis;

/// <summary>Pending new-device logins in Redis as small JSON values that expire by themselves.</summary>
internal sealed class RedisDeviceChallengeStore(IConnectionMultiplexer redis) : IDeviceChallengeStore
{
    private IDatabase Database => redis.GetDatabase();

    public Task SaveAsync(string challengeId, DeviceChallenge challenge, TimeSpan timeToLive, CancellationToken cancellationToken) =>
        Database.StringSetAsync(Key(challengeId), JsonSerializer.Serialize(challenge), timeToLive);

    public async Task<DeviceChallenge?> GetAsync(string challengeId, CancellationToken cancellationToken)
    {
        var value = await Database.StringGetAsync(Key(challengeId));
        return value.IsNullOrEmpty ? null : JsonSerializer.Deserialize<DeviceChallenge>(value.ToString());
    }

    public Task DeleteAsync(string challengeId, CancellationToken cancellationToken) =>
        Database.KeyDeleteAsync(Key(challengeId));

    private static RedisKey Key(string challengeId) => $"identity:device-challenge:{challengeId}";
}
