using System.Globalization;
using System.Text;
using WalletPK.MockProviders.Common;

namespace WalletPK.MockProviders.Settlement;

/// <summary>One settled item as a provider reports it.</summary>
/// <param name="ProviderReference">The provider's reference (Raast reference, biller reference ...).</param>
/// <param name="ClientReference">WalletPK's own ID for it (transferId, paymentId, topUpId).</param>
/// <param name="AmountPaisa">The amount settled.</param>
/// <param name="CompletedAt">When it was settled.</param>
public sealed record SettlementLine(string ProviderReference, string ClientReference, long AmountPaisa, DateTimeOffset CompletedAt);

/// <summary>How many mismatches of each kind to plant in a file (scenario S8).</summary>
/// <param name="Missing">Settled items left out of the file.</param>
/// <param name="Duplicate">Items listed twice.</param>
/// <param name="AmountMismatch">Items with a wrong amount.</param>
/// <param name="Unknown">Items WalletPK never sent (made-up references).</param>
public sealed record MismatchPlan(int Missing = 0, int Duplicate = 0, int AmountMismatch = 0, int Unknown = 0)
{
    public bool IsValid() => Missing >= 0 && Duplicate >= 0 && AmountMismatch >= 0 && Unknown >= 0 && Missing + Duplicate + AmountMismatch + Unknown <= 10_000;

    public bool IsEmpty => Missing + Duplicate + AmountMismatch + Unknown == 0;
}

/// <param name="ProviderReference">The item.</param>
/// <param name="ActualPaisa">What was really settled.</param>
/// <param name="ReportedPaisa">What the file says.</param>
public sealed record AmountChange(string ProviderReference, long ActualPaisa, long ReportedPaisa);

/// <summary>
/// The answer key: exactly which mismatches the file contains, so a test can check that reconciliation
/// (M12) found and classified every one. Fewer than planned when the day had too few items.
/// </summary>
public sealed record InjectedMismatches(
    IReadOnlyList<string> Missing,
    IReadOnlyList<string> Duplicate,
    IReadOnlyList<AmountChange> AmountMismatch,
    IReadOnlyList<SettlementLine> Unknown);

/// <summary>
/// End-of-day settlement files (M5.6), CSV with one record per line:
/// <code>
/// H,bank:HBL,2026-11-15                                   header: party, business date (Pakistan time)
/// D,RAAST2026111512AB...,7d0e...,150000,2026-11-15T09:12:44.0000000+00:00   one per settled item
/// T,42,6301500                                            trailer: item count, total paisa
/// </code>
/// The same data and plan always give the same file, byte for byte.
/// </summary>
public static class SettlementFile
{
    /// <summary>Plants the plan's mismatches into the real lines.</summary>
    public static (IReadOnlyList<SettlementLine> Lines, InjectedMismatches Injected) Apply(
        IReadOnlyList<SettlementLine> actual, MismatchPlan plan, string party, DateOnly businessDate)
    {
        ArgumentNullException.ThrowIfNull(actual);
        ArgumentNullException.ThrowIfNull(plan);

        var random = Deterministic.Random("settlement", party, businessDate.ToString("O", CultureInfo.InvariantCulture));
        var order = actual.OrderBy(line => line.ProviderReference, StringComparer.Ordinal).ToList();
        var shuffled = order.ToArray();
#pragma warning disable CA5394 // Seeded on purpose: the same plan gives the same file.
        random.Shuffle(shuffled);

        var missing = shuffled.Take(plan.Missing).ToList();
        var duplicate = shuffled.Skip(missing.Count).Take(plan.Duplicate).ToList();
        var changed = shuffled.Skip(missing.Count + duplicate.Count).Take(plan.AmountMismatch).ToList();

        var amountChanges = changed.Select(line =>
        {
            var delta = random.Next(1, 50_001) * (random.Next(2) == 0 ? -1 : 1);
            var reported = line.AmountPaisa + delta <= 0 ? line.AmountPaisa + Math.Abs(delta) : line.AmountPaisa + delta;
            return new AmountChange(line.ProviderReference, line.AmountPaisa, reported);
        }).ToList();

        var dayStart = PakistanTime.StartOf(businessDate);
        var unknown = Enumerable.Range(0, plan.Unknown).Select(_ => new SettlementLine(
            $"UNK{businessDate:yyyyMMdd}{random.NextInt64(0, 1_000_000_000_000):D12}",
            NewGuid(random).ToString(),
            random.NextInt64(100, 5_000_001),
            dayStart.AddSeconds(random.Next(0, 86_400)))).ToList();
#pragma warning restore CA5394

        var dropped = missing.Select(line => line.ProviderReference).ToHashSet(StringComparer.Ordinal);
        var twice = duplicate.Select(line => line.ProviderReference).ToHashSet(StringComparer.Ordinal);
        var amounts = amountChanges.ToDictionary(change => change.ProviderReference, change => change.ReportedPaisa, StringComparer.Ordinal);

        var lines = new List<SettlementLine>();
        foreach (var line in order.Concat(unknown).OrderBy(line => line.ProviderReference, StringComparer.Ordinal))
        {
            if (dropped.Contains(line.ProviderReference))
            {
                continue;
            }

            var reported = amounts.TryGetValue(line.ProviderReference, out var amount) ? line with { AmountPaisa = amount } : line;
            lines.Add(reported);
            if (twice.Contains(line.ProviderReference))
            {
                lines.Add(reported);
            }
        }

        return (lines, new InjectedMismatches(
            [.. dropped.Order(StringComparer.Ordinal)],
            [.. twice.Order(StringComparer.Ordinal)],
            [.. amountChanges.OrderBy(change => change.ProviderReference, StringComparer.Ordinal)],
            [.. unknown.OrderBy(line => line.ProviderReference, StringComparer.Ordinal)]));
    }

    private static Guid NewGuid(Random random)
    {
        var bytes = new byte[16];
#pragma warning disable CA5394 // Seeded on purpose.
        random.NextBytes(bytes);
#pragma warning restore CA5394
        return new Guid(bytes);
    }

    public static string ToCsv(string party, DateOnly businessDate, IReadOnlyList<SettlementLine> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var csv = new StringBuilder();
        csv.Append(CultureInfo.InvariantCulture, $"H,{party},{businessDate:yyyy-MM-dd}\n");
        foreach (var line in lines)
        {
            csv.Append(CultureInfo.InvariantCulture, $"D,{line.ProviderReference},{line.ClientReference},{line.AmountPaisa},{line.CompletedAt.ToUniversalTime():O}\n");
        }

        csv.Append(CultureInfo.InvariantCulture, $"T,{lines.Count},{lines.Sum(line => line.AmountPaisa)}\n");
        return csv.ToString();
    }
}
