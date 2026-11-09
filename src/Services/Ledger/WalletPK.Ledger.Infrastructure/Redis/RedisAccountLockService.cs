using System.Diagnostics;
using StackExchange.Redis;
using WalletPK.Ledger.Application.Abstractions;
using WalletPK.Ledger.Domain.Accounts;

namespace WalletPK.Ledger.Infrastructure.Redis;

/// <summary>
/// Redis wallet locks (M3.6):
/// <list type="bullet">
/// <item><c>ledger:lock:{account}</c>: SET NX PX with a random owner value: only one holder, and it expires
/// by itself if the holder crashes.</item>
/// <item><c>ledger:fence:{account}</c>: INCR on every successful lock, giving an ever-growing fencing token
/// that the account row checks on write.</item>
/// </list>
/// Lock-and-INCR and compare-and-delete are Lua scripts, so each is atomic. Several wallets are locked in
/// ascending ID order, so two transfers between the same wallets (A→B and B→A) can never deadlock.
/// </summary>
internal sealed class RedisAccountLockService(IConnectionMultiplexer redis, WalletLockOptions options) : IAccountLockService
{
    // Returns the new fencing token if the lock was taken, 0 if someone else holds it.
    private const string AcquireScript = """
        if redis.call('SET', KEYS[1], ARGV[1], 'NX', 'PX', ARGV[2]) then
          return redis.call('INCR', KEYS[2])
        end
        return 0
        """;

    // Deletes the lock only if we still own it (it may have expired and been taken by someone else).
    private const string ReleaseScript = """
        if redis.call('GET', KEYS[1]) == ARGV[1] then
          return redis.call('DEL', KEYS[1])
        end
        return 0
        """;

    public async Task<IAccountLockLease?> TryAcquireAsync(IReadOnlyCollection<AccountId> accountIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(accountIds);

        var database = redis.GetDatabase();
        var owner = Guid.NewGuid().ToString("N");
        var acquired = new Dictionary<AccountId, long>();
        var waited = Stopwatch.StartNew();

        foreach (var accountId in accountIds.Distinct().OrderBy(id => id.Value))
        {
            while (true)
            {
                var token = (long)await database.ScriptEvaluateAsync(
                    AcquireScript,
                    [LockKey(accountId), FenceKey(accountId)],
                    [owner, options.TimeToLiveMilliseconds]);

                if (token > 0)
                {
                    acquired[accountId] = token;
                    break;
                }

                if (waited.ElapsedMilliseconds >= options.WaitMilliseconds)
                {
                    await ReleaseAllAsync(database, owner, acquired.Keys);
                    return null;
                }

                await Task.Delay(Random.Shared.Next(5, 30), cancellationToken);
            }
        }

        return new Lease(database, owner, acquired);
    }

    internal static RedisKey LockKey(AccountId accountId) => $"ledger:lock:{accountId}";

    internal static RedisKey FenceKey(AccountId accountId) => $"ledger:fence:{accountId}";

    private static async Task ReleaseAllAsync(IDatabase database, string owner, IEnumerable<AccountId> accountIds)
    {
        foreach (var accountId in accountIds)
        {
            try
            {
                await database.ScriptEvaluateAsync(ReleaseScript, [LockKey(accountId)], [owner]);
            }
            catch (RedisException)
            {
                // The lock expires by itself; a failed release only delays the next payment a little.
            }
        }
    }

    private sealed class Lease(IDatabase database, string owner, Dictionary<AccountId, long> tokens) : IAccountLockLease
    {
        public IReadOnlyDictionary<AccountId, long> FencingTokens => tokens;

        public async ValueTask DisposeAsync() => await ReleaseAllAsync(database, owner, tokens.Keys);
    }
}
