namespace WalletPK.MockProviders.Telcos;

/// <summary>A data/minutes bundle.</summary>
public sealed record Bundle(string Code, string Name, long PricePaisa, int ValidityDays);

/// <summary>A mobile network.</summary>
/// <param name="Code">"JAZZ", "ZONG", "TELENOR", "UFONE".</param>
/// <param name="Name">Display name.</param>
/// <param name="Prefixes">Its number ranges ("300" for 0300-xxxxxxx).</param>
/// <param name="Bundles">Bundles it sells.</param>
public sealed record Telco(string Code, string Name, IReadOnlyList<string> Prefixes, IReadOnlyList<Bundle> Bundles);

/// <summary>
/// The mock mobile networks (M5.4). A number belongs to a network by its prefix (number portability is
/// not simulated). Airtime top-ups are whole rupees from Rs. 20 to Rs. 5,000. Test value: a number
/// ending in 0000 is inactive (top-ups refused).
/// </summary>
public static class TelcoCatalog
{
    public const long MinTopUpPaisa = 2_000;
    public const long MaxTopUpPaisa = 500_000;

    private static IEnumerable<string> Range(int from, int to) => Enumerable.Range(from, to - from + 1).Select(prefix => prefix.ToString(System.Globalization.CultureInfo.InvariantCulture));

    public static IReadOnlyList<Telco> All { get; } =
    [
        new("JAZZ", "Jazz", [.. Range(300, 309), .. Range(320, 329)],
        [
            new("JAZZ-DAILY-2GB", "Daily 2 GB", 5_000, 1),
            new("JAZZ-WEEKLY-ALLROUNDER", "Weekly All-Rounder", 33_000, 7),
            new("JAZZ-MONTHLY-SUPREME", "Monthly Supreme", 150_000, 30),
        ]),
        new("ZONG", "Zong", [.. Range(310, 319)],
        [
            new("ZONG-DAILY-SUPER", "Daily Super", 4_500, 1),
            new("ZONG-WEEKLY-PREMIUM", "Weekly Premium", 30_000, 7),
            new("ZONG-MONTHLY-MEGA", "Monthly Mega", 135_000, 30),
        ]),
        new("TELENOR", "Telenor", [.. Range(340, 349)],
        [
            new("TELENOR-DAILY-EASYCARD", "Daily Easy Card", 4_000, 1),
            new("TELENOR-WEEKLY-EASYCARD", "Weekly Easy Card", 29_000, 7),
            new("TELENOR-MONTHLY-EASYCARD", "Monthly Easy Card", 140_000, 30),
        ]),
        new("UFONE", "Ufone", [.. Range(330, 339)],
        [
            new("UFONE-DAILY-SUPER", "Daily Super Card", 4_000, 1),
            new("UFONE-WEEKLY-POWER", "Weekly Power Pack", 28_000, 7),
            new("UFONE-MONTHLY-SUPER", "Monthly Super Card", 120_000, 30),
        ]),
    ];

    public static Telco? ByCode(string code) => All.FirstOrDefault(telco => telco.Code.Equals(code, StringComparison.OrdinalIgnoreCase));

    /// <summary>The network a +92 number belongs to, by prefix.</summary>
    public static Telco? NetworkOf(string e164) => e164.Length == 13 ? All.FirstOrDefault(telco => telco.Prefixes.Contains(e164[3..6])) : null;

    public static bool Inactive(string e164) => e164.EndsWith("0000", StringComparison.Ordinal);
}
