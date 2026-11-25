using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WalletPK.Common.Results;
using WalletPK.Common.Time;
using WalletPK.Reconciliation.Domain;

namespace WalletPK.Reconciliation.Application;

/// <summary>What a payment service's event says about one of its external payments.</summary>
/// <param name="Id">WalletPK's ID for it.</param>
/// <param name="Kind">bank_transfer, bill_payment or topup.</param>
/// <param name="Party">The settlement party.</param>
/// <param name="UserId">The customer.</param>
/// <param name="ProviderReference">The provider's reference, if it gave one yet.</param>
/// <param name="AmountPaisa">How much.</param>
/// <param name="ServiceStatus">The service's status, e.g. "Completed" or "UnderReview".</param>
/// <param name="ChangedAt">When.</param>
public sealed record PaymentUpdate(Guid Id, string Kind, string Party, Guid UserId, string? ProviderReference, long AmountPaisa, string ServiceStatus, DateTimeOffset ChangedAt);

/// <summary>Keeps <see cref="ExternalPayment"/> up to date from the payment services' events (M12.1).</summary>
public sealed class PaymentTracker(IReconStore store, TimeProvider timeProvider)
{
    public const string BankTransfer = "bank_transfer";
    public const string BillPayment = "bill_payment";
    public const string TopUp = "topup";

    public async Task TrackAsync(PaymentUpdate update, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(update);
        update = update with { ChangedAt = update.ChangedAt.ToUniversalTime() };
        var status = StatusOf(update.ServiceStatus);
        var payment = await store.PaymentAsync(update.Id, cancellationToken);
        if (payment is null)
        {
            payment = new ExternalPayment
            {
                Id = update.Id,
                Kind = update.Kind,
                Party = update.Party,
                UserId = update.UserId,
                FirstSeenAt = timeProvider.GetUtcNow(),
                UpdatedAt = DateTimeOffset.MinValue,
            };
            store.Add(payment);
        }

        // Each service publishes a payment's events in order, but a redelivered old one must not undo a newer one.
        if (update.ChangedAt < payment.UpdatedAt)
        {
            payment.ProviderReference ??= update.ProviderReference;
            return;
        }

        payment.Party = update.Party;
        payment.AmountPaisa = update.AmountPaisa;
        payment.ProviderReference = update.ProviderReference ?? payment.ProviderReference;
        payment.ServiceStatus = update.ServiceStatus;
        payment.Status = status.ToString();
        payment.UpdatedAt = update.ChangedAt;
        if (status == OurStatus.Completed)
        {
            payment.CompletedAt ??= update.ChangedAt;
            payment.CompletedOn ??= update.ChangedAt.ToBusinessDate();
        }
        else
        {
            payment.CompletedAt = null;
            payment.CompletedOn = null;
        }
    }

    public static OurStatus StatusOf(string serviceStatus) => serviceStatus switch
    {
        "Completed" => OurStatus.Completed,
        "Failed" => OurStatus.Failed,
        _ => OurStatus.Pending,
    };
}

/// <summary>A run of one day, and its breaks.</summary>
public sealed record RunDetail(ReconRun Run, IReadOnlyList<ReconBreak> Breaks);

/// <summary>A break, the payment it is about, and what has been done about it.</summary>
public sealed record BreakDetail(ReconBreak Break, ExternalPayment? Payment, IReadOnlyList<AuditEntry> History);

/// <summary>One party on one day: its latest run and its breaks still open.</summary>
public sealed record PartyDay(string Party, ReconRun? Run, int OpenBreaks);

/// <summary>A day across every party.</summary>
public sealed record DaySummary(DateOnly Date, IReadOnlyList<PartyDay> Parties, int Lines, int Matched, int Breaks, int OpenBreaks, long ProviderTotalPaisa, long OurTotalPaisa);

/// <summary>One line of the day list.</summary>
public sealed record DayOverview(DateOnly Date, int Reconciled, int Failed, int Lines, int Matched, int Breaks, long ProviderTotalPaisa, long OurTotalPaisa);

