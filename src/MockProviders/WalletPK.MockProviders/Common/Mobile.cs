namespace WalletPK.MockProviders.Common;

/// <summary>Pakistani mobile numbers in any common form → E.164 (+923001234567), or null.</summary>
public static class Mobile
{
    public static string? ToE164(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return null;
        }

        var digits = new string([.. input.Where(c => char.IsAsciiDigit(c))]);
        var national = digits switch
        {
            { Length: 11 } when digits.StartsWith("03", StringComparison.Ordinal) => digits[1..],
            { Length: 12 } when digits.StartsWith("923", StringComparison.Ordinal) => digits[2..],
            { Length: 10 } when digits.StartsWith('3') => digits,
            _ => null,
        };
        return national is null ? null : "+92" + national;
    }
}
