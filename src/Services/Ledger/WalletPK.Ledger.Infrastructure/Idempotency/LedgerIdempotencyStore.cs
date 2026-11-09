using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using StackExchange.Redis;
using WalletPK.Ledger.Infrastructure.Persistence;
using WalletPK.Web.Idempotency;

namespace WalletPK.Ledger.Infrastructure.Idempotency;

/// <summary>
/// Idempotency keys in two places (M3.7):
/// <list type="bullet">
/// <item><b>PostgreSQL</b> (durable, the source of truth): claiming a key is an
/// <c>INSERT ... ON CONFLICT DO NOTHING</c>, so of two identical requests arriving together exactly one
/// wins. The completed response is stored there too, so replays survive a Redis restart.</item>
/// <item><b>Redis</b> (fast path): completed responses are cached, so most replays never touch the database.</item>
/// </list>
/// </summary>
internal sealed class LedgerIdempotencyStore(
    LedgerDbContext dbContext,
    IConnectionMultiplexer redis,
    IdempotencyOptions options,
    TimeProvider timeProvider) : IIdempotencyStore
{
    private TimeSpan Retention => TimeSpan.FromHours(options.RetentionHours);

    private TimeSpan InProgressTimeout => TimeSpan.FromSeconds(options.InProgressTimeoutSeconds);

    public async Task<IdempotencyBegin> TryBeginAsync(string key, string requestHash, CancellationToken cancellationToken)
    {
        // 1. Fast path: a completed response cached in Redis.
        var cached = await redis.GetDatabase().StringGetAsync(CacheKey(key));
        if (!cached.IsNullOrEmpty && JsonSerializer.Deserialize<CachedResponse>(cached.ToString()) is { } hit)
        {
            return hit.RequestHash == requestHash
                ? new IdempotencyBegin(IdempotencyStatus.Completed, hit.Response)
                : new IdempotencyBegin(IdempotencyStatus.Mismatch);
        }

        // 2. Claim the key in PostgreSQL; the primary key lets exactly one concurrent request in.
        var now = timeProvider.GetUtcNow();
        var claimed = await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO idempotency_keys (key, request_hash, state, created_at, expires_at)
            VALUES ({key}, {requestHash}, {IdempotencyRecord.InProgress}, {now}, {now + InProgressTimeout})
            ON CONFLICT (key) DO NOTHING
            """,
            cancellationToken);
        if (claimed == 1)
        {
            return new IdempotencyBegin(IdempotencyStatus.Started);
        }

        // 3. Already known: completed, still running, or abandoned by a crashed server.
        var existing = await dbContext.IdempotencyKeys.AsNoTracking().FirstOrDefaultAsync(record => record.Key == key, cancellationToken);
        if (existing is null)
        {
            return new IdempotencyBegin(IdempotencyStatus.InProgress);
        }

        if (existing.RequestHash != requestHash)
        {
            return new IdempotencyBegin(IdempotencyStatus.Mismatch);
        }

        if (existing.State == IdempotencyRecord.Completed)
        {
            var response = new StoredResponse(existing.StatusCode ?? 200, existing.ContentType, existing.Location, existing.ResponseBody ?? []);
            await CacheAsync(key, requestHash, response);
            return new IdempotencyBegin(IdempotencyStatus.Completed, response);
        }

        if (existing.ExpiresAt <= now)
        {
            // The first attempt's server died mid-request: take the claim over.
            var takenOver = await dbContext.Database.ExecuteSqlInterpolatedAsync(
                $"""
                UPDATE idempotency_keys SET created_at = {now}, expires_at = {now + InProgressTimeout}
                WHERE key = {key} AND state = {IdempotencyRecord.InProgress} AND expires_at <= {now}
                """,
                cancellationToken);
            if (takenOver == 1)
            {
                return new IdempotencyBegin(IdempotencyStatus.Started);
            }
        }

        return new IdempotencyBegin(IdempotencyStatus.InProgress);
    }

    public async Task CompleteAsync(string key, StoredResponse response, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(response);

        var now = timeProvider.GetUtcNow();
        var hash = await dbContext.IdempotencyKeys.AsNoTracking()
            .Where(record => record.Key == key)
            .Select(record => record.RequestHash)
            .FirstOrDefaultAsync(cancellationToken);
        if (hash is null)
        {
            return;
        }

        await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"""
            UPDATE idempotency_keys
            SET state = {IdempotencyRecord.Completed}, status_code = {response.StatusCode}, content_type = {response.ContentType},
                location = {response.Location}, response_body = {response.Body}, expires_at = {now + Retention}
            WHERE key = {key}
            """,
            cancellationToken);
        await CacheAsync(key, hash, response);
    }

    public Task AbandonAsync(string key, CancellationToken cancellationToken) =>
        dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM idempotency_keys WHERE key = {key} AND state = {IdempotencyRecord.InProgress}",
            cancellationToken);

    /// <summary>Deletes completed keys past their retention, and claims abandoned for over a day.</summary>
    public Task<int> PurgeExpiredAsync(CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        return dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"""
            DELETE FROM idempotency_keys
            WHERE (state = {IdempotencyRecord.Completed} AND expires_at < {now})
               OR (state = {IdempotencyRecord.InProgress} AND expires_at < {now - TimeSpan.FromDays(1)})
            """,
            cancellationToken);
    }

    private Task CacheAsync(string key, string requestHash, StoredResponse response) =>
        redis.GetDatabase().StringSetAsync(CacheKey(key), JsonSerializer.Serialize(new CachedResponse(requestHash, response)), Retention);

    private static RedisKey CacheKey(string key) => $"ledger:idempotency:{key}";

    private sealed record CachedResponse(string RequestHash, StoredResponse Response);
}
