using WalletPK.Reconciliation.Domain;

namespace WalletPK.Reconciliation.Application;

/// <summary>The store behind the use cases.</summary>
public interface IReconStore
{
    Task<ExternalPayment?> PaymentAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>Payments with any of these IDs or provider references.</summary>
    Task<IReadOnlyList<ExternalPayment>> PaymentsAsync(IReadOnlyCollection<Guid> ids, IReadOnlyCollection<string> providerReferences, CancellationToken cancellationToken);

    /// <summary>The party's payments that completed on <paramref name="date"/> (Pakistan time).</summary>
    Task<IReadOnlyList<ExternalPayment>> CompletedOnAsync(string party, DateOnly date, CancellationToken cancellationToken);

    /// <summary>Which of <paramref name="ids"/> the latest files of <paramref name="party"/> for <paramref name="dates"/> list.</summary>
    Task<IReadOnlySet<Guid>> ListedOnAsync(string party, IReadOnlyCollection<DateOnly> dates, IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken);

    /// <summary>Stores a fetched file; when it parsed, its lines replace the previous file's for that party and day.</summary>
    Task SaveFileAsync(SettlementFileRecord file, IReadOnlyList<SettlementLine>? lines, CancellationToken cancellationToken);

    Task<SettlementFileRecord?> FileAsync(Guid id, CancellationToken cancellationToken);

    Task<IReadOnlyList<ReconBreak>> BreaksForAsync(string party, DateOnly date, CancellationToken cancellationToken);

    Task<ReconBreak?> BreakAsync(Guid id, CancellationToken cancellationToken);

    Task<IReadOnlyList<ReconBreak>> BreaksAsync(BreakQuery query, CancellationToken cancellationToken);

    /// <summary>Open and disputed breaks by kind.</summary>
    Task<IReadOnlyDictionary<string, int>> OpenBreakCountsAsync(CancellationToken cancellationToken);

    Task<ReconRun?> RunAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>Runs, newest first.</summary>
    Task<IReadOnlyList<ReconRun>> RunsAsync(DateOnly? date, string? party, int limit, CancellationToken cancellationToken);

    /// <summary>The newest run of each party for each day from <paramref name="from"/> on.</summary>
    Task<IReadOnlyList<ReconRun>> LatestRunsAsync(DateOnly from, DateOnly to, CancellationToken cancellationToken);

    void Add(object entity);

    /// <summary>Queues an audit entry: it is chained and written when the current <see cref="InTransactionAsync{T}"/> commits.</summary>
    void Audit(string actor, string action, string subject, string details, DateTimeOffset at);

    Task<IReadOnlyList<AuditEntry>> AuditTrailAsync(long? after, int limit, string? subject, CancellationToken cancellationToken);

    /// <summary>The audit log newest first, before <paramref name="before"/>.</summary>
    Task<IReadOnlyList<AuditEntry>> AuditRecentAsync(long? before, int limit, CancellationToken cancellationToken);

    /// <summary>
    /// Runs <paramref name="work"/>, saves and writes the queued audit entries in one transaction, holding the
    /// reconciliation lock: runs and Finance's actions happen one at a time, so the chain never forks.
    /// </summary>
    Task<T> InTransactionAsync<T>(Func<Task<T>> work, CancellationToken cancellationToken);

    Task SaveChangesAsync(CancellationToken cancellationToken);
}

/// <summary>A fetched settlement file, or why there is none.</summary>
public sealed record SettlementFetch(string? Content, string? Problem);

/// <summary>Where settlement files come from (the providers' SFTP drops; here, the mock providers over HTTP).</summary>
public interface ISettlementSource
{
    Task<SettlementFetch> FetchAsync(string party, DateOnly date, CancellationToken cancellationToken);
}

/// <summary>Counts for dashboards.</summary>
public interface IReconMetrics
{
    void RunFinished(string party, string status);

    void BreaksFound(string kind, int count);

    void OpenBreaks(IReadOnlyDictionary<string, int> byKind);
}
