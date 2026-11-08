using Microsoft.AspNetCore.Http;
using WalletPK.Common.Results;

namespace WalletPK.Web.Errors;

/// <summary>Maps domain error categories to HTTP status codes, in one place for every service.</summary>
public static class ErrorTypeExtensions
{
    public static int ToStatusCode(this ErrorType type) => type switch
    {
        ErrorType.Validation => StatusCodes.Status400BadRequest,
        ErrorType.Unauthorized => StatusCodes.Status401Unauthorized,
        ErrorType.Forbidden => StatusCodes.Status403Forbidden,
        ErrorType.NotFound => StatusCodes.Status404NotFound,
        ErrorType.Conflict => StatusCodes.Status409Conflict,
        ErrorType.BusinessRule => StatusCodes.Status422UnprocessableEntity,
        ErrorType.Unavailable => StatusCodes.Status503ServiceUnavailable,
        _ => StatusCodes.Status500InternalServerError,
    };

    public static string ToTitle(this ErrorType type) => type switch
    {
        ErrorType.Validation => "One or more validation errors occurred",
        ErrorType.Unauthorized => "Authentication required",
        ErrorType.Forbidden => "Not allowed",
        ErrorType.NotFound => "Resource not found",
        ErrorType.Conflict => "Conflict with the current state",
        ErrorType.BusinessRule => "Business rule violated",
        ErrorType.Unavailable => "Service temporarily unavailable",
        _ => "An unexpected error occurred",
    };
}
