namespace WalletPK.Common.Results;

/// <summary>
/// The category of a failure. The API layer maps each category to an HTTP status
/// (Validation = 400, NotFound = 404, Conflict = 409, BusinessRule = 422, ...).
/// </summary>
public enum ErrorType
{
    /// <summary>An unexpected failure.</summary>
    Failure = 0,

    /// <summary>The request is malformed or a value is invalid.</summary>
    Validation,

    /// <summary>The requested resource does not exist.</summary>
    NotFound,

    /// <summary>The request conflicts with the current state (for example a duplicate or a concurrency clash).</summary>
    Conflict,

    /// <summary>The caller is not authenticated.</summary>
    Unauthorized,

    /// <summary>The caller is authenticated but not allowed to do this.</summary>
    Forbidden,

    /// <summary>A business rule was broken (for example insufficient balance or a limit exceeded).</summary>
    BusinessRule,

    /// <summary>A dependency (bank, biller, database) is temporarily unavailable.</summary>
    Unavailable,
}
