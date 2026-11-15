using System.Security.Cryptography;
using System.Text;

namespace WalletPK.MockProviders.Common;

/// <summary>
/// Stable "random" values derived from inputs (an IBAN, a consumer number, a month), so the same account
/// always has the same title and the same consumer the same bill, without seeding a database.
/// </summary>
public static class Deterministic
{
    public static ulong Hash(params string[] parts)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\u001f', parts)));
        return BitConverter.ToUInt64(bytes, 0);
    }

    /// <summary>A number in [min, max], the same every time for the same parts.</summary>
    public static long Between(long min, long max, params string[] parts)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(min, max);
        return min + (long)(Hash(parts) % (ulong)(max - min + 1));
    }

    public static T Pick<T>(IReadOnlyList<T> items, params string[] parts)
    {
        ArgumentNullException.ThrowIfNull(items);
        return items[(int)(Hash(parts) % (ulong)items.Count)];
    }

    /// <summary>A seeded random generator (for settlement-file mismatches: same plan, same file).</summary>
    public static Random Random(params string[] parts) => new(unchecked((int)Hash(parts)));
}

/// <summary>Pakistani names for account titles, bill holders and the like.</summary>
public static class PakistaniNames
{
    public static readonly IReadOnlyList<string> Given =
    [
        "Muhammad Ali", "Ayesha", "Ahmed", "Fatima", "Hassan", "Zainab", "Usman", "Maryam", "Bilal", "Hira",
        "Imran", "Sana", "Hamza", "Amna", "Faisal", "Rabia", "Omar", "Mahnoor", "Saad", "Iqra",
        "Asad", "Noor", "Kamran", "Sadia", "Tariq", "Mehwish", "Waqas", "Saba", "Junaid", "Kiran",
    ];

    public static readonly IReadOnlyList<string> Family =
    [
        "Khan", "Ahmed", "Malik", "Qureshi", "Butt", "Sheikh", "Chaudhry", "Raza", "Siddiqui", "Hussain",
        "Shah", "Awan", "Mirza", "Abbasi", "Baig", "Javed", "Iqbal", "Rana", "Syed", "Bhatti",
    ];

    public static string For(params string[] parts) =>
        $"{Deterministic.Pick(Given, [.. parts, "given"])} {Deterministic.Pick(Family, [.. parts, "family"])}";
}
