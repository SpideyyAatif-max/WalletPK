using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WalletPK.Messaging.Events;
using WalletPK.Messaging.Serialization;
using WalletPK.Notifications.Application;
using WalletPK.Persistence.Inbox;
using WalletPK.Persistence.Outbox;

namespace WalletPK.Notifications.Infrastructure.Persistence;

/// <summary>
/// The Notifications database (walletpk_notifications): recipients and their settings, agent owners,
/// notifications, SMS and email deliveries, and the outbox and inbox.
/// </summary>
public sealed class NotificationsDbContext(DbContextOptions<NotificationsDbContext> options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.AddOutboxMessages();
        modelBuilder.AddInboxMessages();
        modelBuilder.ApplyConfiguration(new RecipientConfiguration());
        modelBuilder.ApplyConfiguration(new AgentOwnerConfiguration());
        modelBuilder.ApplyConfiguration(new NotificationConfiguration());
        modelBuilder.ApplyConfiguration(new DeliveryConfiguration());
    }
}

internal sealed class RecipientConfiguration : IEntityTypeConfiguration<Recipient>
{
    public void Configure(EntityTypeBuilder<Recipient> builder)
    {
        builder.ToTable("recipients");
        builder.HasKey(recipient => recipient.UserId);
        builder.Property(recipient => recipient.UserId).ValueGeneratedNever();
        builder.Property(recipient => recipient.PhoneNumber).HasMaxLength(16);
        builder.Property(recipient => recipient.Email).HasMaxLength(254);
        builder.Property(recipient => recipient.Language).HasMaxLength(2).IsRequired();
        builder.HasIndex(recipient => recipient.PhoneNumber);
    }
}

internal sealed class AgentOwnerConfiguration : IEntityTypeConfiguration<AgentOwner>
{
    public void Configure(EntityTypeBuilder<AgentOwner> builder)
    {
        builder.ToTable("agent_owners");
        builder.HasKey(owner => owner.AgentId);
        builder.Property(owner => owner.AgentId).ValueGeneratedNever();
    }
}

internal sealed class NotificationConfiguration : IEntityTypeConfiguration<Notification>
{
    public void Configure(EntityTypeBuilder<Notification> builder)
    {
        builder.ToTable("notifications");
        builder.HasKey(notification => notification.Id);
        builder.Property(notification => notification.Id).ValueGeneratedNever();
        builder.Property(notification => notification.Template).HasMaxLength(40).IsRequired();
        builder.Property(notification => notification.Category).HasMaxLength(20).IsRequired();
        builder.Property(notification => notification.DedupeKey).HasMaxLength(200).IsRequired();
        builder.Property(notification => notification.Parameters)
            .HasColumnType("jsonb")
            .HasConversion(
                parameters => JsonSerializer.Serialize(parameters, JsonSerializerOptions.Web),
                json => JsonSerializer.Deserialize<Dictionary<string, string>>(json, JsonSerializerOptions.Web) ?? new Dictionary<string, string>(),
                new ValueComparer<Dictionary<string, string>>(
                    (left, right) => left!.Count == right!.Count && !left.Except(right).Any(),
                    parameters => parameters.Aggregate(0, (hash, pair) => HashCode.Combine(hash, pair.Key, pair.Value)),
                    parameters => new Dictionary<string, string>(parameters)));

        // M11.5: one notification per user and subject, however often its event arrives.
        builder.HasIndex(notification => new { notification.UserId, notification.DedupeKey }).IsUnique();
        builder.HasIndex(notification => new { notification.UserId, notification.CreatedAt, notification.Id });
        builder.HasIndex(notification => notification.UserId).HasFilter("read_at IS NULL").HasDatabaseName("ix_notifications_unread");
    }
}

internal sealed class DeliveryConfiguration : IEntityTypeConfiguration<Delivery>
{
    public void Configure(EntityTypeBuilder<Delivery> builder)
    {
        builder.ToTable("deliveries");
        builder.HasKey(delivery => delivery.Id);
        builder.Property(delivery => delivery.Id).ValueGeneratedNever();
        builder.Property(delivery => delivery.Channel).HasMaxLength(10).IsRequired();
        builder.Property(delivery => delivery.Template).HasMaxLength(40).IsRequired();
        builder.Property(delivery => delivery.Destination).HasMaxLength(254).IsRequired();
        builder.Property(delivery => delivery.Language).HasMaxLength(2).IsRequired();
        builder.Property(delivery => delivery.Subject).HasMaxLength(250);
        builder.Property(delivery => delivery.Status).HasMaxLength(10).IsRequired();
        builder.Property(delivery => delivery.ProviderReference).HasMaxLength(64);
        builder.Property(delivery => delivery.LastError).HasMaxLength(500);
        builder.HasOne<Notification>().WithMany().HasForeignKey(delivery => delivery.NotificationId);

        // M11.5: at most one SMS and one email per notification.
        builder.HasIndex(delivery => new { delivery.NotificationId, delivery.Channel }).IsUnique();
        builder.HasIndex(delivery => delivery.NextAttemptAt).HasFilter("status = 'Pending'").HasDatabaseName("ix_deliveries_due");
        builder.HasIndex(delivery => new { delivery.UserId, delivery.CreatedAt });
    }
}

