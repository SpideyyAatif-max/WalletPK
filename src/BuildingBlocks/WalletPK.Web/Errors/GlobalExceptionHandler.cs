using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace WalletPK.Web.Errors;

/// <summary>
/// Last line of defence for exceptions nobody handled. Logs the full exception, then returns a
/// safe ProblemDetails response: the exception message is shown only in Development and a stack
/// trace is never sent to clients. Expected business failures should use <c>Result</c> instead.
/// </summary>
public sealed partial class GlobalExceptionHandler(
    IProblemDetailsService problemDetailsService,
    IHostEnvironment environment,
    ILogger<GlobalExceptionHandler> logger) : IExceptionHandler
{
    /// <summary>Non-standard status used when the client disconnected before we could answer.</summary>
    public const int ClientClosedRequest = 499;

    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(exception);

        if (exception is OperationCanceledException && httpContext.RequestAborted.IsCancellationRequested)
        {
            LogRequestAborted(logger, httpContext.Request.Method, httpContext.Request.Path);
            httpContext.Response.StatusCode = ClientClosedRequest;
            return true;
        }

        var (status, title) = exception switch
        {
            BadHttpRequestException badRequest => (badRequest.StatusCode, "Bad request"),
            _ => (StatusCodes.Status500InternalServerError, "An unexpected error occurred"),
        };

        if (status >= StatusCodes.Status500InternalServerError)
        {
            LogUnhandledException(logger, exception, httpContext.Request.Method, httpContext.Request.Path);
        }
        else
        {
            LogBadRequest(logger, exception.Message, httpContext.Request.Method, httpContext.Request.Path);
        }

        httpContext.Response.StatusCode = status;

        return await problemDetailsService.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            Exception = exception,
            ProblemDetails = new ProblemDetails
            {
                Status = status,
                Title = title,
                Detail = environment.IsDevelopment() ? exception.Message : null,
            },
        });
    }

    [LoggerMessage(EventId = 1000, Level = LogLevel.Error, Message = "Unhandled exception while processing {Method} {Path}")]
    private static partial void LogUnhandledException(ILogger logger, Exception exception, string method, string path);

    [LoggerMessage(EventId = 1001, Level = LogLevel.Warning, Message = "Bad request ({Reason}) for {Method} {Path}")]
    private static partial void LogBadRequest(ILogger logger, string reason, string method, string path);

    [LoggerMessage(EventId = 1002, Level = LogLevel.Information, Message = "Client closed the connection during {Method} {Path}")]
    private static partial void LogRequestAborted(ILogger logger, string method, string path);
}
