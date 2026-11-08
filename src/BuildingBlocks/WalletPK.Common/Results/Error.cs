namespace WalletPK.Common.Results;

/// <summary>
/// A failure described as data instead of an exception.
/// <para><b>Code</b> is stable and machine-readable (for example "wallet.insufficient_balance");
/// <b>Message</b> is for humans and may change.</para>
/// </summary>
public sealed record Error
{
    public Error(string code, string message, ErrorType type, IReadOnlyDictionary<string, string[]>? details = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentNullException.ThrowIfNull(message);

        Code = code;
        Message = message;
        Type = type;
        Details = details;
    }

    /// <summary>Stable, machine-readable code, for example "wallet.insufficient_balance".</summary>
    public string Code { get; }

    /// <summary>Human-readable explanation.</summary>
    public string Message { get; }

    /// <summary>The category, used to choose the HTTP status code.</summary>
    public ErrorType Type { get; }

    /// <summary>Optional per-field validation messages, keyed by field name.</summary>
    public IReadOnlyDictionary<string, string[]>? Details { get; }

    public static Error Failure(string code, string message) => new(code, message, ErrorType.Failure);

    public static Error Validation(string code, string message, IReadOnlyDictionary<string, string[]>? details = null) =>
        new(code, message, ErrorType.Validation, details);

    public static Error NotFound(string code, string message) => new(code, message, ErrorType.NotFound);

    public static Error Conflict(string code, string message) => new(code, message, ErrorType.Conflict);

    public static Error Unauthorized(string code, string message) => new(code, message, ErrorType.Unauthorized);

    public static Error Forbidden(string code, string message) => new(code, message, ErrorType.Forbidden);

    public static Error BusinessRule(string code, string message) => new(code, message, ErrorType.BusinessRule);

    public static Error Unavailable(string code, string message) => new(code, message, ErrorType.Unavailable);

    public override string ToString() => $"{Type}: {Code} - {Message}";
}
