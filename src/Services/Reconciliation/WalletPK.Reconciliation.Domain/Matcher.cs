using System.Globalization;

namespace WalletPK.Reconciliation.Domain;

/// <summary>Where a payment stands in WalletPK's own records.</summary>
public enum OurStatus
{
    /// <summary>Still in flight (sent, waiting for the provider, under review, queued).</summary>
    Pending,

    /// <summary>Done: the customer was charged and the provider owes the money onward.</summary>
    Completed,

    /// <summary>Failed or refunded: the customer has their money back.</summary>
    Failed,
}

/// <summary>A payment WalletPK sent to a provider, as the payment services' events describe it.</summary>
/// <param name="Id">WalletPK's ID (the transfer, payment or top-up ID; the provider echoes it as the client reference).</param>
/// <param name="Party">The settlement party, e.g. "bank:HBL".</param>
/// <param name="ProviderReference">The provider's reference, once it gave one.</param>
/// <param name="AmountPaisa">What WalletPK sent.</param>
/// <param name="Status">Where it stands here.</param>
/// <param name="CompletedAt">When it completed here, if it did.</param>
public sealed record OurPayment(Guid Id, string Party, string? ProviderReference, long AmountPaisa, OurStatus Status, DateTimeOffset? CompletedAt);

/// <summary>The kinds of break reconciliation finds (M12.2).</summary>
public enum BreakKind
{
    /// <summary>Completed here, not in the provider's file (nor the day before's or after's): the provider may not have paid out.</summary>
    MissingAtProvider,

    /// <summary>In the file, but WalletPK never sent it.</summary>
    UnknownToUs,

    /// <summary>In the file more than once: the provider would claim it twice.</summary>
    Duplicate,

    /// <summary>In the file with a different amount from what WalletPK sent.</summary>
    AmountMismatch,

    /// <summary>The provider settled it, but here it failed and the customer was refunded: money lost unless recovered.</summary>
    SettledButFailedHere,

    /// <summary>The provider settled it, but here it is still in flight: the customer is waiting for nothing.</summary>
    SettledButPendingHere,
}

/// <summary>One break.</summary>
/// <param name="Kind">What is wrong.</param>
/// <param name="Key">What identifies it within its party, day and kind: the provider's reference, or WalletPK's ID for a missing item.</param>
/// <param name="OurId">WalletPK's payment, when there is one.</param>
/// <param name="ProviderReference">The provider's reference, when known.</param>
/// <param name="ClientReference">The client reference the file gave.</param>
/// <param name="OurAmountPaisa">What WalletPK sent.</param>
/// <param name="ProviderAmountPaisa">What the file says.</param>
/// <param name="LinesInFile">How many times the file lists it.</param>
/// <param name="OurStatus">Where it stands here.</param>
/// <param name="Description">What is wrong, in words.</param>
public sealed record Break(
    BreakKind Kind,
    string Key,
    Guid? OurId,
    string? ProviderReference,
    string? ClientReference,
    long? OurAmountPaisa,
    long? ProviderAmountPaisa,
    int LinesInFile,
    OurStatus? OurStatus,
    string Description);

/// <summary>A file matched against WalletPK's records.</summary>
/// <param name="Lines">Detail lines in the file.</param>
/// <param name="Matched">Items that agree exactly.</param>
/// <param name="ProviderTotalPaisa">The file's total.</param>
/// <param name="OurCount">Payments that completed here that day.</param>
/// <param name="OurTotalPaisa">What they add up to.</param>
/// <param name="Breaks">Every break, ordered by kind and key.</param>
public sealed record MatchResult(int Lines, int Matched, long ProviderTotalPaisa, int OurCount, long OurTotalPaisa, IReadOnlyList<Break> Breaks);

/// <summary>
/// Matches one provider's settlement file for one day against WalletPK's records (M12.2). Pure: everything it
/// needs is passed in, so the same inputs always give the same breaks.
/// <list type="bullet">
/// <item>Each file item is found by the client reference (WalletPK's ID), or else the provider's reference.</item>
/// <item>An item found nowhere is <see cref="BreakKind.UnknownToUs"/>; one listed twice is a <see cref="BreakKind.Duplicate"/>.</item>
/// <item>A found item is checked against its status here, then its amount.</item>
/// <item>Payments that completed here that day and are not in the file are <see cref="BreakKind.MissingAtProvider"/>, unless
/// the day before's or after's file has them (settled either side of midnight).</item>
/// </list>
/// </summary>
public static class Matcher
{
    /// <param name="file">The provider's file.</param>
    /// <param name="referenced">WalletPK's payments the file's lines refer to (by client or provider reference); others may be included.</param>
    /// <param name="completedOnDay">WalletPK's payments with this party that completed on the file's day (Pakistan time).</param>
    /// <param name="settledOnOtherDays">Payments the neighbouring days' files list.</param>
    public static MatchResult Match(
        SettlementFile file,
        IReadOnlyCollection<OurPayment> referenced,
        IReadOnlyCollection<OurPayment> completedOnDay,
        IReadOnlySet<Guid> settledOnOtherDays)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(referenced);
        ArgumentNullException.ThrowIfNull(completedOnDay);
        ArgumentNullException.ThrowIfNull(settledOnOtherDays);

