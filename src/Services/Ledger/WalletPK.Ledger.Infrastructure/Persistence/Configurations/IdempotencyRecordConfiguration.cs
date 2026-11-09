using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WalletPK.Ledger.Infrastructure.Idempotency;

namespace WalletPK.Ledger.Infrastructure.Persistence.Configurations;

/// <summary>Maps <see cref="IdempotencyRecord"/> to "idempotency_keys".</summary>
internal sealed class IdempotencyRecordConfiguration : IEntityTypeConfiguration<IdempotencyRecord>
{
    public void Configure(EntityTypeBuilder<IdempotencyRecord> builder)
    {
        builder.ToTable("idempotency_keys", table =>
            table.HasCheckConstraint("ck_idempotency_keys_state", "state IN ('InProgress', 'Completed')"));

        builder.HasKey(record => record.Key);
        builder.Property(record => record.Key).HasMaxLength(300);
        builder.Property(record => record.RequestHash).HasMaxLength(64).IsRequired();
        builder.Property(record => record.State).HasMaxLength(12).IsRequired();
        builder.Property(record => record.ContentType).HasMaxLength(100);
        builder.Property(record => record.Location).HasMaxLength(300);
        builder.HasIndex(record => record.ExpiresAt).HasDatabaseName("ix_idempotency_keys_expires_at");
    }
}
