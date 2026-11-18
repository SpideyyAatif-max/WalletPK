using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WalletPK.Messaging.Events;
using WalletPK.Messaging.Serialization;
using WalletPK.Persistence.Inbox;
using WalletPK.Persistence.Outbox;
using WalletPK.Sagas;
using WalletPK.TopupMerchant.Application.Merchants;
using WalletPK.TopupMerchant.Application.TopUps;

namespace WalletPK.TopupMerchant.Infrastructure.Persistence;

/// <summary>
/// The Top-up &amp; Merchant database (walletpk_topup_merchant): top-ups and QR payments live in their Saga
/// rows (saga_instances, saga_steps); merchants, their QR codes and settlements in their own tables. Ledger
/// commands and our events leave through the outbox, the Ledger's replies come in through the inbox.
/// </summary>
public sealed class TopupMerchantDbContext(DbContextOptions<TopupMerchantDbContext> options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.AddOutboxMessages();
        modelBuilder.AddInboxMessages();
        modelBuilder.AddSagas();
        modelBuilder.ApplyConfiguration(new MerchantConfiguration());
        modelBuilder.ApplyConfiguration(new QrCodeConfiguration());
        modelBuilder.ApplyConfiguration(new MerchantSettlementConfiguration());
        modelBuilder.ApplyConfiguration(new SettledPaymentConfiguration());
    }
}

internal sealed class MerchantConfiguration : IEntityTypeConfiguration<Merchant>
{
    public void Configure(EntityTypeBuilder<Merchant> builder)
    {
        builder.ToTable("merchants");
        builder.HasKey(merchant => merchant.Id);
        builder.Property(merchant => merchant.Id).ValueGeneratedNever();
        builder.Property(merchant => merchant.Code).HasMaxLength(10).IsRequired();
        builder.Property(merchant => merchant.Name).HasMaxLength(60).IsRequired();
        builder.Property(merchant => merchant.Category).HasMaxLength(16).IsRequired();
        builder.Property(merchant => merchant.FeePlan).HasMaxLength(16).IsRequired();
        builder.Property(merchant => merchant.City).HasMaxLength(40);
        builder.Property(merchant => merchant.Status).HasMaxLength(16).IsRequired();
        builder.HasIndex(merchant => merchant.OwnerUserId).IsUnique();
        builder.HasIndex(merchant => merchant.Code).IsUnique();
    }
}

internal sealed class QrCodeConfiguration : IEntityTypeConfiguration<QrCode>
{
    public void Configure(EntityTypeBuilder<QrCode> builder)
    {
        builder.ToTable("merchant_qr_codes");
        builder.HasKey(code => code.Id);
        builder.Property(code => code.Id).ValueGeneratedNever();
        builder.Property(code => code.Reference).HasMaxLength(40);
        builder.HasOne<Merchant>().WithMany().HasForeignKey(code => code.MerchantId).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(code => new { code.MerchantId, code.CreatedAt });
    }
}

internal sealed class MerchantSettlementConfiguration : IEntityTypeConfiguration<MerchantSettlement>
{
    public void Configure(EntityTypeBuilder<MerchantSettlement> builder)
    {
        builder.ToTable("merchant_settlements");
        builder.HasKey(settlement => settlement.Id);
        builder.Property(settlement => settlement.Id).ValueGeneratedNever();
        builder.Property(settlement => settlement.Status).HasMaxLength(16).IsRequired();
        builder.Property(settlement => settlement.FailureReason).HasMaxLength(200);
        builder.HasOne<Merchant>().WithMany().HasForeignKey(settlement => settlement.MerchantId).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(settlement => new { settlement.MerchantId, settlement.BusinessDate });
        builder.HasIndex(settlement => settlement.Status);
    }
}

internal sealed class SettledPaymentConfiguration : IEntityTypeConfiguration<SettledPayment>
{
    public void Configure(EntityTypeBuilder<SettledPayment> builder)
    {
        builder.ToTable("settled_payments");
        builder.HasKey(settled => settled.PaymentId);
        builder.Property(settled => settled.PaymentId).ValueGeneratedNever();
        builder.HasOne<MerchantSettlement>().WithMany().HasForeignKey(settled => settled.SettlementId).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(settled => settled.SettlementId);
    }
}

internal sealed class MerchantStore(TopupMerchantDbContext db, JsonEventSerializer serializer, TimeProvider timeProvider) : IMerchantStore
{
    public Task<Merchant?> ByOwnerAsync(Guid ownerUserId, CancellationToken cancellationToken) =>
        db.Set<Merchant>().SingleOrDefaultAsync(merchant => merchant.OwnerUserId == ownerUserId, cancellationToken);

    public Task<Merchant?> ByIdAsync(Guid id, CancellationToken cancellationToken) =>
        db.Set<Merchant>().SingleOrDefaultAsync(merchant => merchant.Id == id, cancellationToken);

    public Task<Merchant?> ByCodeAsync(string code, CancellationToken cancellationToken) =>
        db.Set<Merchant>().SingleOrDefaultAsync(merchant => merchant.Code == code, cancellationToken);

    public void Add(Merchant merchant) => db.Set<Merchant>().Add(merchant);

    public void Add(QrCode code) => db.Set<QrCode>().Add(code);

