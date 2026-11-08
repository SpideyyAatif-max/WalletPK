using Microsoft.Extensions.Diagnostics.HealthChecks;
using StackExchange.Redis;

namespace WalletPK.Identity.Infrastructure.Health;

/// <summary>Readiness: Redis answers PING.</summary>
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
