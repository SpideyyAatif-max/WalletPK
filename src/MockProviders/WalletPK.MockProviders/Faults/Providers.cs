namespace WalletPK.MockProviders.Faults;

/// <summary>
/// Every simulated outside party, by the key used for faults and settlement files:
/// "raast", "bank:HBL", "biller:KE", "telco:JAZZ", "nadra".
/// </summary>
public static class Providers
{
    public const string Raast = "raast";
    public const string Nadra = "nadra";

    public static string Bank(string code) => $"bank:{code}";

    public static string Biller(string code) => $"biller:{code}";

    public static string Telco(string code) => $"telco:{code}";

    public static IReadOnlyList<string> All { get; } =
    [
        Raast,
        .. Banks.BankDirectory.All.Select(bank => Bank(bank.Code)),
        .. Billers.BillerCatalog.All.Select(biller => Biller(biller.Code)),
        .. Telcos.TelcoCatalog.All.Select(telco => Telco(telco.Code)),
        Nadra,
    ];

    public static bool IsKnown(string provider) => All.Contains(provider, StringComparer.Ordinal);
}
