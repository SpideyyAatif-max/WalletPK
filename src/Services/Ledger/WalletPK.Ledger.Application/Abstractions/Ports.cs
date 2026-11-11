using WalletPK.Common.Identifiers;
using WalletPK.Common.Primitives;
using WalletPK.Ledger.Domain.Accounts;
using WalletPK.Ledger.Domain.Holds;
using WalletPK.Ledger.Domain.Journals;

namespace WalletPK.Ledger.Application.Abstractions;

public interface IAccountRepository
{
    Task<Account?> GetByIdAsync(AccountId id, CancellationToken cancellationToken);

    /// <summary>
    /// The user's wallet of the given type, or null. A read-only snapshot (not tracked): use it to find
    /// the wallet, then change it only through a use case that re-reads it under the wallet lock.
    /// </summary>
    Task<Account?> GetWalletByOwnerAsync(UserId ownerId, AccountType type, CancellationToken cancellationToken);

    /// <summary>The wallet with this number (E.164), or null. A read-only snapshot, like <see cref="GetWalletByOwnerAsync"/>.</summary>
    Task<Account?> GetWalletByAliasAsync(string alias, CancellationToken cancellationToken);

    /// <summary>Loads several accounts in one query (tracked, so posting can update them).</summary>
    /// <summary>
    /// Loads accounts to change them, locking their rows (in ID order, so two postings can never
    /// deadlock) until the current transaction ends. A busy system account (a fee or tax shard) is then
    /// updated by one posting after another instead of all of them colliding and retrying.
    /// </summary>
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
    /// <summary>Loads the hold to change it. Call only while holding its wallet's lock.</summary>
    Task<Hold?> GetAsync(HoldId id, CancellationToken cancellationToken);

    /// <summary>
    /// A read-only snapshot (not tracked), to find out which wallet to lock. Never change it: a tracked
    /// copy read before the lock would hide changes another request made while we waited for the lock.
    /// </summary>
    Task<Hold?> PeekAsync(HoldId id, CancellationToken cancellationToken);

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

/// <summary>Commits one use case's changes (accounts, journal, entries, outbox) in a single database transaction.</summary>
public interface ILedgerUnitOfWork
{
    /// <exception cref="DuplicateKeyException">A row with the same key already exists (for example the same transaction ID).</exception>
    /// <exception cref="ConcurrencyConflictException">An account changed between being read and being saved.</exception>
    Task SaveChangesAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Starts a database transaction for one attempt: the row locks taken while loading accounts are
    /// held until it commits. If a transaction is already open (an inbox handler's), joins it instead:
    /// committing is then left to its owner.
    /// </summary>
    Task<ILedgerTransaction> BeginTransactionAsync(CancellationToken cancellationToken);
}

/// <summary>A database transaction; disposing it without committing rolls it back.</summary>
public interface ILedgerTransaction : IAsyncDisposable
{
    Task CommitAsync(CancellationToken cancellationToken);
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
