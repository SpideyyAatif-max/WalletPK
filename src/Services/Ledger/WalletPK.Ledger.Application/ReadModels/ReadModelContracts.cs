using WalletPK.Common.Identifiers;
using WalletPK.Common.Primitives;
using WalletPK.Ledger.Domain.Accounts;
using WalletPK.Ledger.Domain.Journals;

namespace WalletPK.Ledger.Application.ReadModels;

// The read side (M4.4-M4.6): tables in the "read" schema, built from the ledger.* events on Kafka and
// queried with plain SQL. They lag the ledger by milliseconds and can be thrown away and rebuilt from
// the topic at any time; the ledger tables stay the only source of truth for money.

/// <summary>A wallet as the read model knows it.</summary>
/// <param name="WalletId">The wallet (ledger account) ID.</param>
/// <param name="WalletNumber">The mobile number people send money to.</param>
/// <param name="Type">Customer, merchant or agent wallet.</param>
/// <param name="Balance">Balance after the newest entry the read model has seen.</param>
/// <param name="Sequence">That entry's number in the wallet's history (0 = none yet).</param>
/// <param name="UpdatedAt">When that entry was posted (or the wallet opened).</param>
public sealed record WalletBalanceRow(AccountId WalletId, string? WalletNumber, AccountType Type, Money Balance, long Sequence, DateTimeOffset UpdatedAt);

/// <summary>One page of a wallet's history, newest first. Every filter is optional.</summary>
/// <param name="WalletId">The wallet.</param>
/// <param name="Limit">Most rows to return.</param>
/// <param name="BeforeSequence">Only entries older than this one (paging cursor).</param>
/// <param name="Type">Only this kind of journal.</param>
/// <param name="Direction">Only money in (Credit) or out (Debit).</param>
/// <param name="From">Posted at or after.</param>
/// <param name="To">Posted before.</param>
public sealed record HistoryQuery(
    AccountId WalletId,
    int Limit,
    long? BeforeSequence = null,
    JournalType? Type = null,
    EntryDirection? Direction = null,
    DateTimeOffset? From = null,
    DateTimeOffset? To = null);

/// <summary>One entry in a wallet's history.</summary>
/// <param name="Sequence">The entry's number in the wallet's history (1, 2, 3 ...).</param>
/// <param name="TransactionId">The journal.</param>
/// <param name="Type">What happened.</param>
/// <param name="Description">The journal's description.</param>
/// <param name="Direction">Debit = money out of the wallet, Credit = money in.</param>
/// <param name="Amount">The entry amount.</param>
/// <param name="BalanceAfter">The wallet's balance right after it.</param>
/// <param name="CounterpartyWalletNumber">The other wallet (E.164) in a wallet-to-wallet payment.</param>
/// <param name="PostedAt">When.</param>
public sealed record HistoryRow(
    long Sequence,
    TransactionId TransactionId,
    JournalType Type,
    string Description,
    EntryDirection Direction,
    Money Amount,
    Money BalanceAfter,
    string? CounterpartyWalletNumber,
    DateTimeOffset PostedAt);

/// <summary>Journals of one type posted in one minute (UTC).</summary>
/// <param name="Minute">Start of the minute.</param>
/// <param name="JournalType">For example "P2PTransfer".</param>
/// <param name="Count">How many journals.</param>
/// <param name="Volume">Total money moved.</param>
public sealed record MinuteMetric(DateTimeOffset Minute, string JournalType, long Count, Money Volume);

/// <summary>Fast reads from the read models (Dapper; p95 under 10 ms).</summary>
public interface IReadModelQueries
{
    /// <summary>The user's wallet of <paramref name="type"/>, or null when the read model has not seen it (yet).</summary>
    Task<WalletBalanceRow?> GetWalletAsync(UserId ownerId, AccountType type, CancellationToken cancellationToken);

    Task<IReadOnlyList<HistoryRow>> GetHistoryAsync(HistoryQuery query, CancellationToken cancellationToken);

    /// <summary>Per-minute journal counts and volumes in [<paramref name="from"/>, <paramref name="to"/>), oldest first.</summary>
    Task<IReadOnlyList<MinuteMetric>> GetMetricsAsync(DateTimeOffset from, DateTimeOffset to, JournalType? type, CancellationToken cancellationToken);
}

/// <summary>How complete the read models are, compared with the ledger itself.</summary>
/// <param name="Generation">Bumped by every rebuild; the projection's consumer group is named after it.</param>
/// <param name="RebuiltAt">When the last rebuild started (null if never rebuilt).</param>
/// <param name="Wallets">Wallets in the balance read model.</param>
/// <param name="HistoryRows">Rows in the history read model.</param>
/// <param name="WalletEntries">Entries on wallets in the ledger (what <paramref name="HistoryRows"/> should reach).</param>
/// <param name="ProjectedJournals">Journals counted in the metrics.</param>
/// <param name="Journals">Journals in the ledger.</param>
public sealed record ReadModelStatus(
    long Generation,
    DateTimeOffset? RebuiltAt,
    long Wallets,
    long HistoryRows,
    long WalletEntries,
    long ProjectedJournals,
    long Journals)
{
    /// <summary>True when every ledger entry and journal has reached the read models.</summary>
    public bool CaughtUp => HistoryRows == WalletEntries && ProjectedJournals == Journals;
}

/// <summary>Operations on the read models themselves (M4.6).</summary>
public interface IReadModelMaintenance
{
    Task<ReadModelStatus> GetStatusAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Empties the read models and starts a new generation, which replays ledger.events from offset 0.
    /// Returns the new generation. Reads are incomplete until the replay catches up (see the status).
    /// </summary>
    Task<long> RebuildAsync(CancellationToken cancellationToken);
}
