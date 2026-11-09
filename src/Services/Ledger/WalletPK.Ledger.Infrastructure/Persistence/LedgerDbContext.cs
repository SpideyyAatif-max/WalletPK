using Microsoft.EntityFrameworkCore;
using Npgsql;
using WalletPK.Ledger.Application.Abstractions;
using WalletPK.Ledger.Domain.Accounts;
using WalletPK.Ledger.Domain.Holds;
using WalletPK.Ledger.Domain.Journals;
using WalletPK.Ledger.Infrastructure.Audit;
using WalletPK.Ledger.Infrastructure.Idempotency;
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
    }
}
