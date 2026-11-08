using Microsoft.AspNetCore.Http;
using WalletPK.Common.Results;

namespace WalletPK.Web.Errors;

/// <summary>
/// Turns <see cref="Result"/> and <see cref="Error"/> into HTTP responses for Minimal API endpoints.
/// Failures become RFC 9457 ProblemDetails with a machine-readable <c>code</c>, for example:
/// <code>{ "status": 422, "title": "Business rule violated", "code": "wallet.insufficient_balance", ... }</code>
/// </summary>
public static class ResultHttpExtensions
{
    /// <summary>Error extension field holding the stable error code.</summary>
    public const string CodeExtension = "code";

    /// <summary>Converts an error to a ProblemDetails response with the matching status code.</summary>
    public static IResult ToProblem(this Error error)
    {
        ArgumentNullException.ThrowIfNull(error);

        var extensions = new Dictionary<string, object?> { [CodeExtension] = error.Code };

        if (error.Type == ErrorType.Validation && error.Details is { Count: > 0 } details)
        {
            return TypedResults.ValidationProblem(
                details,
                detail: error.Message,
                title: error.Type.ToTitle(),
                extensions: extensions);
        }

        return TypedResults.Problem(
            detail: error.Message,
            statusCode: error.Type.ToStatusCode(),
            title: error.Type.ToTitle(),
            extensions: extensions);
    }

    /// <summary>Success becomes 204 No Content; failure becomes ProblemDetails.</summary>
    public static IResult ToHttpResult(this Result result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return result.IsSuccess ? TypedResults.NoContent() : result.Error.ToProblem();
    }

    /// <summary>Success becomes 200 OK with the value; failure becomes ProblemDetails.</summary>
    public static IResult ToHttpResult<T>(this Result<T> result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return result.IsSuccess ? TypedResults.Ok(result.Value) : result.Error.ToProblem();
    }

    /// <summary>Success uses a custom response (for example 201 Created); failure becomes ProblemDetails.</summary>
    public static IResult ToHttpResult<T>(this Result<T> result, Func<T, IResult> onSuccess)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(onSuccess);
        return result.IsSuccess ? onSuccess(result.Value) : result.Error.ToProblem();
    }
}
