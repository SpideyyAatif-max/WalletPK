using System.Globalization;
using System.Text;

namespace WalletPK.MockProviders.Nadra;

/// <summary>
/// How alike two names are, from 0 to 1, the way a human checker would judge them: case, punctuation,
/// spacing and word order do not matter, and the usual spellings of Muhammad are the same name.
/// </summary>
public static class NameMatcher
{
    public const double Threshold = 0.85;

    private static readonly Dictionary<string, string> _variants = new(StringComparer.Ordinal)
    {
        ["MOHAMMAD"] = "MUHAMMAD",
        ["MOHAMMED"] = "MUHAMMAD",
        ["MUHAMMED"] = "MUHAMMAD",
        ["MOHAMAD"] = "MUHAMMAD",
        ["MUHAMAD"] = "MUHAMMAD",
        ["MOHD"] = "MUHAMMAD",
        ["MUHD"] = "MUHAMMAD",
        ["M"] = "MUHAMMAD",
    };

    public static string Normalize(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var letters = new StringBuilder(name.Length);
        foreach (var c in name.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            letters.Append(char.IsLetter(c) ? char.ToUpperInvariant(c) : ' ');
        }

        var words = letters.ToString()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(word => _variants.GetValueOrDefault(word, word))
            .Order(StringComparer.Ordinal);
        return string.Join(' ', words);
    }

    public static double Score(string a, string b)
    {
        var left = Normalize(a);
        var right = Normalize(b);
        if (left.Length == 0 || right.Length == 0)
        {
            return 0;
        }

        return 1.0 - ((double)Distance(left, right) / Math.Max(left.Length, right.Length));
    }

    public static bool Matches(string a, string b) => Score(a, b) >= Threshold;

    private static int Distance(string a, string b)
    {
        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++)
        {
            previous[j] = j;
        }

        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
            }

            (previous, current) = (current, previous);
        }

        return previous[b.Length];
    }
}
