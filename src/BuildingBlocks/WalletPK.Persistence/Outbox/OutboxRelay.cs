using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WalletPK.Messaging.Abstractions;

namespace WalletPK.Persistence.Outbox;

/// <summary>Outbox relay settings, from "Outbox:Relay".</summary>
public sealed class OutboxRelayOptions
{
    public const string SectionName = "Outbox:Relay";

    /// <summary>Run the background relay (tests may switch it off and publish on demand).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Rows taken (and locked) per round.</summary>
    public int BatchSize { get; set; } = 200;

    /// <summary>Wait between rounds when there was nothing (or not a full batch) to send.</summary>
    public int PollIntervalMilliseconds { get; set; } = 250;

    /// <summary>Published rows are deleted after this long (they are only kept for debugging).</summary>
    public int RetentionHours { get; set; } = 72;

    /// <summary>Longest wait before retrying a row that failed to publish.</summary>
    public int MaxBackoffSeconds { get; set; } = 300;

    public bool IsValid() =>
        BatchSize is >= 1 and <= 5_000 && PollIntervalMilliseconds >= 10 && RetentionHours >= 1 && MaxBackoffSeconds >= 1;
}

/// <param name="Published">Rows delivered to Kafka this round.</param>
/// <param name="Failed">Rows that could not be delivered (retried later, with back-off).</param>
public sealed record OutboxRelayResult(int Published, int Failed)
{
    public int Total => Published + Failed;
}

/// <summary>
/// Publishes outbox rows to Kafka (M4.1). Each round, in one transaction:
/// <list type="number">
/// <item>Take the oldest unpublished rows with <c>FOR UPDATE SKIP LOCKED</c>. Several relays (one per
/// service instance) can run at once: each takes rows nobody else has locked, so no row is sent twice
/// and nobody waits for anybody.</item>
/// <item>Hand them all to the producer at once (it batches them), then wait for every acknowledgement.</item>
/// <item>Mark delivered rows published; for the rest, count the attempt and set a back-off time.</item>
/// </list>
/// If the process dies after Kafka acknowledged but before the commit, those rows are sent again on
/// the next round: delivery is at least once, and consumers de-duplicate on the event ID (the inbox).
/// Order across a batch is the outbox order; consumers that need strict per-wallet order also check
/// the per-wallet sequence carried in the events.
/// </summary>
public sealed partial class OutboxRelay<TDbContext>(
    TDbContext db,
    IEventPublisher publisher,
    TimeProvider timeProvider,
    OutboxRelayOptions options,
    ILogger<OutboxRelay<TDbContext>> logger)
    where TDbContext : DbContext
{
    public async Task<OutboxRelayResult> PublishBatchAsync(CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var batchSize = options.BatchSize;

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var batch = await db.Set<OutboxMessage>()
            .FromSql($"""
                SELECT *
                  FROM outbox_messages
                 WHERE published_at IS NULL
                   AND (next_attempt_at IS NULL OR next_attempt_at <= {now})
                 ORDER BY created_at, id
                 LIMIT {batchSize}
                   FOR UPDATE SKIP LOCKED
                """)
            .ToListAsync(cancellationToken);

        if (batch.Count == 0)
        {
            await transaction.CommitAsync(cancellationToken);
            return new OutboxRelayResult(0, 0);
        }

        // Start every send before awaiting any, so the producer can put them in one request.
        var deliveries = batch.Select(message => (Message: message, Delivery: publisher.PublishAsync(message.ToEnvelope(), cancellationToken))).ToList();

        int published = 0, failed = 0;
        foreach (var (message, delivery) in deliveries)
        {
            try
            {
                await delivery;
                message.MarkPublished(timeProvider.GetUtcNow());
                published++;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                var backoff = Backoff(message.PublishAttempts + 1);
                message.RecordFailure(exception.Message, now + backoff);
                failed++;
                LogPublishFailed(logger, message.EventType, message.Id, message.PublishAttempts, backoff.TotalSeconds, exception.Message);
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        if (published > 0)
        {
            LogPublished(logger, published, typeof(TDbContext).Name);
        }

        return new OutboxRelayResult(published, failed);
    }

    /// <summary>Deletes published rows older than the retention period; returns how many.</summary>
    public Task<int> DeleteOldPublishedAsync(CancellationToken cancellationToken)
    {
        var cutoff = timeProvider.GetUtcNow().AddHours(-options.RetentionHours);
        return db.Set<OutboxMessage>()
            .Where(message => message.PublishedAt != null && message.PublishedAt < cutoff)
            .ExecuteDeleteAsync(cancellationToken);
    }

    /// <summary>1 s, 2 s, 4 s ... up to the configured maximum.</summary>
    internal TimeSpan Backoff(int attempt) =>
        TimeSpan.FromSeconds(Math.Min(options.MaxBackoffSeconds, Math.Pow(2, Math.Clamp(attempt - 1, 0, 20))));

    [LoggerMessage(Level = LogLevel.Debug, Message = "Outbox relay published {Count} event(s) from {Context}")]
    private static partial void LogPublished(ILogger logger, int count, string context);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Outbox relay could not publish {EventType} {EventId} (attempt {Attempt}); retrying in {BackoffSeconds} s: {Error}")]
    private static partial void LogPublishFailed(ILogger logger, string eventType, Guid eventId, int attempt, double backoffSeconds, string error);
}

/// <summary>Runs the <see cref="OutboxRelay{TDbContext}"/> continuously, and clears old published rows every ten minutes.</summary>
internal sealed partial class OutboxRelayService<TDbContext>(
    IServiceScopeFactory scopes,
    OutboxRelayOptions options,
    TimeProvider timeProvider,
    ILogger<OutboxRelayService<TDbContext>> logger) : BackgroundService
    where TDbContext : DbContext
{
    private static readonly TimeSpan _cleanupInterval = TimeSpan.FromMinutes(10);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Enabled)
        {
            return;
        }

        var nextCleanup = timeProvider.GetUtcNow();
        while (!stoppingToken.IsCancellationRequested)
        {
            var fullBatch = false;
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var relay = scope.ServiceProvider.GetRequiredService<OutboxRelay<TDbContext>>();

                var result = await relay.PublishBatchAsync(stoppingToken);
                fullBatch = result.Total == options.BatchSize && result.Failed == 0;

                if (timeProvider.GetUtcNow() >= nextCleanup)
                {
                    await relay.DeleteOldPublishedAsync(stoppingToken);
                    nextCleanup = timeProvider.GetUtcNow() + _cleanupInterval;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                // Usually the database is unreachable; nothing is lost, the rows wait in the outbox.
                LogRoundFailed(logger, typeof(TDbContext).Name, exception);
                await Delay(TimeSpan.FromSeconds(5), stoppingToken);
                continue;
            }

            if (!fullBatch)
            {
                await Delay(TimeSpan.FromMilliseconds(options.PollIntervalMilliseconds), stoppingToken);
            }
        }
    }

    private static async Task Delay(TimeSpan delay, CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(delay, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Outbox relay round failed for {Context}; retrying in 5 s")]
    private static partial void LogRoundFailed(ILogger logger, string context, Exception exception);
}
