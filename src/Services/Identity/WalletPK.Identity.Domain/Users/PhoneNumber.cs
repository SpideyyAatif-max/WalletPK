using WalletPK.Common.Primitives;
using WalletPK.Common.Results;

namespace WalletPK.Identity.Domain.Users;

/// <summary>
/// A Pakistani mobile number, always stored in E.164 form (+923001234567) whatever the user typed:
/// "0300 1234567", "0300-1234567", "+92 300 1234567" and "923001234567" are all the same number.
/// </summary>
public sealed record PhoneNumber
{
    private PhoneNumber(string value)
    {
        Value = value;
    }

    /// <summary>E.164 form, for example "+923001234567".</summary>
    public string Value { get; }

    /// <summary>Safe for logs and API responses, for example "+92300*****67".</summary>
    public string Masked => PakistaniMobileNumber.Mask(Value);

    public static Result<PhoneNumber> Create(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return Error.Validation("phone.required", "Enter your mobile number.");
        }

        return PakistaniMobileNumber.TryNormalize(input, out var e164)
            ? new PhoneNumber(e164)
            : Error.Validation("phone.invalid", "Enter a Pakistani mobile number, for example 0300 1234567.");
    }

    public override string ToString() => Value;
}