public static class ReconErrors
{
    public static readonly Error RunNotFound = Error.NotFound("recon.run_not_found", "No such reconciliation run.");
    public static readonly Error PaymentNotFound = Error.NotFound("recon.payment_not_found", "Reconciliation has not seen this payment.");
    public static readonly Error BreakNotFound = Error.NotFound("recon.break_not_found", "No such break.");
    public static readonly Error FutureDate = Error.Validation("recon.future_date", "Only days that have ended (Pakistan time) can be reconciled.");
    public static readonly Error NoteRequired = Error.Validation("recon.note_required", "Write a note of 5 to 1,000 characters: it goes on the audit log.");
    public static readonly Error UnknownResolution = Error.Validation("recon.unknown_resolution", $"The resolution is one of {string.Join(", ", Resolutions.All)}.");
    public static readonly Error AlreadyClosed = Error.Conflict("recon.break_closed", "This break is already closed.");
    public static readonly Error NotClosed = Error.Conflict("recon.break_not_closed", "Only resolved or cleared breaks can be reopened.");
    public static readonly Error NotOpen = Error.Conflict("recon.break_not_open", "Only an open break can be disputed.");

    public static Error UnknownParty(string party) => Error.Validation("recon.unknown_party", $"'{party}' is not a party WalletPK reconciles with.");
}

