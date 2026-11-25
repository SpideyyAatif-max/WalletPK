using WalletPK.Reconciliation.Domain;

namespace WalletPK.Reconciliation.Application;

/// <summary>
/// A payment WalletPK sent to an outside party (a bank transfer, bill payment or top-up), kept up to date
/// from the payment services' events (M12.1).
/// </summary>
public sealed class ExternalPayment
{
    /// <summary>WalletPK's ID; the provider echoes it as the client reference.</summary>
    public Guid Id { get; set; }

    /// <summary>bank_transfer, bill_payment or topup.</summary>
    public string Kind { get; set; } = "";

    /// <summary>Settlement party, e.g. "bank:HBL".</summary>
    public string Party { get; set; } = "";

    public Guid UserId { get; set; }

    public string? ProviderReference { get; set; }

    public long AmountPaisa { get; set; }

    /// <summary>Pending, Completed or Failed (<see cref="OurStatus"/>).</summary>
    public string Status { get; set; } = "";

    /// <summary>The service's own status, e.g. "UnderReview".</summary>
    public string ServiceStatus { get; set; } = "";

    public DateTimeOffset? CompletedAt { get; set; }

    /// <summary>The Pakistan business date it completed on.</summary>
    public DateOnly? CompletedOn { get; set; }

    /// <summary>When the newest event about it happened.</summary>
    public DateTimeOffset UpdatedAt { get; set; }

    public DateTimeOffset FirstSeenAt { get; set; }

    public OurPayment ToOurs() =>
        new(Id, Party, ProviderReference, AmountPaisa, Enum.Parse<OurStatus>(Status), CompletedAt);
}

/// <summary>A settlement file as it was fetched, kept as evidence.</summary>
public sealed class SettlementFileRecord
{
    public Guid Id { get; set; }

    public string Party { get; set; } = "";

    public DateOnly BusinessDate { get; set; }

    public DateTimeOffset FetchedAt { get; set; }

    public int LineCount { get; set; }

    public long TotalPaisa { get; set; }

    /// <summary>SHA-256 of the content, hex.</summary>
    public string Sha256 { get; set; } = "";

    public string Content { get; set; } = "";
}

/// <summary>A line of the latest file for its party and day.</summary>
public sealed class SettlementLineRecord
{
    public long Id { get; set; }

    public Guid FileId { get; set; }

    public string Party { get; set; } = "";

    public DateOnly BusinessDate { get; set; }

    public string ProviderReference { get; set; } = "";

    public string ClientReference { get; set; } = "";

    /// <summary>The client reference as a WalletPK ID, when it is one.</summary>
    public Guid? ClientId { get; set; }

    public long AmountPaisa { get; set; }

    public DateTimeOffset CompletedAt { get; set; }
}

public static class RunStatuses
{
    public const string Completed = "Completed";

    /// <summary>The file could not be fetched, or was rejected.</summary>
    public const string Failed = "Failed";
}

/// <summary>One party's file for one day, matched (or why it could not be).</summary>
public sealed class ReconRun
{
    public Guid Id { get; set; }

    public string Party { get; set; } = "";

    public DateOnly BusinessDate { get; set; }

    public string Status { get; set; } = "";

    public string? Problem { get; set; }

    public Guid? FileId { get; set; }

    public int Lines { get; set; }

    public int Matched { get; set; }

    public long ProviderTotalPaisa { get; set; }

    public int OurCount { get; set; }

    public long OurTotalPaisa { get; set; }

    public int MissingAtProvider { get; set; }

    public int UnknownToUs { get; set; }

    public int Duplicate { get; set; }

    public int AmountMismatch { get; set; }

    public int SettledButFailedHere { get; set; }

    public int SettledButPendingHere { get; set; }

    public int Breaks => MissingAtProvider + UnknownToUs + Duplicate + AmountMismatch + SettledButFailedHere + SettledButPendingHere;

    /// <summary>"system:scheduler" or "user:{id}".</summary>
    public string TriggeredBy { get; set; } = "";

    public DateTimeOffset StartedAt { get; set; }

    public DateTimeOffset FinishedAt { get; set; }
}

public static class BreakStatuses
{
    public const string Open = "Open";

    /// <summary>Raised with the provider; waiting for their answer.</summary>
    public const string Disputed = "Disputed";

    /// <summary>Closed by Finance, with a resolution and a note.</summary>
    public const string Resolved = "Resolved";

    /// <summary>Gone on a later run (the provider corrected its file, or the item arrived late).</summary>
    public const string Cleared = "Cleared";

    public static IReadOnlyList<string> All { get; } = [Open, Disputed, Resolved, Cleared];

    public static bool IsOpen(string status) => status is Open or Disputed;
}

/// <summary>How Finance closed a break (M12.3).</summary>
public static class Resolutions
{
    /// <summary>A correcting ledger entry was posted.</summary>
    public const string Adjusted = "Adjusted";

