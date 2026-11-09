using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WalletPK.Ledger.Infrastructure.Audit;

namespace WalletPK.Ledger.Infrastructure.Persistence.Configurations;

/// <summary>Maps <see cref="AuditRecord"/> to "audit_log". The append-only trigger lives in LedgerDatabaseGuards.InstallAudit.</summary>
internal sealed class AuditRecordConfiguration : IEntityTypeConfiguration<AuditRecord>
{
    public void Configure(EntityTypeBuilder<AuditRecord> builder)
    {
        builder.ToTable("audit_log", table =>
        {
            // Sealed means all four chain columns are set; unsealed means none are.
            table.HasCheckConstraint(
                "ck_audit_log_sealed_consistently",
                "(chain_sequence IS NULL AND previous_hash IS NULL AND hash IS NULL AND sealed_at IS NULL) OR " +
                "(chain_sequence IS NOT NULL AND previous_hash IS NOT NULL AND hash IS NOT NULL AND sealed_at IS NOT NULL)");
            table.HasCheckConstraint("ck_audit_log_chain_sequence_positive", "chain_sequence IS NULL OR chain_sequence > 0");
        });

        builder.HasKey(record => record.Id);
        builder.Property(record => record.Id).UseIdentityAlwaysColumn();
        builder.Property(record => record.EventType).HasMaxLength(40).IsRequired();
        builder.Property(record => record.SubjectType).HasMaxLength(20).IsRequired();
        builder.Property(record => record.SubjectId).HasMaxLength(64).IsRequired();
        builder.Property(record => record.Actor).HasMaxLength(64).IsRequired();
        builder.Property(record => record.CorrelationId).HasMaxLength(100);

        // text, not jsonb: jsonb would re-format the JSON, and the hash covers the exact stored text.
        builder.Property(record => record.Payload).HasColumnType("text").IsRequired();

        builder.Property(record => record.PreviousHash).HasMaxLength(64).IsFixedLength();
        builder.Property(record => record.Hash).HasMaxLength(64).IsFixedLength();

        // One row per chain position: two sealers can never fork the chain.
        builder.HasIndex(record => record.ChainSequence)
            .IsUnique()
            .HasFilter("chain_sequence IS NOT NULL")
            .HasDatabaseName("ux_audit_log_chain_sequence");

        // The sealer's work queue.
        builder.HasIndex(record => record.Id, "ix_audit_log_unsealed")
            .HasFilter("hash IS NULL")
            .HasDatabaseName("ix_audit_log_unsealed");

        builder.HasIndex(record => new { record.SubjectType, record.SubjectId })
            .HasDatabaseName("ix_audit_log_subject");
    }
}
