using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using WalletPK.Web.Errors;
using WalletPK.Web.Security;

namespace WalletPK.Gateway.Webhooks;

/// <summary>
/// Verifies the HMAC SHA-256 signature of every request under <c>/webhooks/{provider}/</c> before
/// it is forwarded: unknown provider, missing or stale timestamp (older than 5 minutes),
/// oversized body or wrong signature are all rejected with 401.
/// </summary>
public sealed partial class WebhookSignatureMiddleware(
    RequestDelegate next,
    IOptions<WebhookOptions> options,
    TimeProvider timeProvider,
    ILogger<WebhookSignatureMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!context.Request.Path.StartsWithSegments(WebhookOptions.PathPrefix, StringComparison.OrdinalIgnoreCase, out var remaining))
        {
            await next(context);
            return;
        }

        var settings = options.Value;
        var provider = remaining.Value?.Trim('/').Split('/', 2)[0] ?? string.Empty;

        if (provider.Length == 0 || !settings.Secrets.TryGetValue(provider, out var secret) || string.IsNullOrEmpty(secret))
        {
            await RejectAsync(context, provider, "webhook.unknown_provider", "Unknown webhook provider.");
            return;
        }

        if (!long.TryParse(context.Request.Headers[WebhookSignature.TimestampHeader], NumberStyles.None, CultureInfo.InvariantCulture, out var timestamp))
        {
            await RejectAsync(context, provider, "webhook.missing_timestamp", $"The {WebhookSignature.TimestampHeader} header is missing or invalid.");
            return;
        }

        var age = Math.Abs(timeProvider.GetUtcNow().ToUnixTimeSeconds() - timestamp);
        if (age > settings.ToleranceSeconds)
        {
            await RejectAsync(context, provider, "webhook.stale_timestamp", "The webhook timestamp is outside the allowed window.");
            return;
        }

        if (context.Request.ContentLength > settings.MaxBodyBytes)
        {
            await RejectAsync(context, provider, "webhook.too_large", "The webhook body is too large.");
            return;
        }

        // Read the raw body for verification, then rewind it so YARP forwards it unchanged.
        context.Request.EnableBuffering();
        using var buffer = new MemoryStream();
        await context.Request.Body.CopyToAsync(buffer, context.RequestAborted);
        context.Request.Body.Position = 0;

        if (buffer.Length > settings.MaxBodyBytes)
        {
            await RejectAsync(context, provider, "webhook.too_large", "The webhook body is too large.");
            return;
        }

        if (!WebhookSignature.Verify(secret, timestamp, buffer.ToArray(), context.Request.Headers[WebhookSignature.SignatureHeader]))
        {
            await RejectAsync(context, provider, "webhook.invalid_signature", "The webhook signature is invalid.");
            return;
        }

        await next(context);
    }

    private async Task RejectAsync(HttpContext context, string provider, string code, string detail)
    {
        LogRejected(logger, provider, code);

        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        var problem = new ProblemDetails
        {
            Status = StatusCodes.Status401Unauthorized,
            Title = "Webhook rejected",
            Detail = detail,
        };
        problem.Extensions[ResultHttpExtensions.CodeExtension] = code;

        await context.RequestServices.GetRequiredService<IProblemDetailsService>()
            .WriteAsync(new ProblemDetailsContext { HttpContext = context, ProblemDetails = problem });
    }

    [LoggerMessage(EventId = 2000, Level = LogLevel.Warning, Message = "Rejected webhook from provider {Provider}: {Reason}")]
    private static partial void LogRejected(ILogger logger, string provider, string reason);
}

public static class WebhookSignatureExtensions
{
    public static IServiceCollection AddWebhookSignatureValidation(this IServiceCollection services)
    {
        services.AddOptions<WebhookOptions>().BindConfiguration(WebhookOptions.SectionName);
        services.TryAddSingleton(TimeProvider.System);
        return services;
    }

    public static IApplicationBuilder UseWebhookSignatureValidation(this IApplicationBuilder app) =>
        app.UseMiddleware<WebhookSignatureMiddleware>();
}
