using Microsoft.EntityFrameworkCore;
using Npgsql;
using WalletPK.Identity.Application.Abstractions;
using WalletPK.Identity.Domain.Devices;
using WalletPK.Identity.Domain.Kyc;
using WalletPK.Identity.Domain.Sessions;
using WalletPK.Identity.Domain.Users;
using WalletPK.Identity.Infrastructure.Persistence.Outbox;

namespace WalletPK.Identity.Infrastructure.Persistence;

/// <summary>The Identity service's database (walletpk_identity). Also the unit of work for its use cases.</summary>
public sealed class IdentityDbContext(DbContextOptions<IdentityDbContext> options) : DbContext(options), IUnitOfWork
{
    public DbSet<User> Users => Set<User>();

    public DbSet<Device> Devices => Set<Device>();

    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();

    public DbSet<KycProfile> KycProfiles => Set<KycProfile>();

    /// <summary>Integration events waiting for the relay (M4.1); written by <see cref="DomainEventOutboxInterceptor"/>.</summary>
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    async Task IUnitOfWork.SaveChangesAsync(CancellationToken cancellationToken)
    {
        try
        {
            await SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException exception)
        {
            // The row's xmin changed since it was read: another request saved first.
            ChangeTracker.Clear();
            throw new ConcurrencyConflictException("The data was changed by another request.", exception);
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } postgres)
        {
            ChangeTracker.Clear();
            throw new UniqueConstraintViolationException($"Unique constraint '{postgres.ConstraintName}' was violated.", exception)
            {
                ConstraintName = postgres.ConstraintName,
            };
        }
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(IdentityDbContext).Assembly);
    }
}
