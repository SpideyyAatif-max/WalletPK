using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace WalletPK.Reconciliation.Domain;

/// <summary>
/// The audit log's hash chain (FR-5.3): each entry's hash covers the one before it, so changing, removing or
/// inserting any entry breaks every hash after it.
/// </summary>
public static class AuditChain
{
    /// <summary>The "previous hash" of the first entry.</summary>
    public static readonly string Genesis = new('0', 64);

    /// <summary>To the microsecond, as PostgreSQL keeps it, so a stored entry hashes the same when read back.</summary>
    public static DateTimeOffset Truncate(DateTimeOffset at) => new(at.UtcTicks - (at.UtcTicks % 10), TimeSpan.Zero);

    public static string Hash(string previousHash, string actor, string action, string subject, string details, DateTimeOffset at)
    {
        var text = string.Join('\u001f', previousHash, actor, action, subject, details, at.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }
}

/// <summary>The settlement parties: who sends files, and where to fetch them.</summary>
public static class Parties
{
    public const string BankPrefix = "bank:";
    public const string BillerPrefix = "biller:";
    public const string TelcoPrefix = "telco:";

    public static string Bank(string code) => BankPrefix + code.ToUpperInvariant();

    public static string Biller(string code) => BillerPrefix + code.ToUpperInvariant();

    public static string Telco(string code) => TelcoPrefix + code.ToUpperInvariant();

    /// <summary>Every party the mock providers settle with (M5.6).</summary>
    public static IReadOnlyList<string> Default { get; } =
    [
        Bank("HBL"), Bank("MEEZAN"), Bank("UBL"), Bank("MCB"),
        Biller("KE"), Biller("LESCO"), Biller("SNGPL"), Biller("PTCL"),
        Telco("JAZZ"), Telco("ZONG"), Telco("TELENOR"), Telco("UFONE"),
    ];

    /// <summary>"bank:HBL" → "banks/HBL", the settlement file path; null for anything else.</summary>
    public static string? FilePath(string party) =>
        party.StartsWith(BankPrefix, StringComparison.Ordinal) ? $"banks/{party[BankPrefix.Length..]}"
        : party.StartsWith(BillerPrefix, StringComparison.Ordinal) ? $"billers/{party[BillerPrefix.Length..]}"
        : party.StartsWith(TelcoPrefix, StringComparison.Ordinal) ? $"telcos/{party[TelcoPrefix.Length..]}"
        : null;

    public static bool IsValid(string party) =>
        FilePath(party) is { } path && path.Split('/')[1] is { Length: > 0 and <= 20 } code && code.All(char.IsAsciiLetterOrDigit);
}
