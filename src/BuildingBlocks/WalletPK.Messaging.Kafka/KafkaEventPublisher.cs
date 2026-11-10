using Confluent.Kafka;
using Microsoft.Extensions.Logging;
using WalletPK.Messaging.Abstractions;
using WalletPK.Messaging.Serialization;

namespace WalletPK.Messaging.Kafka;

/// <summary>
/// Sends envelopes to Kafka (used only by the outbox relay). One producer per process, configured for
/// safety over raw speed:
/// <list type="bullet">
/// <item>acks=all: a message counts as sent only when every in-sync replica has it.</item>
/// <item>Idempotence on: the broker drops duplicates from producer retries and keeps per-partition order.</item>
/// <item>lz4 compression and a 5 ms linger: messages produced together travel in one batch.</item>
/// </list>
/// </summary>
public sealed partial class KafkaEventPublisher : IEventPublisher, IDisposable
{
    private readonly IProducer<string, string> _producer;
    private int _disposed;

    public KafkaEventPublisher(KafkaOptions options, ILogger<KafkaEventPublisher> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        var config = new ProducerConfig
        {
            BootstrapServers = options.BootstrapServers,
            ClientId = options.ClientId,
            Acks = Acks.All,
            EnableIdempotence = true,
            LingerMs = 5,
            CompressionType = CompressionType.Lz4,
            MessageTimeoutMs = options.MessageTimeoutMilliseconds,
        };

        _producer = new ProducerBuilder<string, string>(config)
            .SetErrorHandler((_, error) => LogProducerError(logger, error.Code, error.Reason, error.IsFatal))
            .Build();
    }

    /// <summary>Completes when the broker has acknowledged the message; throws if it could not be delivered.</summary>
    public async Task PublishAsync(EventEnvelope envelope, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        await _producer.ProduceAsync(envelope.Topic, KafkaMessages.ToMessage(envelope), cancellationToken);
    }

    /// <summary>Safe to call more than once (the container may hold this instance under two service types).</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        // Give in-flight messages a moment to be delivered before the process exits.
        _producer.Flush(TimeSpan.FromSeconds(5));
        _producer.Dispose();
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Kafka producer error {Code}: {Reason} (fatal: {IsFatal})")]
    private static partial void LogProducerError(ILogger logger, ErrorCode code, string reason, bool isFatal);
}
