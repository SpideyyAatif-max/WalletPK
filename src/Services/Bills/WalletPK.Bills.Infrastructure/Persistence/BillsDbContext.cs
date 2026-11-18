using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WalletPK.Bills.Application.Payments;
using WalletPK.Bills.Application.Saved;
using WalletPK.Persistence.Inbox;
using WalletPK.Persistence.Outbox;
using WalletPK.Sagas;

namespace WalletPK.Bills.Infrastructure.Persistence;

/// <summary>
/// The Bills database (walletpk_bills): payments live in their Saga rows (saga_instances, saga_steps);
/// saved billers in saved_billers; Ledger commands and Bills events leave through the outbox, the
/// Ledger's replies come in through the inbox.
/// </summary>
public sealed class BillsDbContext(DbContextOptions<BillsDbContext> options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.AddOutboxMessages();
        modelBuilder.AddInboxMessages();
        modelBuilder.AddSagas();
        modelBuilder.ApplyConfiguration(new SavedBillerConfiguration());
    }
}

internal sealed class SavedBillerConfiguration : IEntityTypeConfiguration<SavedBiller>
{
    public void Configure(EntityTypeBuilder<SavedBiller> builder)
    {
        builder.ToTable("saved_billers");
        builder.HasKey(saved => saved.Id);
        builder.Property(saved => saved.Id).ValueGeneratedNever();
        builder.Property(saved => saved.Biller).HasMaxLength(16).IsRequired();
        builder.Property(saved => saved.ConsumerNumber).HasMaxLength(20).IsRequired();
        builder.Property(saved => saved.Nickname).HasMaxLength(40);
        builder.Property(saved => saved.CustomerName).HasMaxLength(100);
        builder.Property(saved => saved.LastBillingMonth).HasMaxLength(7);
        builder.Property(saved => saved.LastStatus).HasMaxLength(16);
        builder.Property(saved => saved.RemindedForMonth).HasMaxLength(7);
        builder.HasIndex(saved => new { saved.UserId, saved.Biller, saved.ConsumerNumber }).IsUnique();
        builder.HasIndex(saved => saved.NextCheckAt);
    }
}

internal sealed class SavedBillers(BillsDbContext db) : ISavedBillers
{
    public async Task<IReadOnlyList<SavedBiller>> ForUserAsync(Guid userId, CancellationToken cancellationToken) =>
        await db.Set<SavedBiller>().Where(saved => saved.UserId == userId).OrderBy(saved => saved.CreatedAt).ToListAsync(cancellationToken);

    public Task<SavedBiller?> FindAsync(Guid userId, Guid id, CancellationToken cancellationToken) =>
        db.Set<SavedBiller>().SingleOrDefaultAsync(saved => saved.UserId == userId && saved.Id == id, cancellationToken);

    public Task<SavedBiller?> FindAsync(Guid userId, string biller, string consumerNumber, CancellationToken cancellationToken) =>
        db.Set<SavedBiller>().SingleOrDefaultAsync(saved => saved.UserId == userId && saved.Biller == biller && saved.ConsumerNumber == consumerNumber, cancellationToken);

    public void Add(SavedBiller saved) => db.Set<SavedBiller>().Add(saved);

    public void Remove(SavedBiller saved) => db.Set<SavedBiller>().Remove(saved);

    public Task SaveChangesAsync(CancellationToken cancellationToken) => db.SaveChangesAsync(cancellationToken);
}

/// <summary>Lists over saga_instances (indexes in the migrations).</summary>
internal sealed class BillPaymentQueries(BillsDbContext db) : IBillPaymentQueries
{
    public async Task<IReadOnlyList<SagaSnapshot<BillPaymentData>>> ForUserAsync(Guid userId, int limit, DateTimeOffset? before, CancellationToken cancellationToken)
    {
        var user = userId.ToString();
        var sagaType = BillPaymentSaga.SagaName;
        var query = before is { } cursor
            ? db.Set<SagaInstance>().FromSql($"""
                SELECT * FROM saga_instances
                 WHERE saga_type = {sagaType} AND data->>'userId' = {user} AND created_at < {cursor}
                 ORDER BY created_at DESC LIMIT {limit}
                """)
            : db.Set<SagaInstance>().FromSql($"""
                SELECT * FROM saga_instances
                 WHERE saga_type = {sagaType} AND data->>'userId' = {user}
                 ORDER BY created_at DESC LIMIT {limit}
                """);
        var rows = await query.AsNoTracking().ToListAsync(cancellationToken);
        return [.. rows.Select(row => SagaReader<BillsDbContext>.ToSnapshot<BillPaymentData>(row, []))];
    }

    public async Task<IReadOnlyList<SagaSnapshot<BillPaymentData>>> InStatesAsync(IReadOnlyList<string> states, int limit, CancellationToken cancellationToken)
    {
        var sagaType = BillPaymentSaga.SagaName;
        var wanted = states.ToArray();
        var rows = await db.Set<SagaInstance>()
            .Where(saga => saga.SagaType == sagaType && wanted.Contains(saga.State))
            .OrderBy(saga => saga.State == wanted[0] ? 0 : 1)
            .ThenBy(saga => saga.UpdatedAt)
            .Take(limit)
            .AsNoTracking()
            .ToListAsync(cancellationToken);
        return [.. rows.Select(row => SagaReader<BillsDbContext>.ToSnapshot<BillPaymentData>(row, []))];
    }
}
