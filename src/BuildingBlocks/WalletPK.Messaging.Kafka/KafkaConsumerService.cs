using System.Diagnostics.Metrics;
using Confluent.Kafka;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WalletPK.Common.Correlation;
using WalletPK.Messaging.Abstractions;

namespace WalletPK.Messaging.Kafka;

/// <summary>One consumer group of a service: its group ID and the topics it reads.</summary>
/// <param name="GroupId">The consumer group, for example "ledger". Also the inbox's consumer name.</param>
/// <param name="Topics">Topics to subscribe to.</param>
public sealed record KafkaConsumerDefinition(string GroupId, IReadOnlyList<string> Topics);

/// <summary>
/// Reads one consumer group's topics and hands every message to the scoped <see cref="IEnvelopeProcessor"/>.
/// <list type="bullet">
/// <item><b>At least once:</b> a message's offset is stored only after it was processed, so a crash
/// re-delivers it; the inbox turns that into exactly-once processing.</item>
/// <item><b>In order:</b> messages are processed one at a time; a failing message is retried with
/// growing back-off (up to 30 s) and the partition does not move past it, so one wallet's events are
/// never applied out of order. Dead-lettering poison messages arrives with M7.5.</item>
/// <item><b>Traceable:</b> the message's correlation ID is restored for its processing, so its logs and
/// any events it causes carry the ID of the user action that started it all.</item>
/// </list>
/// </summary>
public sealed partial class KafkaConsumerService(
    KafkaConsumerDefinition definition,
    KafkaOptions options,
    IServiceScopeFactory scopes,
    IMeterFactory meterFactory,
    ILogger<KafkaConsumerService> logger) : BackgroundService
{
    private static readonly TimeSpan _maxBackoff = TimeSpan.FromSeconds(30);

    private readonly Counter<long> _processed = meterFactory.Create("WalletPK.Messaging").CreateCounter<long>(
        "walletpk.messaging.consumed", description: "Kafka messages processed, tagged by consumer, topic and result");

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        // Consume() blocks, so the loop gets its own thread instead of holding up application start.
        Task.Factory.StartNew(() => ConsumeLoopAsync(stoppingToken), stoppingToken, TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();

    private async Task ConsumeLoopAsync(CancellationToken stoppingToken)
    {
        var config = new ConsumerConfig
        {
            BootstrapServers = options.BootstrapServers,
            ClientId = options.ClientId,
            GroupId = definition.GroupId,
            AutoOffsetReset = AutoOffsetReset.Earliest,
            EnableAutoCommit = true,        // commits the offsets we store, every few seconds
            EnableAutoOffsetStore = false,  // ...and we store an offset only after processing succeeded
            EnablePartitionEof = false,
            AllowAutoCreateTopics = false,
        };

        using var consumer = new ConsumerBuilder<string, string>(config)
            .SetErrorHandler((_, error) => LogConsumerError(logger, definition.GroupId, error.Code, error.Reason))
            .Build();
        consumer.Subscribe(definition.Topics);
        LogStarted(logger, definition.GroupId, string.Join(", ", definition.Topics));

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                ConsumeResult<string, string>? result;
                try
                {
                    result = consumer.Consume(stoppingToken);
                }
                catch (ConsumeException exception) when (!exception.Error.IsFatal)
                {
                    // For example a topic that does not exist yet; the consumer picks it up once created.
                    LogConsumerError(logger, definition.GroupId, exception.Error.Code, exception.Error.Reason);
                    await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
                    continue;
                }

                if (result?.Message is null)
                {
                    continue;
                }

                await ProcessWithRetryAsync(result, stoppingToken);
                consumer.StoreOffset(result);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down.
        }
        finally
        {
            // Commits the stored offsets and leaves the group cleanly, so partitions move to other instances at once.
            consumer.Close();
        }
    }

    private async Task ProcessWithRetryAsync(ConsumeResult<string, string> result, CancellationToken stoppingToken)
    {
        if (!KafkaMessages.TryFromMessage(result.Topic, result.Message, out var envelope, out var problem))
        {
            LogMalformed(logger, definition.GroupId, result.TopicPartitionOffset.ToString(), problem!);
            Count(result.Topic, "malformed");
            return;
        }

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                CorrelationContext.Current = envelope!.CorrelationId;
                using (logger.BeginScope(new Dictionary<string, object?>
                {
                    ["EventId"] = envelope.EventId,
                    ["EventType"] = envelope.EventType,
                    ["CorrelationId"] = envelope.CorrelationId,
                }))
                {
                    await using var scope = scopes.CreateAsyncScope();
                    await scope.ServiceProvider.GetRequiredService<IEnvelopeProcessor>()
                        .ProcessAsync(envelope, definition.GroupId, stoppingToken);
                }

                Count(result.Topic, "processed");
                return;
            }
            catch (Exception exception) when (exception is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
            {
                var delay = TimeSpan.FromMilliseconds(Math.Min(_maxBackoff.TotalMilliseconds, 200 * Math.Pow(2, Math.Min(attempt - 1, 10))));
                LogProcessingFailed(logger, definition.GroupId, envelope!.EventType, envelope.EventId, attempt, delay.TotalSeconds, exception);
                Count(result.Topic, "failed");
                await Task.Delay(delay, stoppingToken);
            }
            finally
            {
                CorrelationContext.Current = null;
            }
        }
    }

    private void Count(string topic, string outcome) =>
        _processed.Add(1,
            new KeyValuePair<string, object?>("consumer", definition.GroupId),
            new KeyValuePair<string, object?>("topic", topic),
            new KeyValuePair<string, object?>("result", outcome));

    [LoggerMessage(Level = LogLevel.Information, Message = "Kafka consumer {GroupId} subscribed to {Topics}")]
    private static partial void LogStarted(ILogger logger, string groupId, string topics);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Kafka consumer {GroupId}: {Code} {Reason}")]
    private static partial void LogConsumerError(ILogger logger, string groupId, ErrorCode code, string reason);

    [LoggerMessage(Level = LogLevel.Error, Message = "Kafka consumer {GroupId} skipped a malformed message at {Position}: {Problem}")]
    private static partial void LogMalformed(ILogger logger, string groupId, string position, string problem);

    [LoggerMessage(Level = LogLevel.Error, Message = "Kafka consumer {GroupId} failed to process {EventType} {EventId} (attempt {Attempt}); retrying in {DelaySeconds} s")]
    private static partial void LogProcessingFailed(ILogger logger, string groupId, string eventType, Guid eventId, int attempt, double delaySeconds, Exception exception);
}
