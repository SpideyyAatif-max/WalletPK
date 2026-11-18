using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WalletPK.Ledger.Application.Wallets;

namespace WalletPK.Ledger.Infrastructure.Persistence;

/// <summary>Row of "transfer_rejections": a refused TransferFunds command (M6.1).</summary>
internal sealed class TransferRejectionRecord
{
    public Guid TransactionId { get; set; }

    public string Code { get; set; } = "";

    public string Reason { get; set; } = "";

    public DateTimeOffset RejectedAt { get; set; }
}

internal sealed class TransferRejectionConfiguration : IEntityTypeConfiguration<TransferRejectionRecord>
{
    public void Configure(EntityTypeBuilder<TransferRejectionRecord> builder)
    {
        builder.ToTable("transfer_rejections");
        builder.HasKey(record => record.TransactionId);
        builder.Property(record => record.TransactionId).ValueGeneratedNever();
        builder.Property(record => record.Code).HasMaxLength(100).IsRequired();
        builder.Property(record => record.Reason).HasMaxLength(500).IsRequired();
    }
}

internal sealed class TransferRejections(LedgerDbContext db) : ITransferRejections
{
    public async Task<TransferRejection?> FindAsync(Guid transactionId, CancellationToken cancellationToken) =>
        await db.Set<TransferRejectionRecord>().AsNoTracking().SingleOrDefaultAsync(record => record.TransactionId == transactionId, cancellationToken) is { } record
            ? new TransferRejection(record.TransactionId, record.Code, record.Reason, record.RejectedAt)
            : null;

    public void Add(TransferRejection rejection)
    {
        ArgumentNullException.ThrowIfNull(rejection);
        db.Set<TransferRejectionRecord>().Add(new TransferRejectionRecord
        {
            TransactionId = rejection.TransactionId,
            Code = rejection.Code.Length <= 100 ? rejection.Code : rejection.Code[..100],
            Reason = rejection.Reason.Length <= 500 ? rejection.Reason : rejection.Reason[..500],
            RejectedAt = rejection.RejectedAt,
        });
    }
}