/// <summary>Daily runs: fetch each party's file, match it and record the breaks (M12.1, M12.2).</summary>
public sealed class ReconciliationService(
    IReconStore store,
    ISettlementSource source,
    ReconciliationOptions options,
    IReconMetrics metrics,
    TimeProvider timeProvider)
{
    public const string Scheduler = "system:scheduler";

    public IReadOnlyList<string> Parties => options.PartiesOrDefault;

    /// <summary>Reconciles <paramref name="date"/> for each party (every party when none are given), one at a time.</summary>
    public async Task<Result<IReadOnlyList<ReconRun>>> RunAsync(DateOnly date, IReadOnlyList<string>? parties, string actor, CancellationToken cancellationToken)
    {
        if (date > timeProvider.GetBusinessDate())
        {
            return ReconErrors.FutureDate;
        }

        var which = parties is { Count: > 0 } ? parties.Distinct(StringComparer.Ordinal).ToList() : [.. Parties];
        if (which.FirstOrDefault(party => !Parties.Contains(party)) is { } unknown)
        {
            return ReconErrors.UnknownParty(unknown);
        }

        var runs = new List<ReconRun>(which.Count);
        foreach (var party in which)
        {
            runs.Add(await RunPartyAsync(party, date, actor, cancellationToken));
        }

        metrics.OpenBreaks(await store.OpenBreakCountsAsync(cancellationToken));
        return runs;
    }

    private async Task<ReconRun> RunPartyAsync(string party, DateOnly date, string actor, CancellationToken cancellationToken)
    {
        var startedAt = timeProvider.GetUtcNow();

        // Outside the transaction: a slow provider must not hold the reconciliation lock.
        var fetch = await source.FetchAsync(party, date, cancellationToken);

        var run = await store.InTransactionAsync(async () =>
        {
            var now = timeProvider.GetUtcNow();
            var run = new ReconRun
            {
                Id = Guid.CreateVersion7(now),
                Party = party,
                BusinessDate = date,
                TriggeredBy = actor,
                StartedAt = startedAt,
            };
            store.Add(run);

            if (fetch.Content is not { } content)
            {
                return Fail(run, fetch.Problem ?? "The file could not be fetched.", now);
            }

            var (file, problem) = SettlementCsv.Parse(content, party, date);
            var record = new SettlementFileRecord
            {
                Id = Guid.CreateVersion7(now),
                Party = party,
                BusinessDate = date,
                FetchedAt = now,
                LineCount = file?.Lines.Count ?? 0,
                TotalPaisa = file?.TotalPaisa ?? 0,
                Sha256 = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content))),
                Content = content,
            };
            await store.SaveFileAsync(record, file?.Lines, cancellationToken);
            run.FileId = record.Id;
            if (file is null)
            {
                return Fail(run, $"The file was rejected: {problem}", now);
            }

            var result = await MatchAsync(file, cancellationToken);
            await RecordBreaksAsync(run, result, now, cancellationToken);

            run.Status = RunStatuses.Completed;
            run.Lines = result.Lines;
            run.Matched = result.Matched;
            run.ProviderTotalPaisa = result.ProviderTotalPaisa;
            run.OurCount = result.OurCount;
            run.OurTotalPaisa = result.OurTotalPaisa;
            run.MissingAtProvider = Count(result, BreakKind.MissingAtProvider);
            run.UnknownToUs = Count(result, BreakKind.UnknownToUs);
            run.Duplicate = Count(result, BreakKind.Duplicate);
            run.AmountMismatch = Count(result, BreakKind.AmountMismatch);
            run.SettledButFailedHere = Count(result, BreakKind.SettledButFailedHere);
            run.SettledButPendingHere = Count(result, BreakKind.SettledButPendingHere);
            run.FinishedAt = now;
            store.Audit(actor, "run.completed", $"run:{run.Id}", Json(new
            {
                party,
                date,
                fileSha256 = record.Sha256,
                lines = run.Lines,
                matched = run.Matched,
                breaks = run.Breaks,
                providerTotalPaisa = run.ProviderTotalPaisa,
                ourTotalPaisa = run.OurTotalPaisa,
            }), now);
            return run;
        }, cancellationToken);

        metrics.RunFinished(party, run.Status);
        return run;
    }

    private ReconRun Fail(ReconRun run, string problem, DateTimeOffset now)
    {
        run.Status = RunStatuses.Failed;
        run.Problem = problem.Length > 500 ? problem[..500] : problem;
        run.FinishedAt = now;
        store.Audit(run.TriggeredBy, "run.failed", $"run:{run.Id}", Json(new { party = run.Party, date = run.BusinessDate, problem = run.Problem }), now);
        return run;
    }

    private async Task<MatchResult> MatchAsync(SettlementFile file, CancellationToken cancellationToken)
    {
        var ids = file.Lines.Select(line => Guid.TryParse(line.ClientReference, out var id) ? id : (Guid?)null).OfType<Guid>().Distinct().ToList();
        var references = file.Lines.Select(line => line.ProviderReference).Distinct(StringComparer.Ordinal).ToList();
        var referenced = await store.PaymentsAsync(ids, references, cancellationToken);
        var completed = await store.CompletedOnAsync(file.Party, file.BusinessDate, cancellationToken);

        // Payments that completed here that day but are not in this file may be in the neighbouring days' files.
        var listed = ids.ToHashSet();
        var notListed = completed.Select(payment => payment.Id).Where(id => !listed.Contains(id)).ToList();
        var elsewhere = notListed.Count == 0
            ? new HashSet<Guid>()
            : await store.ListedOnAsync(file.Party, [file.BusinessDate.AddDays(-1), file.BusinessDate.AddDays(1)], notListed, cancellationToken);

        return Matcher.Match(file, [.. referenced.Select(payment => payment.ToOurs())], [.. completed.Select(payment => payment.ToOurs())], elsewhere);
    }

    /// <summary>
    /// A break found again keeps its history (and Finance's work on it); a new one opens; an open one this run
    /// no longer finds is cleared. Resolved breaks stay resolved.
    /// </summary>
    private async Task RecordBreaksAsync(ReconRun run, MatchResult result, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var existing = (await store.BreaksForAsync(run.Party, run.BusinessDate, cancellationToken)).ToDictionary(item => (item.Kind, item.Key));
        var found = new HashSet<(string, string)>();
        foreach (var item in result.Breaks)
        {
            var kind = item.Kind.ToString();
            found.Add((kind, item.Key));
            if (!existing.TryGetValue((kind, item.Key), out var record))
            {
                record = new ReconBreak
                {
                    Id = Guid.CreateVersion7(now),
                    Party = run.Party,
                    BusinessDate = run.BusinessDate,
                    Kind = kind,
                    Key = item.Key,
                    FirstRunId = run.Id,
                    FoundAt = now,
                };
                store.Add(record);
                metrics.BreaksFound(kind, 1);
            }
            else if (record.Status == BreakStatuses.Cleared)
            {
                record.Status = BreakStatuses.Open;
                record.ClosedAt = null;
                store.Audit(run.TriggeredBy, "break.reappeared", $"break:{record.Id}", Json(new { run = run.Id }), now);
            }

            record.OurId = item.OurId;
            record.ProviderReference = item.ProviderReference;
            record.ClientReference = item.ClientReference;
            record.OurAmountPaisa = item.OurAmountPaisa;
            record.ProviderAmountPaisa = item.ProviderAmountPaisa;
            record.LinesInFile = item.LinesInFile;
            record.OurStatus = item.OurStatus?.ToString();
            record.Description = item.Description;
            record.LastRunId = run.Id;
            record.UpdatedAt = now;
        }

        foreach (var record in existing.Values.Where(item => BreakStatuses.IsOpen(item.Status) && !found.Contains((item.Kind, item.Key))))
        {
            record.Status = BreakStatuses.Cleared;
            record.ClosedAt = now;
            record.UpdatedAt = now;
            record.LastRunId = run.Id;
            store.Audit(run.TriggeredBy, "break.cleared", $"break:{record.Id}", Json(new { run = run.Id }), now);
        }
    }

    private static int Count(MatchResult result, BreakKind kind) => result.Breaks.Count(item => item.Kind == kind);

    public async Task<Result<RunDetail>> RunDetailAsync(Guid id, CancellationToken cancellationToken)
    {
        if (await store.RunAsync(id, cancellationToken) is not { } run)
        {
            return ReconErrors.RunNotFound;
        }

        var breaks = await store.BreaksForAsync(run.Party, run.BusinessDate, cancellationToken);
        return new RunDetail(run, breaks);
    }

    public async Task<Result<ExternalPayment>> PaymentAsync(Guid id, CancellationToken cancellationToken) =>
        await store.PaymentAsync(id, cancellationToken) is { } payment ? payment : ReconErrors.PaymentNotFound;

    public Task<IReadOnlyList<ReconRun>> RunsAsync(DateOnly? date, string? party, int? limit, CancellationToken cancellationToken) =>
        store.RunsAsync(date, party, Math.Clamp(limit ?? 50, 1, 500), cancellationToken);

    public async Task<Result<string>> FileContentAsync(Guid runId, CancellationToken cancellationToken) =>
        await store.RunAsync(runId, cancellationToken) is { FileId: { } fileId } && await store.FileAsync(fileId, cancellationToken) is { } file
            ? file.Content
            : ReconErrors.RunNotFound;

    /// <summary>Every party's latest run on <paramref name="date"/>, with the day's totals.</summary>
    public async Task<DaySummary> SummaryAsync(DateOnly date, CancellationToken cancellationToken)
    {
        var runs = (await store.LatestRunsAsync(date, date, cancellationToken)).ToDictionary(run => run.Party, StringComparer.Ordinal);
        var breaks = await store.BreaksAsync(new BreakQuery("open", null, null, date, date, 10_000), cancellationToken);
        var open = breaks.GroupBy(item => item.Party, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        var parties = Parties.Union(runs.Keys, StringComparer.Ordinal)
            .Select(party => new PartyDay(party, runs.GetValueOrDefault(party), open.GetValueOrDefault(party)))
            .ToList();
        var done = runs.Values.Where(run => run.Status == RunStatuses.Completed).ToList();
        return new DaySummary(
            date,
            parties,
            done.Sum(run => run.Lines),
            done.Sum(run => run.Matched),
            done.Sum(run => run.Breaks),
            breaks.Count,
            done.Sum(run => run.ProviderTotalPaisa),
            done.Sum(run => run.OurTotalPaisa));
    }

    /// <summary>The last <paramref name="days"/> days that have runs, newest first.</summary>
    public async Task<IReadOnlyList<DayOverview>> DaysAsync(int? days, CancellationToken cancellationToken)
    {
        var today = timeProvider.GetBusinessDate();
        var runs = await store.LatestRunsAsync(today.AddDays(-(Math.Clamp(days ?? 14, 1, 90) - 1)), today, cancellationToken);
        return [.. runs.GroupBy(run => run.BusinessDate).OrderByDescending(group => group.Key).Select(group =>
        {
            var done = group.Where(run => run.Status == RunStatuses.Completed).ToList();
            return new DayOverview(
                group.Key,
                done.Count,
                group.Count() - done.Count,
                done.Sum(run => run.Lines),
                done.Sum(run => run.Matched),
                done.Sum(run => run.Breaks),
                done.Sum(run => run.ProviderTotalPaisa),
                done.Sum(run => run.OurTotalPaisa));
        })];
    }

    internal static string Json(object value) => JsonSerializer.Serialize(value, JsonSerializerOptions.Web);
}

