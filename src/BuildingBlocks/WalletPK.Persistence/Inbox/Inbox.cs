using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WalletPK.Messaging.Abstractions;
using WalletPK.Messaging.Dispatching;
using WalletPK.Messaging.Serialization;

namespace WalletPK.Persistence.Inbox;

/// <summary>
/// One event a consumer has already processed (M4.3). Kafka delivers at least once; the row is
/// inserted in the same transaction as the handler's changes, so a redelivered event finds its row
/// and is skipped: each event changes the database exactly once.
/// </summary>
public sealed class InboxMessage
{
    private InboxMessage()
    {
    }

    public Guid EventId { get; private set; }

    /// <summary>The consumer group (one service can have several consumers of the same event).</summary>
    public string Consumer { get; private set; } = null!;

    public string EventType { get; private set; } = null!;

    public DateTimeOffset ProcessedAt { get; private set; }
}

/// <summary>Maps <see cref="InboxMessage"/> to "inbox_messages" (the same shape in every consuming service).</summary>
public sealed class InboxMessageConfiguration : IEntityTypeConfiguration<InboxMessage>
{
    public void Configure(EntityTypeBuilder<InboxMessage> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("inbox_messages");
        builder.HasKey(message => new { message.EventId, message.Consumer });
        builder.Property(message => message.Consumer).HasMaxLength(100);
        builder.Property(message => message.EventType).HasMaxLength(100).IsRequired();
        builder.HasIndex(message => message.ProcessedAt).HasDatabaseName("ix_inbox_messages_processed_at");
    }
}

public static class InboxModelBuilderExtensions
{
    /// <summary>Adds the "inbox_messages" table to a service's model. Call it from OnModelCreating.</summary>
    public static ModelBuilder AddInboxMessages(this ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        return modelBuilder.ApplyConfiguration(new InboxMessageConfiguration());
    }
}

/// <summary>
/// Processes one consumed event exactly once, in one database transaction:
/// <list type="number">
/// <item>Events this service does not know are ignored (services only register what they consume).</item>
/// <item>Insert (event ID, consumer) into the inbox; if it is already there, this is a redelivery: stop.</item>
/// <item>Run the event's handlers. They share this scope's DbContext, so their changes and the inbox
/// row commit together, or (if anything throws) not at all and the consumer retries the event.</item>
/// </list>
/// </summary>
public sealed partial class InboxEnvelopeProcessor<TDbContext>(
    TDbContext db,
    JsonEventSerializer serializer,
    IntegrationEventDispatcher dispatcher,
    TimeProvider timeProvider,
    ILogger<InboxEnvelopeProcessor<TDbContext>> logger) : IEnvelopeProcessor
    where TDbContext : DbContext
{
    public async Task ProcessAsync(EventEnvelope envelope, string consumer, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        if (!serializer.TryUnwrap(envelope, out var integrationEvent))
        {
            return;
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var now = timeProvider.GetUtcNow();
        var inserted = await db.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO inbox_messages (event_id, consumer, event_type, processed_at)
            VALUES ({envelope.EventId}, {consumer}, {envelope.EventType}, {now})
            ON CONFLICT DO NOTHING
            """,
            cancellationToken);

        if (inserted == 0)
        {
            await transaction.CommitAsync(cancellationToken);
            LogDuplicate(logger, consumer, envelope.EventType, envelope.EventId);
            return;
        }

        var context = new EventContext(envelope.EventId, envelope.EventType, envelope.Topic, envelope.CorrelationId, envelope.OccurredAt);
        await dispatcher.DispatchAsync(integrationEvent, context, cancellationToken);

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Consumer {Consumer} already processed {EventType} {EventId}; skipped the redelivery")]
    private static partial void LogDuplicate(ILogger logger, string consumer, string eventType, Guid eventId);
}

/// <summary>Inbox settings, from "Inbox".</summary>
public sealed class InboxOptions
{
    public const string SectionName = "Inbox";

    /// <summary>
    /// Processed rows are deleted after this long. Must exceed the longest Kafka retention (30 days),
    /// or a replayed event could slip past the inbox.
    /// </summary>
    public int RetentionDays { get; set; } = 35;
}

/// <summary>Deletes old inbox rows once an hour.</summary>
internal sealed partial class InboxCleanupService<TDbContext>(
    IServiceScopeFactory scopes,
    InboxOptions options,
    TimeProvider timeProvider,
    ILogger<InboxCleanupService<TDbContext>> logger) : BackgroundService
    where TDbContext : DbContext
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
        do
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var cutoff = timeProvider.GetUtcNow().AddDays(-options.RetentionDays);
                await scope.ServiceProvider.GetRequiredService<TDbContext>().Set<InboxMessage>()
                    .Where(message => message.ProcessedAt < cutoff)
                    .ExecuteDeleteAsync(stoppingToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                LogCleanupFailed(logger, exception);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Inbox cleanup failed; will retry in an hour")]
    private static partial void LogCleanupFailed(ILogger logger, Exception exception);
}