        var byId = new Dictionary<Guid, OurPayment>();
        var byReference = new Dictionary<string, OurPayment>(StringComparer.Ordinal);
        foreach (var payment in referenced.Concat(completedOnDay))
        {
            byId.TryAdd(payment.Id, payment);
            if (payment.ProviderReference is { Length: > 0 } reference)
            {
                byReference.TryAdd(reference, payment);
            }
        }

        var breaks = new List<Break>();
        var inFile = new HashSet<Guid>();
        var matched = 0;

        foreach (var group in file.Lines.GroupBy(line => line.ProviderReference, StringComparer.Ordinal))
        {
            var line = group.First();
            var count = group.Count();
            var payment = (Guid.TryParse(line.ClientReference, out var clientId) ? byId.GetValueOrDefault(clientId) : null)
                ?? byReference.GetValueOrDefault(line.ProviderReference);

            if (payment is null || !string.Equals(payment.Party, file.Party, StringComparison.Ordinal))
            {
                breaks.Add(new Break(BreakKind.UnknownToUs, line.ProviderReference, null, line.ProviderReference, line.ClientReference, null, line.AmountPaisa, count, null,
                    payment is null
                        ? $"{file.Party} settled {Rupees(line.AmountPaisa)} under reference {line.ProviderReference} (client reference {line.ClientReference}); WalletPK has no such payment."
                        : $"{file.Party} settled {Rupees(line.AmountPaisa)} under reference {line.ProviderReference}, but WalletPK sent that payment to {payment.Party}."));
                continue;
            }

            inFile.Add(payment.Id);
            var clean = true;
            if (count > 1)
            {
                clean = false;
                breaks.Add(new Break(BreakKind.Duplicate, line.ProviderReference, payment.Id, line.ProviderReference, line.ClientReference, payment.AmountPaisa, line.AmountPaisa, count, payment.Status,
                    $"Reference {line.ProviderReference} is in the file {count} times; WalletPK sent it once."));
            }

            if (payment.Status != OurStatus.Completed)
            {
                clean = false;
                breaks.Add(payment.Status == OurStatus.Failed
                    ? new Break(BreakKind.SettledButFailedHere, line.ProviderReference, payment.Id, line.ProviderReference, line.ClientReference, payment.AmountPaisa, line.AmountPaisa, count, payment.Status,
                        $"{file.Party} settled {Rupees(line.AmountPaisa)} (reference {line.ProviderReference}), but here the payment failed and the customer was refunded.")
                    : new Break(BreakKind.SettledButPendingHere, line.ProviderReference, payment.Id, line.ProviderReference, line.ClientReference, payment.AmountPaisa, line.AmountPaisa, count, payment.Status,
                        $"{file.Party} settled {Rupees(line.AmountPaisa)} (reference {line.ProviderReference}), but here the payment is still in flight."));
            }
            else if (line.AmountPaisa != payment.AmountPaisa)
            {
                clean = false;
                breaks.Add(new Break(BreakKind.AmountMismatch, line.ProviderReference, payment.Id, line.ProviderReference, line.ClientReference, payment.AmountPaisa, line.AmountPaisa, count, payment.Status,
                    $"The file says {Rupees(line.AmountPaisa)} for reference {line.ProviderReference}; WalletPK sent {Rupees(payment.AmountPaisa)} ({Rupees(line.AmountPaisa - payment.AmountPaisa, signed: true)})."));
            }

            if (clean)
            {
                matched++;
            }
        }

        foreach (var payment in completedOnDay.Where(payment => payment.Status == OurStatus.Completed && payment.Party == file.Party).DistinctBy(payment => payment.Id))
        {
            if (!inFile.Contains(payment.Id) && !settledOnOtherDays.Contains(payment.Id))
            {
                breaks.Add(new Break(BreakKind.MissingAtProvider, payment.Id.ToString(), payment.Id, payment.ProviderReference, null, payment.AmountPaisa, null, 0, payment.Status,
                    $"WalletPK completed {Rupees(payment.AmountPaisa)}{(payment.ProviderReference is null ? "" : $" (reference {payment.ProviderReference})")} with {file.Party}; it is not in the file."));
            }
        }

        var ours = completedOnDay.Where(payment => payment.Status == OurStatus.Completed && payment.Party == file.Party).DistinctBy(payment => payment.Id).ToList();
        return new MatchResult(
            file.Lines.Count,
            matched,
            file.TotalPaisa,
            ours.Count,
            ours.Sum(payment => payment.AmountPaisa),
            [.. breaks.OrderBy(item => item.Kind).ThenBy(item => item.Key, StringComparer.Ordinal)]);
    }

    /// <summary>Paisa as rupees, e.g. "Rs. 1,250.00".</summary>
    public static string Rupees(long paisa, bool signed = false)
    {
        var sign = paisa < 0 ? "-" : signed ? "+" : "";
        return string.Create(CultureInfo.InvariantCulture, $"{sign}Rs. {Math.Abs(paisa) / 100m:N2}");
    }
}
