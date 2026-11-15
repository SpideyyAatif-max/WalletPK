namespace WalletPK.Payments.Domain;

/// <summary>A bank WalletPK can send to over Raast.</summary>
/// <param name="Code">WalletPK's short code, used in settings, metrics and the circuit breakers ("HBL").</param>
/// <param name="Name">Display name.</param>
/// <param name="IbanCode">The 4 letters in its IBANs ("HABB").</param>
public sealed record RaastBank(string Code, string Name, string IbanCode);

/// <summary>The banks on the Raast network (each has its own circuit breaker and bulkhead).</summary>
public static class RaastBanks
{
    public static IReadOnlyList<RaastBank> All { get; } =
    [
        new("HBL", "Habib Bank Limited", "HABB"),
        new("MEEZAN", "Meezan Bank", "MEZN"),
        new("UBL", "United Bank Limited", "UNIL"),
        new("MCB", "MCB Bank", "MUCB"),
    ];

    public static RaastBank? ByCode(string? code) =>
        code is null ? null : All.FirstOrDefault(bank => bank.Code.Equals(code, StringComparison.OrdinalIgnoreCase));

    public static RaastBank? ByIbanCode(string ibanCode) => All.FirstOrDefault(bank => bank.IbanCode == ibanCode);

    /// <summary>The bank of a normalized IBAN, or null when it is not on the network.</summary>
    public static RaastBank? ForIban(string iban) => ByIbanCode(Iban.BankCodeOf(iban));
}
