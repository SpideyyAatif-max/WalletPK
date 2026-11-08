using System.Globalization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using WalletPK.Web.Errors;
using WalletPK.Web.Identity;

namespace WalletPK.Gateway.RateLimiting;

public static class GatewayRateLimitingExtensions
{
    /// <summary>Policy name for login, sign-up, OTP and refresh routes (referenced in appsettings.json).</summary>
    public const string AuthPolicy = "auth";

    public const string ErrorCode = "rate_limit.exceeded";

    public static IServiceCollection AddGatewayRateLimiting(this IServiceCollection services)
    {
        services.AddOptions<GatewayRateLimitOptions>().BindConfiguration(GatewayRateLimitOptions.SectionName);

        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

            // Token buckets allow short bursts but cap the sustained rate. All three must allow the request.
            options.GlobalLimiter = PartitionedRateLimiter.CreateChained(
                PartitionedRateLimiter.Create<HttpContext, string>(ByIp),
                PartitionedRateLimiter.Create<HttpContext, string>(ByUser),
                PartitionedRateLimiter.Create<HttpContext, string>(ByDevice));

            options.AddPolicy(AuthPolicy, context =>
            {
                var limits = Limits(context);
                return RateLimitPartition.GetFixedWindowLimiter(
                    "auth:" + ClientIp(context),
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = limits.AuthPermitLimit,
                        Window = TimeSpan.FromSeconds(limits.AuthWindowSeconds),
                        QueueLimit = 0,
                    });
            });

            options.OnRejected = WriteRejectionAsync;
        });

        return services;
    }

    private static RateLimitPartition<string> ByIp(HttpContext context)
    {
        if (IsExempt(context))
        {
            return RateLimitPartition.GetNoLimiter("exempt");
        }

        var limits = Limits(context);
        return RateLimitPartition.GetTokenBucketLimiter(
            "ip:" + ClientIp(context),
            _ => Bucket(limits.PerIpTokenLimit, limits.PerIpTokensPerSecond));
    }

    private static RateLimitPartition<string> ByUser(HttpContext context)
    {
        var userId = context.User.FindFirst(WalletPkClaimTypes.Subject)?.Value;
        if (IsExempt(context) || string.IsNullOrEmpty(userId))
        {
            return RateLimitPartition.GetNoLimiter("no-user");
        }

        var limits = Limits(context);
        return RateLimitPartition.GetTokenBucketLimiter(
            "user:" + userId,
            _ => Bucket(limits.PerUserTokenLimit, limits.PerUserTokensPerSecond));
    }

    private static RateLimitPartition<string> ByDevice(HttpContext context)
    {
        var deviceId = context.User.FindFirst(WalletPkClaimTypes.DeviceId)?.Value;
        if (IsExempt(context) || string.IsNullOrEmpty(deviceId))
        {
            return RateLimitPartition.GetNoLimiter("no-device");
        }

        var limits = Limits(context);
        return RateLimitPartition.GetTokenBucketLimiter(
            "device:" + deviceId,
            _ => Bucket(limits.PerDeviceTokenLimit, limits.PerDeviceTokensPerSecond));
    }

    private static TokenBucketRateLimiterOptions Bucket(int tokenLimit, int tokensPerSecond) => new()
    {
        TokenLimit = tokenLimit,
        TokensPerPeriod = tokensPerSecond,
        ReplenishmentPeriod = TimeSpan.FromSeconds(1),
        QueueLimit = 0,
        AutoReplenishment = true,
    };

    // Health probes from Docker and monitoring must never be throttled.
    private static bool IsExempt(HttpContext context) =>
        context.Request.Path.StartsWithSegments("/health", StringComparison.OrdinalIgnoreCase);

    private static string ClientIp(HttpContext context) =>
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown";

    private static GatewayRateLimitOptions Limits(HttpContext context) =>
        context.RequestServices.GetRequiredService<IOptions<GatewayRateLimitOptions>>().Value;

    private static async ValueTask WriteRejectionAsync(OnRejectedContext context, CancellationToken cancellationToken)
    {
        var httpContext = context.HttpContext;
        var retryAfterSeconds = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter)
            ? Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds))
            : 1;

        httpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        httpContext.Response.Headers.RetryAfter = retryAfterSeconds.ToString(CultureInfo.InvariantCulture);

        var problem = new ProblemDetails
        {
            Status = StatusCodes.Status429TooManyRequests,
            Title = "Too many requests",
            Detail = $"Rate limit exceeded. Try again in {retryAfterSeconds} second(s).",
        };
        problem.Extensions[ResultHttpExtensions.CodeExtension] = ErrorCode;

        await httpContext.RequestServices.GetRequiredService<IProblemDetailsService>()
            .WriteAsync(new ProblemDetailsContext { HttpContext = httpContext, ProblemDetails = problem });
    }
}