/// <summary>The store behind the use cases.</summary>
internal sealed class NotificationStore(NotificationsDbContext db, JsonEventSerializer serializer, TimeProvider timeProvider) : INotificationStore
{
    public async Task<Recipient?> RecipientAsync(Guid userId, CancellationToken cancellationToken) =>
        await db.Set<Recipient>().FindAsync([userId], cancellationToken);

    public Task<Recipient?> RecipientByPhoneAsync(string phoneNumber, CancellationToken cancellationToken) =>
        db.Set<Recipient>().Where(recipient => recipient.PhoneNumber == phoneNumber).OrderByDescending(recipient => recipient.UpdatedAt).FirstOrDefaultAsync(cancellationToken);

    public async Task<Guid?> AgentOwnerAsync(Guid agentId, CancellationToken cancellationToken) =>
        (await db.Set<AgentOwner>().FindAsync([agentId], cancellationToken))?.UserId;

    public async Task<bool> ExistsAsync(Guid userId, string dedupeKey, CancellationToken cancellationToken) =>
        db.Set<Notification>().Local.Any(notification => notification.UserId == userId && notification.DedupeKey == dedupeKey)
        || await db.Set<Notification>().AnyAsync(notification => notification.UserId == userId && notification.DedupeKey == dedupeKey, cancellationToken);

    public async Task<IReadOnlyList<Notification>> PageAsync(Guid userId, (DateTimeOffset At, Guid Id)? before, int limit, CancellationToken cancellationToken)
    {
        // Row comparison keeps the (created_at, id) order exact when two notifications share a timestamp.
        var query = before is { } cursor
            ? db.Set<Notification>().FromSql(
                $"SELECT * FROM notifications WHERE user_id = {userId} AND (created_at, id) < ({cursor.At}, {cursor.Id}) ORDER BY created_at DESC, id DESC LIMIT {limit}")
            : db.Set<Notification>().FromSql(
                $"SELECT * FROM notifications WHERE user_id = {userId} ORDER BY created_at DESC, id DESC LIMIT {limit}");
        return await query.AsNoTracking().ToListAsync(cancellationToken);
    }

    public Task<int> UnreadCountAsync(Guid userId, CancellationToken cancellationToken) =>
        db.Set<Notification>().CountAsync(notification => notification.UserId == userId && notification.ReadAt == null, cancellationToken);

    public Task<Notification?> NotificationAsync(Guid userId, Guid id, CancellationToken cancellationToken) =>
        db.Set<Notification>().SingleOrDefaultAsync(notification => notification.Id == id && notification.UserId == userId, cancellationToken);

    public Task<int> MarkAllReadAsync(Guid userId, DateTimeOffset now, CancellationToken cancellationToken) =>
        db.Set<Notification>()
            .Where(notification => notification.UserId == userId && notification.ReadAt == null)
            .ExecuteUpdateAsync(set => set.SetProperty(notification => notification.ReadAt, now), cancellationToken);

    public async Task<IReadOnlyList<Delivery>> DeliveriesForAsync(Guid userId, int limit, CancellationToken cancellationToken) =>
        await db.Set<Delivery>().AsNoTracking()
            .Where(delivery => delivery.UserId == userId)
            .OrderByDescending(delivery => delivery.CreatedAt)
            .Take(limit)
            .ToListAsync(cancellationToken);

    public async Task<int> ForEachDueAsync(int limit, DateTimeOffset now, Func<Delivery, Task> send, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(send);
        var done = 0;
        while (done < limit)
        {
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            var due = await db.Set<Delivery>()
                .FromSql($"SELECT * FROM deliveries WHERE status = 'Pending' AND next_attempt_at <= {now} ORDER BY next_attempt_at LIMIT 1 FOR UPDATE SKIP LOCKED")
                .ToListAsync(cancellationToken);
            if (due is not [var delivery])
            {
                break;
            }

            await send(delivery);
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            db.ChangeTracker.Clear();
            done++;
        }

        return done;
    }

    public void Add(object entity) => db.Add(entity);

    public void Publish(IntegrationEvent message) =>
        db.AddToOutbox(serializer, message, timeProvider.GetUtcNow(), correlationId: null);

    public Task SaveChangesAsync(CancellationToken cancellationToken) => db.SaveChangesAsync(cancellationToken);
}
