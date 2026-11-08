using WalletPK.Common.Results;

namespace WalletPK.Identity.Domain.Kyc;

/// <summary>
/// A Pakistani Computerised National Identity Card number: 13 digits, written 12345-1234567-1
/// (5-digit area code, 7-digit serial, 1 check digit that is odd for men and even for women).
/// Accepts the number with or without dashes and always stores the 13 digits.
/// </summary>
public sealed record Cnic
{
    public const int Length = 13;

    private Cnic(string value)
    {
        Value = value;
    }

    /// <summary>The 13 digits, for example "3520212345671". Sensitive: never log it.</summary>
    public string Value { get; }

    /// <summary>The usual written form, for example "35202-1234567-1".</summary>
    public string Formatted => $"{Value[..5]}-{Value[5..12]}-{Value[12..]}";

    /// <summary>Safe to show and store in clear text, for example "35202-*******-1".</summary>
    public string Masked => $"{Value[..5]}-*******-{Value[12..]}";

    public static Result<Cnic> Create(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return Error.Validation("cnic.required", "Enter your CNIC number.");
        }

        var trimmed = input.Trim();
        if (!trimmed.All(c => char.IsAsciiDigit(c) || c == '-'))
        {
            return Invalid;
        }

        // Dashes are allowed only in the standard places (12345-1234567-1) or not at all.
        var digits = trimmed.Replace("-", string.Empty, StringComparison.Ordinal);
        var dashesOk = trimmed.Length == Length || (trimmed.Length == Length + 2 && trimmed[5] == '-' && trimmed[13] == '-');

        // The first digit is a province code (1-7); a number of one repeated digit is never issued.
        if (!dashesOk || digits.Length != Length || digits[0] is < '1' or > '7' || digits.Distinct().Count() == 1)
        {
            return Invalid;
        }

        return new Cnic(digits);
    }

    /// <summary>Hides the number from ToString, so it never ends up in a log by accident.</summary>
    public override string ToString() => Masked;

    private static Error Invalid { get; } =
        Error.Validation("cnic.invalid", "Enter a valid 13-digit CNIC number, for example 35202-1234567-1.");
}
