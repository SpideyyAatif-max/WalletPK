using WalletPK.Common.Results;

namespace WalletPK.Identity.Domain.Users;

/// <summary>
/// A Pakistani mobile number, always stored in E.164 form (+923001234567) whatever the user typed:
/// "0300 1234567", "0300-1234567", "+92 300 1234567" and "923001234567" are all the same number.
/// </summary>
public sealed record PhoneNumber
{
    private const string CountryCode = "+92";

    private PhoneNumber(string value)
    {
        Value = value;
    }

    /// <summary>E.164 form, for example "+923001234567".</summary>
    public string Value { get; }

    /// <summary>Safe for logs and API responses, for example "+92300*****67".</summary>
    public string Masked => $"{Value[..6]}*****{Value[^2..]}";

    public static Result<PhoneNumber> Create(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return Error.Validation("phone.required", "Enter your mobile number.");
        }

        // Keep digits only (drops +, spaces, dashes and brackets).
        var digits = new string(input.Where(char.IsAsciiDigit).ToArray());

        if (digits.StartsWith("0092", StringComparison.Ordinal) && digits.Length == 14)
        {
            digits = digits[4..];
        }
        else if (digits.StartsWith("92", StringComparison.Ordinal) && digits.Length == 12)
        {
            digits = digits[2..];
        }
        else if (digits.StartsWith('0') && digits.Length == 11)
        {
            digits = digits[1..];
        }

        // Pakistani mobile numbers: 3XX followed by 7 digits.
        if (digits.Length != 10 || digits[0] != '3')
        {
            return Error.Validation("phone.invalid", "Enter a Pakistani mobile number, for example 0300 1234567.");
        }

        return new PhoneNumber(CountryCode + digits);
    }

    public override string ToString() => Value;
}