/// <summary>Finance's work on breaks (M12.3): dispute with the provider, resolve with a note, reopen a mistake.</summary>
public sealed class BreakService(IReconStore store, TimeProvider timeProvider)
{
    public Task<IReadOnlyList<ReconBreak>> ListAsync(BreakQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        return store.BreaksAsync(query with { Limit = Math.Clamp(query.Limit, 1, 1_000) }, cancellationToken);
    }

    public async Task<Result<BreakDetail>> DetailAsync(Guid id, CancellationToken cancellationToken)
    {
        if (await store.BreakAsync(id, cancellationToken) is not { } item)
        {
            return ReconErrors.BreakNotFound;
        }

        var payment = item.OurId is { } ourId ? await store.PaymentAsync(ourId, cancellationToken) : null;
        var history = await store.AuditTrailAsync(null, 200, $"break:{id}", cancellationToken);
        return new BreakDetail(item, payment, history);
    }

    public Task<Result<ReconBreak>> DisputeAsync(Guid id, string? note, string actor, CancellationToken cancellationToken) =>
        ChangeAsync(id, note, actor, "break.disputed", item => item.Status == BreakStatuses.Open ? null : ReconErrors.NotOpen, item =>
        {
            item.Status = BreakStatuses.Disputed;
            return new { };
        }, cancellationToken);

