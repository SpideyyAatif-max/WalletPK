using StackExchange.Redis;
using WalletPK.Identity.Application.Abstractions;
using WalletPK.Identity.Domain.Users;

namespace WalletPK.Identity.Infrastructure.Redis;

/// <summary>
/// OTPs in Redis: one hash per (purpose, phone) holding the code hash and wrong-guess count,
/// with a TTL so expired codes disappear by themselves. The resend cooldown is a separate key
/// created with SET NX EX, which is atomic even if two requests arrive at once.
/// </summary>
internal sealed class RedisOtpStore(IConnectionMultiplexer redis) : IOtpStore
{
    private const string HashField = "hash";
    private const string FailedField = "failed";

    private IDatabase Database => redis.GetDatabase();

    public async Task<TimeSpan?> TryBeginCooldownAsync(OtpPurpose purpose, PhoneNumber phoneNumber, TimeSpan cooldown, CancellationToken cancellationToken)
    {
        var key = CooldownKey(purpose, phoneNumber);
        if (await Database.StringSetAsync(key, "1", cooldown, When.NotExists))
        {
            return null;
        }

        return await Database.KeyTimeToLiveAsync(key) ?? cooldown;
    }

    public async Task SaveAsync(OtpPurpose purpose, PhoneNumber phoneNumber, string codeHash, TimeSpan timeToLive, CancellationToken cancellationToken)
    {
        var key = CodeKey(purpose, phoneNumber);
        var transaction = Database.CreateTransaction();
        _ = transaction.KeyDeleteAsync(key);
        _ = transaction.HashSetAsync(key, [new HashEntry(HashField, codeHash), new HashEntry(FailedField, 0)]);
        _ = transaction.KeyExpireAsync(key, timeToLive);
        await transaction.ExecuteAsync();
    }

    public async Task<OtpRecord?> GetAsync(OtpPurpose purpose, PhoneNumber phoneNumber, CancellationToken cancellationToken)
    {
        var entries = await Database.HashGetAllAsync(CodeKey(purpose, phoneNumber));
        if (entries.Length == 0)
        {
            return null;
        }

        var values = entries.ToDictionary(entry => entry.Name.ToString(), entry => entry.Value);
        if (!values.TryGetValue(HashField, out var hash) || hash.IsNullOrEmpty)
        {
            return null;
        }

        var failed = values.TryGetValue(FailedField, out var count) && count.TryParse(out int parsed) ? parsed : 0;
        return new OtpRecord(hash.ToString(), failed);
    }

    public async Task<int> IncrementFailedAttemptsAsync(OtpPurpose purpose, PhoneNumber phoneNumber, CancellationToken cancellationToken) =>
        (int)await Database.HashIncrementAsync(CodeKey(purpose, phoneNumber), FailedField);

    public Task DeleteAsync(OtpPurpose purpose, PhoneNumber phoneNumber, CancellationToken cancellationToken) =>
        Database.KeyDeleteAsync(CodeKey(purpose, phoneNumber));

    internal static RedisKey CodeKey(OtpPurpose purpose, PhoneNumber phoneNumber) =>
        $"identity:otp:{purpose.ToString().ToUpperInvariant()}:{phoneNumber.Value}";

    private static RedisKey CooldownKey(OtpPurpose purpose, PhoneNumber phoneNumber) =>
        $"identity:otp-cooldown:{purpose.ToString().ToUpperInvariant()}:{phoneNumber.Value}";
}
