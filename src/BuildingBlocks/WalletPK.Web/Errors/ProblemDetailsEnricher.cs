using System.Diagnostics;
using Microsoft.AspNetCore.Http;
using WalletPK.Common.Correlation;
using WalletPK.Web.Correlation;

namespace WalletPK.Web.Errors;

/// <summary>
/// Adds the same diagnostic fields to every error response, whichever code produced it:
/// <c>instance</c> (method and path), <c>traceId</c> (find the request in Jaeger) and
/// <c>correlationId</c> (find every log line of the user action in Seq).
/// </summary>
public static class ProblemDetailsEnricher
{
    public const string TraceIdExtension = "traceId";
    public const string CorrelationIdExtension = "correlationId";

    public static void Enrich(ProblemDetailsContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var httpContext = context.HttpContext;
        var problem = context.ProblemDetails;

        problem.Instance ??= $"{httpContext.Request.Method} {httpContext.Request.Path}";
        problem.Extensions.TryAdd(TraceIdExtension, Activity.Current?.TraceId.ToString() ?? httpContext.TraceIdentifier);

        var correlationId = httpContext.Items[CorrelationIdMiddleware.HeaderName] as string ?? CorrelationContext.Current;
        if (correlationId is not null)
        {
            problem.Extensions.TryAdd(CorrelationIdExtension, correlationId);
        }
    }
}
