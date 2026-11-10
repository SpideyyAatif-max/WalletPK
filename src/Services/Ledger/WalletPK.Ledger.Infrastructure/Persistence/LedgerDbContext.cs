using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using WalletPK.Ledger.Application.Abstractions;
using WalletPK.Ledger.Domain.Accounts;
using WalletPK.Ledger.Domain.Holds;
using WalletPK.Ledger.Domain.Journals;
using WalletPK.Ledger.Infrastructure.Audit;
using WalletPK.Ledger.Infrastructure.Idempotency;
using WalletPK.Persistence.Inbox;
using WalletPK.Persistence.Outbox;

namespace WalletPK.Ledger.Infrastructure.Persistence;

/// <summary>The Ledger service's database (walletpk_ledger). Also the unit of work for its use cases.</summary>
public sealed class LedgerDbContext(DbContextOptions<LedgerDbContext> options) : DbContext(options), ILedgerUnitOfWork
{
    public DbSet<Account> Accounts => Set<Account>();

    public DbSet<JournalTransaction> JournalTransactions => Set<JournalTransaction>();

    public DbSet<JournalEntry> JournalEntries => Set<JournalEntry>();

    public DbSet<Hold> Holds => Set<Hold>();

    public DbSet<IdempotencyRecord> IdempotencyKeys => Set<IdempotencyRecord>();

    /// <summary>Tamper-evident history of every money-relevant change (M3.10).</summary>
    public DbSet<AuditRecord> AuditLog => Set<AuditRecord>();

    /// <summary>Integration events waiting for the relay (M4.1).</summary>
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    async Task<ILedgerTransaction> ILedgerUnitOfWork.BeginTransactionAsync(CancellationToken cancellationToken) =>
        Database.CurrentTransaction is null
            ? new OwnedTransaction(await Database.BeginTransactionAsync(cancellationToken))
            : JoinedTransaction.Instance;

    async Task ILedgerUnitOfWork.SaveChangesAsync(CancellationToken cancellationToken)
    {
        try
        {
            await SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException exception)
        {
            // An account's xmin changed since it was read: another posting saved first.
            ChangeTracker.Clear();
            throw new ConcurrencyConflictException("An account was changed by another posting.", exception);
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
            ConstraintName: "ux_journal_entries_account_sequence",
        })
        {
            // Two postings to the same (system) account read the same counter and both took the next
            // entry number; the database let only one have it. Same race as a stale xmin: retry from fresh data.
            ChangeTracker.Clear();
            throw new ConcurrencyConflictException("Another posting took the same entry number on an account.", exception);
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } postgres)
        {
            ChangeTracker.Clear();
            throw new DuplicateKeyException($"Unique constraint '{postgres.ConstraintName}' was violated.", exception);
        }
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(LedgerDbContext).Assembly);
        modelBuilder.AddOutboxMessages();
        modelBuilder.AddInboxMessages();
    }
}

/// <summary>A transaction this attempt started: it commits it, or rolls it back on dispose.</summary>
internal sealed class OwnedTransaction(IDbContextTransaction transaction) : ILedgerTransaction
{
    public Task CommitAsync(CancellationToken cancellationToken) => transaction.CommitAsync(cancellationToken);

    public ValueTask DisposeAsync() => transaction.DisposeAsync();
}

/// <summary>Someone else's transaction (for example an inbox handler's): its owner commits or rolls back.</summary>
internal sealed class JoinedTransaction : ILedgerTransaction
{
    public static JoinedTransaction Instance { get; } = new();

    public Task CommitAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
