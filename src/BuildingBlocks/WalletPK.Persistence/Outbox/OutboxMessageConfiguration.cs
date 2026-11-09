using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace WalletPK.Persistence.Outbox;

/// <summary>Maps <see cref="OutboxMessage"/> to "outbox_messages" (the same shape in every service's database).</summary>
public sealed class OutboxMessageConfiguration : IEntityTypeConfiguration<OutboxMessage>
{
    public void Configure(EntityTypeBuilder<OutboxMessage> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("outbox_messages");

        builder.HasKey(message => message.Id);
        builder.Property(message => message.Id).ValueGeneratedNever();

        builder.Property(message => message.EventType).HasMaxLength(100).IsRequired();
        builder.Property(message => message.Topic).HasMaxLength(100).IsRequired();
        builder.Property(message => message.PartitionKey).HasMaxLength(100).IsRequired();
        builder.Property(message => message.Payload).HasColumnType("jsonb").IsRequired();
        builder.Property(message => message.CorrelationId).HasMaxLength(64);
        builder.Property(message => message.CausationId).HasMaxLength(64);
        builder.Property(message => message.LastError).HasMaxLength(2000);

        // The relay reads "oldest unpublished first"; a partial index keeps that fast however
        // many published rows pile up.
        builder.HasIndex(message => message.CreatedAt)
            .HasDatabaseName("ix_outbox_messages_unpublished")
            .HasFilter("published_at IS NULL");
    }
}

public static class OutboxModelBuilderExtensions
{
    /// <summary>Adds the "outbox_messages" table to a service's model. Call it from OnModelCreating.</summary>
    public static ModelBuilder AddOutboxMessages(this ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        return modelBuilder.ApplyConfiguration(new OutboxMessageConfiguration());
    }
}
