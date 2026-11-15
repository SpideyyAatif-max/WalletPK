using WalletPK.MockProviders.Common;

namespace WalletPK.MockProviders.Banks;

/// <summary>A bank on the simulated Raast network.</summary>
/// <param name="Code">Short code used in faults and settlement files ("HBL").</param>
/// <param name="Name">Display name.</param>
/// <param name="IbanCode">The 4 letters in its IBANs ("HABB").</param>
public sealed record Bank(string Code, string Name, string IbanCode);

/// <summary>Account states, decided by the account number's last four digits (see <see cref="BankDirectory"/>).</summary>
public static class AccountStatus
{
    public const string Active = "Active";
    public const string Closed = "Closed";
    public const string Blocked = "Blocked";
}

/// <summary>A bank account as title fetch shows it.</summary>
public sealed record BankAccount(string Iban, Bank Bank, string Title, string Status);

/// <summary>
/// The simulated banks and their accounts. There is no account table: every valid IBAN of these banks is
/// an account, with a title derived from the IBAN, so any test can make up accounts. Test values:
/// <list type="table">
/// <item><term>account number ends in 0000</term><description>closed: transfers are rejected</description></item>
/// <item><term>account number ends in 9999</term><description>blocked: transfers are rejected</description></item>
/// <item><term>mobile number ends in 000</term><description>has no Raast ID (alias lookup: not found)</description></item>
/// </list>
/// </summary>
public static class BankDirectory
{
    public static IReadOnlyList<Bank> All { get; } =
    [
        new("HBL", "Habib Bank Limited", "HABB"),
        new("MEEZAN", "Meezan Bank", "MEZN"),
        new("UBL", "United Bank Limited", "UNIL"),
        new("MCB", "MCB Bank", "MUCB"),
    ];

    public static Bank? ByCode(string code) => All.FirstOrDefault(bank => bank.Code.Equals(code, StringComparison.OrdinalIgnoreCase));

    public static Bank? ByIbanCode(string ibanCode) => All.FirstOrDefault(bank => bank.IbanCode == ibanCode);

    /// <summary>The account behind a normalized IBAN, or null when the IBAN's bank is not on the network.</summary>
    public static BankAccount? Account(string iban)
    {
        if (ByIbanCode(Iban.BankCodeOf(iban)) is not { } bank)
        {
            return null;
        }

        var number = Iban.AccountNumberOf(iban);
        var status = number.EndsWith("0000", StringComparison.Ordinal) ? AccountStatus.Closed
            : number.EndsWith("9999", StringComparison.Ordinal) ? AccountStatus.Blocked
            : AccountStatus.Active;
        return new BankAccount(iban, bank, PakistaniNames.For("account", iban), status);
    }

    /// <summary>
    /// Raast ID lookup: the account a mobile number (E.164) is linked to, or null when it has none. The
    /// bank and account number are derived from the number, so the answer never changes.
    /// </summary>
    public static BankAccount? ByAlias(string mobileE164)
    {
        if (mobileE164.EndsWith("000", StringComparison.Ordinal))
        {
            return null;
        }

        var bank = Deterministic.Pick(All, "alias-bank", mobileE164);
        var digits = new string([.. mobileE164.Where(char.IsAsciiDigit)]); // 923001234567
        var accountNumber = ("0000" + digits)[^16..];
        return Account(Iban.For(bank.IbanCode, accountNumber));
    }
}
