using System.Diagnostics;
using System.Globalization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;

namespace WalletPK.Web.Correlation;

/// <summary>
/// Gives every request a correlation ID that follows it through all services:
/// reuses a valid incoming <c>X-Correlation-ID</c> header or creates a new one, returns it in the
/// response header, adds it to every log line and tags the current trace with it.
/// </summary>
public sealed class CorrelationIdMiddleware(RequestDelegate next, ILogger<CorrelationIdMiddleware> logger)
{
    /// <summary>HTTP header carrying the correlation ID.</summary>
    public const string HeaderName = "X-Correlation-ID";

    private const int MaxLength = 64;

    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var correlationId = Resolve(context.Request.Headers[HeaderName]);

        CorrelationContext.Current = correlationId;
        context.Items[HeaderName] = correlationId;
        Activity.Current?.SetTag("walletpk.correlation_id", correlationId);

        context.Response.OnStarting(() =>
        {
            context.Response.Headers[HeaderName] = correlationId;
            return Task.CompletedTask;
        });

        using (logger.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = correlationId }))
        {
            await next(context);
        }
    }

    /// <summary>Keeps a safe incoming ID; otherwise creates a new one (32 hex characters).</summary>
    internal static string Resolve(StringValues incoming)
    {
        var value = incoming.ToString();
        return IsValid(value)
            ? value
            : Guid.CreateVersion7().ToString("N", CultureInfo.InvariantCulture);
    }

    // Only short IDs of letters, digits and - _ . : are accepted, so a caller cannot inject
    // control characters or huge values into our logs and headers.
    private static bool IsValid(string value) =>
        value.Length is > 0 and <= MaxLength &&
        value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or ':');
}
