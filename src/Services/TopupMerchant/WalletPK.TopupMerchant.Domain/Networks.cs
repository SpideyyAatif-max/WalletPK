namespace WalletPK.TopupMerchant.Domain;

/// <summary>A mobile network WalletPK tops up.</summary>
/// <param name="Code">"JAZZ", "ZONG", "TELENOR", "UFONE" (also its circuit breaker and bulkhead).</param>
/// <param name="Name">Display name.</param>
/// <param name="Prefixes">The 3-digit codes after +92 its numbers start with ("300").</param>
public sealed record Network(string Code, string Name, IReadOnlyList<string> Prefixes);

/// <summary>The networks and which numbers belong to which (M8.1).</summary>
public static class Networks
{
    private static IEnumerable<string> Range(int from, int to) =>
        Enumerable.Range(from, to - from + 1).Select(prefix => prefix.ToString(System.Globalization.CultureInfo.InvariantCulture));

    public static IReadOnlyList<Network> All { get; } =
    [
        new("JAZZ", "Jazz", [.. Range(300, 309), .. Range(320, 329)]),
        new("ZONG", "Zong", [.. Range(310, 319)]),
        new("UFONE", "Ufone", [.. Range(330, 339)]),
        new("TELENOR", "Telenor", [.. Range(340, 349)]),
    ];

    public static Network? ByCode(string? code) =>
        code is null ? null : All.FirstOrDefault(network => network.Code.Equals(code, StringComparison.OrdinalIgnoreCase));

    /// <summary>The network of an E.164 number ("+923001234567"), by its prefix (Pakistan has no number portability here).</summary>
    public static Network? Of(string e164)
    {
        ArgumentNullException.ThrowIfNull(e164);
        return e164.Length == 13 && e164.StartsWith("+92", StringComparison.Ordinal) ? All.FirstOrDefault(network => network.Prefixes.Contains(e164[3..6])) : null;
    }
}

public static class TopUpRules
{
    /// <summary>Airtime is whole rupees from Rs. 20 ...</summary>
    public const long MinAirtimePaisa = 2_000;

    /// <summary>... to Rs. 5,000.</summary>
    public const long MaxAirtimePaisa = 500_000;

    public static bool IsValidAirtime(long paisa) => paisa is >= MinAirtimePaisa and <= MaxAirtimePaisa && paisa % 100 == 0;
}

/// <summary>What the customer sees of a top-up.</summary>
public static class TopUpStatus
{
    public const string Processing = "Processing";

    /// <summary>The network has not confirmed it; our team is checking (the money stays reserved).</summary>
    public const string UnderReview = "UnderReview";

    public const string Completed = "Completed";

    public const string Failed = "Failed";
}

/// <summary>The top-up Saga's states.</summary>
public static class TopUpStates
{
    public const string Reserving = "Reserving";

    public const string ToppingUp = "ToppingUp";

    /// <summary>The network is unavailable; trying again shortly.</summary>
    public const string Retrying = "Retrying";

    /// <summary>No answer from the network: asking it whether the top-up arrived before trying again.</summary>
    public const string Verifying = "Verifying";

    public const string Settling = "Settling";

    public const string Returning = "Returning";

    /// <summary>Still unconfirmed after the verification window: topup.dlq, for a person.</summary>
    public const string DeadLettered = "DeadLettered";

    public const string Completed = "Completed";

    public const string Failed = "Failed";
}
