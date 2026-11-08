using StackExchange.Redis;
using WalletPK.Identity.Application.Abstractions;
using WalletPK.Identity.Domain.Users;

namespace WalletPK.Identity.Infrastructure.Redis;

/// <summary>Registration tokens in Redis; GETDEL makes each token usable exactly once.</summary>
internal sealed class RedisRegistrationTokenStore(IConnectionMultiplexer redis) : IRegistrationTokenStore
{
    public Task SaveAsync(string token, PhoneNumber phoneNumber, TimeSpan timeToLive, CancellationToken cancellationToken) =>
        redis.GetDatabase().StringSetAsync(Key(token), phoneNumber.Value, timeToLive);

    public async Task<PhoneNumber?> ConsumeAsync(string token, CancellationToken cancellationToken)
    {
        var value = await redis.GetDatabase().StringGetDeleteAsync(Key(token));
        if (value.IsNullOrEmpty)
        {
            return null;
        }

        var phone = PhoneNumber.Create(value.ToString());
        return phone.IsSuccess ? phone.Value : null;
    }

    private static RedisKey Key(string token) => $"identity:registration-token:{token}";
}
