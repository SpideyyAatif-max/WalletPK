using WalletPK.Common.Identifiers;
using WalletPK.Common.Primitives;
using WalletPK.Ledger.Domain.Accounts;
using WalletPK.Ledger.Domain.Holds;
using WalletPK.Ledger.Domain.Journals;

namespace WalletPK.Ledger.Application.Abstractions;

public interface IAccountRepository
{
    Task<Account?> GetByIdAsync(AccountId id, CancellationToken cancellationToken);

    /// <summary>The user's wallet of the given type, or null.</summary>
    Task<Account?> GetWalletByOwnerAsync(UserId ownerId, AccountType type, CancellationToken cancellationToken);

    /// <summary>The wallet with this number (E.164), or null.</summary>
    Task<Account?> GetWalletByAliasAsync(string alias, CancellationToken cancellationToken);

    /// <summary>Loads several accounts in one query (tracked, so posting can update them).</summary>
    Task<IReadOnlyList<Account>> GetManyAsync(IReadOnlyCollection<AccountId> ids, CancellationToken cancellationToken);

    void Add(Account account);
}

public interface IJournalRepository
{
    /// <summary>The journal with its entries, or null.</summary>
    Task<JournalTransaction?> GetAsync(TransactionId id, CancellationToken cancellationToken);

    void Add(JournalTransaction journal);
}

public interface IHoldRepository
{
    Task<Hold?> GetAsync(HoldId id, CancellationToken cancellationToken);

    /// <summary>IDs of active holds whose time ran out, oldest first.</summary>
    Task<IReadOnlyList<HoldId>> ListExpiredAsync(DateTimeOffset now, int limit, CancellationToken cancellationToken);

    void Add(Hold hold);
}

/// <summary>
/// Exclusive locks on wallets (Redis in production), so two payments from the same wallet never run at
/// the same time on different servers. Each lock comes with a <b>fencing token</b>: a number that only
/// ever grows. The token is written with the payment, and the account refuses a token older than the
/// last one it saw, so a server whose lock silently expired (a long pause) cannot write afterwards.
/// </summary>
public interface IAccountLockService
{
    /// <summary>
    /// Locks all the given accounts (in a fixed order, so two callers can never deadlock), waiting a
    /// short time if they are busy. Returns null if they stay busy.
    /// </summary>
    Task<IAccountLockLease?> TryAcquireAsync(IReadOnlyCollection<AccountId> accountIds, CancellationToken cancellationToken);
}

/// <summary>Held locks; disposing releases them.</summary>
public interface IAccountLockLease : IAsyncDisposable
{
    /// <summary>The fencing token of each locked account.</summary>
    IReadOnlyDictionary<AccountId, long> FencingTokens { get; }
}

/// <summary>The Identity service's answer to "may this user debit this much now?".</summary>
/// <param name="Allowed">True when the debit fits and has been counted against the limits.</param>
/// <param name="Decision">For example "Allowed", "ExceedsDaily", "CoolingOff".</param>
/// <param name="Reason">Customer-facing explanation.</param>
public sealed record LimitDecisionResult(bool Allowed, string Decision, string Reason);

/// <summary>
/// Transaction limits, owned by the Identity service and called over gRPC (M2.6). The Ledger reserves
/// before every customer debit and releases if the debit then fails.
/// </summary>
public interface ILimitsGateway
{
    /// <exception cref="LimitsUnavailableException">The Identity service could not be reached; the debit must not go ahead.</exception>
    Task<LimitDecisionResult> ReserveAsync(TransactionId transactionId, UserId userId, Domain.Journals.JournalType type, Money amount, string? deviceId, CancellationToken cancellationToken);

    /// <summary>Best effort; a release that fails is reconciled later (the reservation simply expires with the day).</summary>
    Task ReleaseAsync(TransactionId transactionId, CancellationToken cancellationToken);
}

/// <summary>The limits service did not answer. Payments fail closed: no answer means no payment.</summary>
public sealed class LimitsUnavailableException : Exception
{
    public LimitsUnavailableException()
    {
    }

    public LimitsUnavailableException(string message)
        : base(message)
    {
    }

    public LimitsUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>One line of a wallet's history (a statement line).</summary>
/// <param name="EntryId">Position in the ledger; also the paging cursor.</param>
/// <param name="TransactionId">The journal.</param>
/// <param name="Type">What happened.</param>
/// <param name="Description">The journal's description.</param>
/// <param name="Direction">Debit = money out of the wallet, Credit = money in.</param>
/// <param name="Amount">The entry amount.</param>
/// <param name="BalanceAfter">The wallet's balance right after it.</param>
/// <param name="PostedAt">When.</param>
/// <param name="CounterpartyNumber">The other wallet's number (E.164), for transfers between wallets.</param>
public sealed record HistoryLine(
    long EntryId,
    TransactionId TransactionId,
    Domain.Journals.JournalType Type,
    string Description,
    Domain.Journals.EntryDirection Direction,
    Money Amount,
    Money BalanceAfter,
    DateTimeOffset PostedAt,
    string? CounterpartyNumber);

/// <summary>Read-only queries (no tracking, no domain objects), for screens.</summary>
public interface ILedgerQueries
{
    /// <summary>The account's entries, newest first, older than <paramref name="beforeEntryId"/> when given.</summary>
    Task<IReadOnlyList<HistoryLine>> GetHistoryAsync(AccountId accountId, int limit, long? beforeEntryId, CancellationToken cancellationToken);
}

/// <summary>Commits one use case's changes (accounts, journal, entries, outbox) in a single database transaction.</summary>
public interface ILedgerUnitOfWork
{
    /// <exception cref="DuplicateKeyException">A row with the same key already exists (for example the same transaction ID).</exception>
    /// <exception cref="ConcurrencyConflictException">An account changed between being read and being saved.</exception>
    Task SaveChangesAsync(CancellationToken cancellationToken);
}

/// <summary>A unique key was violated, for example a journal with this transaction ID was already posted.</summary>
public sealed class DuplicateKeyException : Exception
{
    public DuplicateKeyException()
    {
    }

    public DuplicateKeyException(string message)
        : base(message)
    {
    }

    public DuplicateKeyException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>Another posting changed one of the accounts first (optimistic concurrency); nothing was saved.</summary>
public sealed class ConcurrencyConflictException : Exception
{
    public ConcurrencyConflictException()
    {
    }

    public ConcurrencyConflictException(string message)
        : base(message)
    {
    }

    public ConcurrencyConflictException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
