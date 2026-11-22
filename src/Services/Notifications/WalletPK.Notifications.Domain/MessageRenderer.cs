using System.Globalization;
using System.Text;

namespace WalletPK.Notifications.Domain;

/// <summary>
/// Fills a template in the reader's language. Notifications store their parameters, not their text, so the
/// notification centre always shows them in the language the user has chosen now. A parameter value can be:
/// <list type="bullet">
/// <item>plain text, used as it is (amounts are already "Rs. 1,250.00", which reads the same in Urdu);</item>
/// <item>"#phrase", a translated phrase from <see cref="Phrases"/>, which can hold placeholders itself;</item>
/// <item>"@date:2026-11-30", "@datetime:2026-11-30T17:00:00Z" or "@month:2026-11", written out in the
/// language (times in Pakistan time).</item>
/// </list>
/// A placeholder without a parameter is left as it is, so a missing value shows rather than breaking the message.
/// </summary>
public static class MessageRenderer
{
    public const string PhrasePrefix = "#";
    public const string DatePrefix = "@date:";
    public const string DateTimePrefix = "@datetime:";
    public const string MonthPrefix = "@month:";

    private static readonly TimeSpan _pakistan = TimeSpan.FromHours(5);

    private static readonly string[] _urduMonths =
        ["جنوری", "فروری", "مارچ", "اپریل", "مئی", "جون", "جولائی", "اگست", "ستمبر", "اکتوبر", "نومبر", "دسمبر"];

    public static Wording Render(Template template, string language, IReadOnlyDictionary<string, string> parameters)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(parameters);
        var wording = template.In(language);
        return new(Fill(wording.Title, language, parameters, 0), Fill(wording.Body, language, parameters, 0));
    }

    /// <summary>The placeholders in a piece of text, for checks that every template gets its parameters.</summary>
    public static IEnumerable<string> PlaceholdersIn(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        for (var start = text.IndexOf('{', StringComparison.Ordinal); start >= 0; start = text.IndexOf('{', start + 1))
        {
            var end = text.IndexOf('}', start);
            if (end > start + 1)
            {
                yield return text[(start + 1)..end];
            }
        }
    }

    private static string Fill(string text, string language, IReadOnlyDictionary<string, string> parameters, int depth)
    {
        var result = new StringBuilder(text.Length + 32);
        var position = 0;
        while (position < text.Length)
        {
            var start = text.IndexOf('{', position);
            var end = start < 0 ? -1 : text.IndexOf('}', start);
            if (start < 0 || end < 0)
            {
                result.Append(text, position, text.Length - position);
                break;
            }

            result.Append(text, position, start - position);
            var name = text[(start + 1)..end];
            result.Append(parameters.TryGetValue(name, out var value) ? Value(value, language, parameters, depth) : text[start..(end + 1)]);
            position = end + 1;
        }

        return result.ToString();
    }

    private static string Value(string value, string language, IReadOnlyDictionary<string, string> parameters, int depth)
    {
        if (value.StartsWith(PhrasePrefix, StringComparison.Ordinal) && Phrases.All.TryGetValue(value[PhrasePrefix.Length..], out var phrase))
        {
            var text = language == Languages.Urdu ? phrase.Urdu : phrase.English;
            return depth < 3 ? Fill(text, language, parameters, depth + 1) : text;
        }

        if (value.StartsWith(DatePrefix, StringComparison.Ordinal)
            && DateOnly.TryParseExact(value[DatePrefix.Length..], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            return language == Languages.Urdu
                ? $"{date.Day} {_urduMonths[date.Month - 1]} {date.Year}"
                : date.ToString("d MMM yyyy", CultureInfo.InvariantCulture);
        }

        if (value.StartsWith(DateTimePrefix, StringComparison.Ordinal)
            && DateTimeOffset.TryParse(value[DateTimePrefix.Length..], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at))
        {
            var local = at.ToOffset(_pakistan);
            return language == Languages.Urdu
                ? $"{local.Day} {_urduMonths[local.Month - 1]} {local.Year}، {local.ToString("HH:mm", CultureInfo.InvariantCulture)}"
                : local.ToString("d MMM yyyy, HH:mm", CultureInfo.InvariantCulture);
        }

        if (value.StartsWith(MonthPrefix, StringComparison.Ordinal)
            && DateOnly.TryParseExact($"{value[MonthPrefix.Length..]}-01", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var month))
        {
            return language == Languages.Urdu
                ? $"{_urduMonths[month.Month - 1]} {month.Year}"
                : month.ToString("MMMM yyyy", CultureInfo.InvariantCulture);
        }

        return value;
    }

    /// <summary>"Rs. 1,250.50" from paisa, the way amounts appear in messages (in both languages).</summary>
    public static string Rupees(long paisa) =>
        $"Rs. {(paisa / 100m).ToString("N2", CultureInfo.InvariantCulture)}";

    /// <summary>A date parameter: shown as "30 Nov 2026" or "30 نومبر 2026".</summary>
    public static string Date(DateOnly date) => DatePrefix + date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>A date-and-time parameter, shown in Pakistan time.</summary>
    public static string DateTime(DateTimeOffset at) => DateTimePrefix + at.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

    /// <summary>A month parameter from "2026-11".</summary>
    public static string Month(string yearMonth) => $"{MonthPrefix}{yearMonth}";

    /// <summary>A translated phrase parameter.</summary>
    public static string Phrase(string key) => $"{PhrasePrefix}{key}";
}