    public Task<QrCode?> QrAsync(Guid id, CancellationToken cancellationToken) =>
        db.Set<QrCode>().AsNoTracking().SingleOrDefaultAsync(code => code.Id == id, cancellationToken);

    public async Task<bool> TryClaimAsync(Guid qrId, Guid paymentId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var failed = "Failed";
        var claimed = await db.Database.ExecuteSqlAsync(
            $"""
            UPDATE merchant_qr_codes AS qr SET payment_id = {paymentId}
             WHERE qr.id = {qrId} AND qr.expires_at > {now}
               AND (qr.payment_id IS NULL
                    OR EXISTS (SELECT 1 FROM saga_instances AS s WHERE s.id = qr.payment_id AND s.state = {failed}))
            """,
            cancellationToken);
        return claimed == 1;
    }

    public async Task<IReadOnlyList<MerchantSettlement>> SettlementsAsync(Guid merchantId, int limit, CancellationToken cancellationToken) =>
        await db.Set<MerchantSettlement>().AsNoTracking()
            .Where(settlement => settlement.MerchantId == merchantId)
            .OrderByDescending(settlement => settlement.CreatedAt)
            .Take(limit)
            .ToListAsync(cancellationToken);

    public async Task<UnsettledTotals> UnsettledAsync(Guid merchantId, CancellationToken cancellationToken)
    {
        var rows = await MerchantPaymentQueries.UnsettledAsync(db, merchantId, before: null, limit: int.MaxValue, cancellationToken);
        return new UnsettledTotals(rows.Count, rows.Sum(row => row.AmountPaisa), rows.Sum(row => row.FeePaisa), rows.Sum(row => row.TaxPaisa), rows.Sum(row => row.NetPaisa));
    }

    public void Publish(IntegrationEvent message, string? correlationId) =>
        db.AddToOutbox(serializer, message, timeProvider.GetUtcNow(), correlationId);

    public Task SaveChangesAsync(CancellationToken cancellationToken) => db.SaveChangesAsync(cancellationToken);
}

/// <summary>A completed QR payment not yet in a settlement.</summary>
internal sealed record UnsettledPayment(Guid PaymentId, Guid MerchantId, long AmountPaisa, long FeePaisa, long TaxPaisa, long NetPaisa);

internal static class MerchantPaymentQueries
{
    /// <summary>
    /// Completed payments to <paramref name="merchantId"/> (all merchants when null) that no settlement
    /// includes yet, completed before <paramref name="before"/> (when given), oldest first.
    /// </summary>
    public static async Task<IReadOnlyList<UnsettledPayment>> UnsettledAsync(TopupMerchantDbContext db, Guid? merchantId, DateTimeOffset? before, int limit, CancellationToken cancellationToken)
    {
        var sagaType = MerchantPaymentSaga.SagaName;
        var completed = "Completed";
        var merchant = merchantId?.ToString();
        var rows = await db.Set<SagaInstance>()
            .FromSql($"""
                SELECT s.* FROM saga_instances AS s
                 WHERE s.saga_type = {sagaType} AND s.state = {completed}
                   AND ({merchant}::text IS NULL OR s.data->>'merchantId' = {merchant})
                   AND ({before}::timestamptz IS NULL OR s.completed_at < {before})
                   AND NOT EXISTS (SELECT 1 FROM settled_payments AS p WHERE p.payment_id = s.id)
                 ORDER BY s.completed_at
                """)
            .AsNoTracking()
            .Take(limit)
            .ToListAsync(cancellationToken);
        return
        [
            .. rows.Select(row =>
            {
                var data = SagaReader<TopupMerchantDbContext>.ReadData<MerchantPaymentData>(row);
                return new UnsettledPayment(row.Id, data.MerchantId, data.AmountPaisa, data.FeePaisa, data.TaxPaisa, data.NetPaisa);
            }),
        ];
    }
}

/// <summary>Lists over saga_instances by a field of their data (partial expression indexes in the migrations).</summary>
internal sealed class SagaLists(TopupMerchantDbContext db) : ISagaLists
{
    /// <summary>The fields lists are kept by (each has an index); nothing else reaches the SQL.</summary>
    private static readonly HashSet<string> Fields = ["userId", "merchantId", "payerUserId"];

    public async Task<IReadOnlyList<SagaSnapshot<TData>>> ByFieldAsync<TData>(string sagaType, string field, string value, int limit, CancellationToken cancellationToken)
        where TData : class
    {
        if (!Fields.Contains(field))
        {
            throw new ArgumentOutOfRangeException(nameof(field), field, "Not a listed field.");
        }

#pragma warning disable EF1002 // The field is one of the constants above; the values are parameters.
        var rows = await db.Set<SagaInstance>()
            .FromSqlRaw(
                $"SELECT * FROM saga_instances WHERE saga_type = {{0}} AND data->>'{field}' = {{1}} ORDER BY created_at DESC LIMIT {{2}}",
                sagaType,
                value,
                limit)
            .AsNoTracking()
            .ToListAsync(cancellationToken);
#pragma warning restore EF1002
        return [.. rows.Select(row => SagaReader<TopupMerchantDbContext>.ToSnapshot<TData>(row, []))];
    }
}
