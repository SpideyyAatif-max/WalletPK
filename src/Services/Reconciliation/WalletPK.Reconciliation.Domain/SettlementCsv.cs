using System.Globalization;

namespace WalletPK.Reconciliation.Domain;

/// <summary>One settled item as the provider reports it.</summary>
/// <param name="ProviderReference">The provider's reference (Raast, biller or network reference).</param>
/// <param name="ClientReference">What the provider says is WalletPK's ID for it (transfer, payment or top-up ID).</param>
/// <param name="AmountPaisa">The amount settled.</param>
/// <param name="CompletedAt">When the provider settled it.</param>
public sealed record SettlementLine(string ProviderReference, string ClientReference, long AmountPaisa, DateTimeOffset CompletedAt);

/// <summary>A settlement file, read and checked.</summary>
/// <param name="Party">Who it is from, e.g. "bank:HBL".</param>
/// <param name="BusinessDate">The day it covers (Pakistan time).</param>
/// <param name="Lines">Its detail lines, in file order.</param>
public sealed record SettlementFile(string Party, DateOnly BusinessDate, IReadOnlyList<SettlementLine> Lines)
{
    public long TotalPaisa => Lines.Sum(line => line.AmountPaisa);
}

/// <summary>
/// Reads the end-of-day settlement files every provider sends (M12.1), CSV with one record per line:
/// <code>
/// H,{party},{yyyy-MM-dd}
/// D,{providerReference},{clientReference},{amountPaisa},{completedAt ISO 8601}
/// T,{count},{totalPaisa}
/// </code>
/// A file whose header names another party or day, whose trailer disagrees with its lines, or with any line
/// that does not parse is rejected whole: half a file would show the missing half as breaks.
/// </summary>
public static class SettlementCsv
{
    /// <summary>The file, or why it was rejected.</summary>
    public static (SettlementFile? File, string? Problem) Parse(string csv, string expectedParty, DateOnly expectedDate)
    {
        ArgumentNullException.ThrowIfNull(csv);
        var rows = csv.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (rows.Length < 2)
        {
            return (null, "The file has no header and trailer.");
        }

        var header = rows[0].Split(',');
        if (header is not ["H", var party, var dateText])
        {
            return (null, "The first record is not a header (H,party,date).");
        }

        if (!string.Equals(party, expectedParty, StringComparison.Ordinal))
        {
            return (null, $"The header names {party}, not {expectedParty}.");
        }

        if (!DateOnly.TryParseExact(dateText, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) || date != expectedDate)
        {
            return (null, $"The header's date is {dateText}, not {expectedDate:yyyy-MM-dd}.");
        }

        var trailer = rows[^1].Split(',');
        if (trailer is not ["T", var countText, var totalText]
            || !int.TryParse(countText, NumberStyles.None, CultureInfo.InvariantCulture, out var declaredCount)
            || !long.TryParse(totalText, NumberStyles.None, CultureInfo.InvariantCulture, out var declaredTotal))
        {
            return (null, "The last record is not a trailer (T,count,total).");
        }

        var lines = new List<SettlementLine>(rows.Length - 2);
        for (var index = 1; index < rows.Length - 1; index++)
        {
            var fields = rows[index].Split(',');
            if (fields is not ["D", { Length: > 0 and <= 64 } reference, { Length: > 0 and <= 64 } client, var amountText, var completedText]
                || !long.TryParse(amountText, NumberStyles.None, CultureInfo.InvariantCulture, out var amount)
                || amount <= 0
                || !DateTimeOffset.TryParse(completedText, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var completedAt))
            {
                return (null, $"Record {index + 1} is not a valid detail line (D,reference,client reference,amount,completed at).");
            }

            lines.Add(new SettlementLine(reference, client, amount, completedAt.ToUniversalTime()));
        }

        if (declaredCount != lines.Count)
        {
            return (null, $"The trailer counts {declaredCount} lines; the file has {lines.Count}.");
        }

        var file = new SettlementFile(party, date, lines);
        return file.TotalPaisa != declaredTotal
            ? (null, $"The trailer totals {declaredTotal} paisa; the lines add up to {file.TotalPaisa}.")
            : (file, null);
    }
}