    /// <summary>The provider paid, refunded or corrected it.</summary>
    public const string Recovered = "Recovered";

    /// <summary>Written off as a loss.</summary>
    public const string WrittenOff = "WrittenOff";

    /// <summary>Nothing to do (e.g. an explained timing difference).</summary>
    public const string NoActionNeeded = "NoActionNeeded";

    public static IReadOnlyList<string> All { get; } = [Adjusted, Recovered, WrittenOff, NoActionNeeded];
}

/// <summary>A break, from the run that first found it until Finance closes it.</summary>
public sealed class ReconBreak
{
    public Guid Id { get; set; }

    public string Party { get; set; } = "";

    public DateOnly BusinessDate { get; set; }

    /// <summary>A <see cref="BreakKind"/>.</summary>
    public string Kind { get; set; } = "";

    /// <summary>Unique within party, day and kind (see <see cref="Break.Key"/>).</summary>
    public string Key { get; set; } = "";

    public Guid? OurId { get; set; }

    public string? ProviderReference { get; set; }

    public string? ClientReference { get; set; }

    public long? OurAmountPaisa { get; set; }

    public long? ProviderAmountPaisa { get; set; }

    public int LinesInFile { get; set; }

    public string? OurStatus { get; set; }

    public string Description { get; set; } = "";

    public string Status { get; set; } = BreakStatuses.Open;

    public string? Resolution { get; set; }

    /// <summary>The last note Finance wrote.</summary>
    public string? Note { get; set; }

    public string? UpdatedBy { get; set; }

    public Guid FirstRunId { get; set; }

    public Guid LastRunId { get; set; }

    public DateTimeOffset FoundAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public DateTimeOffset? ClosedAt { get; set; }

    /// <summary>How much money the break is about, in paisa (never negative).</summary>
    public long AtStakePaisa => Enum.Parse<BreakKind>(Kind) switch
    {
        BreakKind.MissingAtProvider => OurAmountPaisa ?? 0,
        BreakKind.Duplicate => (ProviderAmountPaisa ?? 0) * Math.Max(LinesInFile - 1, 0),
        BreakKind.AmountMismatch => Math.Abs((OurAmountPaisa ?? 0) - (ProviderAmountPaisa ?? 0)),
        _ => ProviderAmountPaisa ?? 0,
    };
}

/// <summary>One entry of the hash-chained audit log (FR-5.3).</summary>
public sealed class AuditEntry
{
    public long Sequence { get; set; }

    public string Actor { get; set; } = "";

    public string Action { get; set; } = "";

    public string Subject { get; set; } = "";

    public string Details { get; set; } = "";

    public DateTimeOffset At { get; set; }

    public string PreviousHash { get; set; } = "";

    public string Hash { get; set; } = "";
}

/// <summary>Which breaks to list.</summary>
/// <param name="Status">Open, Disputed, Resolved or Cleared; "open" for Open and Disputed.</param>
/// <param name="Kind">A <see cref="BreakKind"/>.</param>
/// <param name="Party">e.g. "bank:HBL".</param>
/// <param name="From">First business date.</param>
/// <param name="To">Last business date.</param>
/// <param name="Limit">At most this many.</param>
public sealed record BreakQuery(string? Status, string? Kind, string? Party, DateOnly? From, DateOnly? To, int Limit);

/// <summary>Reconciliation settings, from "Reconciliation".</summary>
public sealed class ReconciliationOptions
{
    public const string SectionName = "Reconciliation";

    /// <summary>The parties reconciled every day (default: every party the providers settle with).</summary>
    public List<string> Parties { get; set; } = [];

    /// <summary>Read the payment topics from the start the first time (the default), so earlier payments are known.</summary>
    public bool StartFromLatest { get; set; }

    public ScheduleOptions Schedule { get; set; } = new();

    public IReadOnlyList<string> PartiesOrDefault => Parties is { Count: > 0 } ? Parties : Domain.Parties.Default;

    public bool IsValid() =>
        PartiesOrDefault.All(Domain.Parties.IsValid)
        && Schedule.PollSeconds is >= 1 and <= 3_600
        && Schedule.RetryMinutes is >= 1 and <= 1_440
        && TimeOnly.TryParse(Schedule.DailyAt, System.Globalization.CultureInfo.InvariantCulture, out _);
}

/// <summary>The nightly run (M12.1): yesterday's files, a little after midnight Pakistan time.</summary>
public sealed class ScheduleOptions
{
    public bool Enabled { get; set; } = true;

    /// <summary>Pakistan time of day, "HH:mm", from which yesterday is reconciled.</summary>
    public string DailyAt { get; set; } = "00:30";

    public int PollSeconds { get; set; } = 60;

    /// <summary>How long to wait before trying a party whose file could not be fetched again.</summary>
    public int RetryMinutes { get; set; } = 15;
}