    public Task<Result<ReconBreak>> ResolveAsync(Guid id, string? resolution, string? note, string actor, CancellationToken cancellationToken)
    {
        var known = Resolutions.All.FirstOrDefault(value => string.Equals(value, resolution, StringComparison.OrdinalIgnoreCase));
        return known is null
            ? Task.FromResult<Result<ReconBreak>>(ReconErrors.UnknownResolution)
            : ChangeAsync(id, note, actor, "break.resolved", item => BreakStatuses.IsOpen(item.Status) ? null : ReconErrors.AlreadyClosed, item =>
            {
                item.Status = BreakStatuses.Resolved;
                item.Resolution = known;
                item.ClosedAt = timeProvider.GetUtcNow();
                return new { resolution = known };
            }, cancellationToken);
    }

    public Task<Result<ReconBreak>> ReopenAsync(Guid id, string? note, string actor, CancellationToken cancellationToken) =>
        ChangeAsync(id, note, actor, "break.reopened", item => BreakStatuses.IsOpen(item.Status) ? ReconErrors.NotClosed : null, item =>
        {
            item.Status = BreakStatuses.Open;
            item.Resolution = null;
            item.ClosedAt = null;
            return new { };
        }, cancellationToken);

    private async Task<Result<ReconBreak>> ChangeAsync(
        Guid id, string? note, string actor, string action, Func<ReconBreak, Error?> check, Func<ReconBreak, object> apply, CancellationToken cancellationToken)
    {
        var text = note?.Trim();
        if (text is not { Length: >= 5 and <= 1_000 })
        {
            return ReconErrors.NoteRequired;
        }

        return await store.InTransactionAsync<Result<ReconBreak>>(async () =>
        {
            if (await store.BreakAsync(id, cancellationToken) is not { } item)
            {
                return ReconErrors.BreakNotFound;
            }

            if (check(item) is { } error)
            {
                return error;
            }

            var now = timeProvider.GetUtcNow();
            var from = item.Status;
            var extra = apply(item);
            item.Note = text;
            item.UpdatedBy = actor;
            item.UpdatedAt = now;
            store.Audit(actor, action, $"break:{item.Id}", ReconciliationService.Json(new
            {
                from,
                to = item.Status,
                extra,
                note = text,
                party = item.Party,
                date = item.BusinessDate,
                kind = item.Kind,
                key = item.Key,
            }), now);
            return item;
        }, cancellationToken);
    }
}

