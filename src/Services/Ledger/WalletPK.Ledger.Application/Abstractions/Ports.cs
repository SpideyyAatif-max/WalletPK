using WalletPK.Common.Identifiers;
using WalletPK.Ledger.Domain.Accounts;
using WalletPK.Ledger.Domain.Journals;

namespace WalletPK.Ledger.Application.Abstractions;

public interface IAccountRepository
{
    Task<Account?> GetByIdAsync(AccountId id, CancellationToken cancellationToken);

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
