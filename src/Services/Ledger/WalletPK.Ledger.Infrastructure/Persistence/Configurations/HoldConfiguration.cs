using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WalletPK.Common.Identifiers;
using WalletPK.Ledger.Domain.Accounts;
using WalletPK.Ledger.Domain.Holds;

namespace WalletPK.Ledger.Infrastructure.Persistence.Configurations;

/// <summary>Maps <see cref="Hold"/> to "holds".</summary>
internal sealed class HoldConfiguration : IEntityTypeConfiguration<Hold>
{
    public void Configure(EntityTypeBuilder<Hold> builder)
    {
        builder.ToTable("holds", table =>
        {
            table.HasCheckConstraint("ck_holds_amount_positive", "amount > 0");
            table.HasCheckConstraint("ck_holds_captured_within_amount", "captured_amount >= 0 AND captured_amount <= amount");
            table.HasCheckConstraint("ck_holds_status", "status IN ('Active', 'Captured', 'Released', 'Expired')");
        });

        builder.HasKey(hold => hold.Id);
        builder.Property(hold => hold.Id)
            .HasConversion(id => id.Value, value => new HoldId(value))
            .ValueGeneratedNever();

        builder.Property(hold => hold.AccountId)
            .HasConversion(id => id.Value, value => new AccountId(value));
        builder.HasOne<Account>()
            .WithMany()
            .HasForeignKey(hold => hold.AccountId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.Property(hold => hold.Amount);
        builder.Property(hold => hold.CapturedAmount);
        builder.Property(hold => hold.Description).HasMaxLength(Hold.DescriptionMaxLength).IsRequired();
        builder.Property(hold => hold.Reference).HasMaxLength(Hold.ReferenceMaxLength);
        builder.Property(hold => hold.Status).HasConversion<string>().HasMaxLength(10);
        builder.Property(hold => hold.CreatedAt);
        builder.Property(hold => hold.ExpiresAt);
        builder.Property(hold => hold.ClosedAt);
        builder.Property(hold => hold.CaptureTransactionId)
            .HasConversion(id => id!.Value.Value, value => new TransactionId(value));
        builder.Property(hold => hold.Version).IsRowVersion();

        // The sweeper's query: active holds by expiry time. Partial, so it stays small.
        builder.HasIndex(hold => hold.ExpiresAt)
            .HasFilter("status = 'Active'")
            .HasDatabaseName("ix_holds_active_expires_at");

        builder.Ignore(hold => hold.DomainEvents);
    }
}