/// <summary>The audit log (FR-5.3) and its check.</summary>
public sealed class AuditService(IReconStore store)
{
    public Task<IReadOnlyList<AuditEntry>> ListAsync(long? after, int? limit, CancellationToken cancellationToken) =>
        store.AuditTrailAsync(after, Math.Clamp(limit ?? 100, 1, 1_000), null, cancellationToken);

    public Task<IReadOnlyList<AuditEntry>> RecentAsync(long? before, int? limit, CancellationToken cancellationToken) =>
        store.AuditRecentAsync(before, Math.Clamp(limit ?? 50, 1, 500), cancellationToken);

    /// <summary>Recomputes every hash; the first entry whose hash or link is wrong, if any.</summary>
    public async Task<AuditVerification> VerifyAsync(CancellationToken cancellationToken)
    {
        var previous = AuditChain.Genesis;
        long count = 0;
        long? after = null;
        while (true)
        {
            var page = await store.AuditTrailAsync(after, 1_000, null, cancellationToken);
            foreach (var entry in page)
            {
                count++;
                if (entry.PreviousHash != previous || AuditChain.Hash(entry.PreviousHash, entry.Actor, entry.Action, entry.Subject, entry.Details, entry.At) != entry.Hash)
                {
                    return new AuditVerification(false, count, entry.Sequence, previous);
                }

                previous = entry.Hash;
            }

            if (page.Count < 1_000)
            {
                return new AuditVerification(true, count, null, previous);
            }

            after = page[^1].Sequence;
        }
    }
}

/// <param name="Intact">Every hash and link checks out.</param>
/// <param name="Entries">Entries checked (up to the broken one).</param>
/// <param name="BrokenAt">The first bad entry's sequence number.</param>
/// <param name="HeadHash">The last good hash.</param>
public sealed record AuditVerification(bool Intact, long Entries, long? BrokenAt, string HeadHash);

/// <summary>When the nightly run is due (M12.1): a pure function of the clock and what has run, for testing.</summary>
public static class ReconSchedule
{
    /// <summary>
    /// The day to reconcile now (yesterday, once <paramref name="dailyAt"/> has passed in Pakistan) and the
    /// parties still to do: those with no completed run that day whose last failure, if any, is at least
    /// <paramref name="retryAfter"/> old.
    /// </summary>
    public static (DateOnly Date, IReadOnlyList<string> Parties) Due(
        DateTimeOffset now, TimeOnly dailyAt, IReadOnlyList<string> parties, IReadOnlyList<ReconRun> runsThatDay, TimeSpan retryAfter)
    {
        ArgumentNullException.ThrowIfNull(parties);
        ArgumentNullException.ThrowIfNull(runsThatDay);
        var local = now.ToPakistanTime();
        var date = DateOnly.FromDateTime(local.DateTime).AddDays(-1);
        if (TimeOnly.FromDateTime(local.DateTime) < dailyAt)
        {
            return (date, []);
        }

        var latest = runsThatDay.Where(run => run.BusinessDate == date).GroupBy(run => run.Party, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.MaxBy(run => run.StartedAt)!, StringComparer.Ordinal);
        return (date, [.. parties.Where(party => !latest.TryGetValue(party, out var run)
            || (run.Status != RunStatuses.Completed && now - run.FinishedAt >= retryAfter))]);
    }
}
