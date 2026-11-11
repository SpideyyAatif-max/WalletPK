using System.Text.Json;
using Confluent.Kafka;
using Microsoft.Extensions.Logging.Abstractions;
using WalletPK.Messaging.Events;
using WalletPK.Messaging.Kafka;
using WalletPK.Messaging.Serialization;

namespace WalletPK.Ledger.IntegrationTests.Infrastructure;

/// <summary>A message read back from the test broker.</summary>
public sealed record ReadMessage(string Topic, int Partition, string Key, string EventType, Guid EventId, JsonElement Payload, EventEnvelope Envelope);

/// <summary>Produces to and reads from the test Kafka broker directly, around the service under test.</summary>
public static class KafkaTestKit
{
    /// <summary>Publishes an event the way another service's outbox relay would.</summary>
    public static async Task PublishAsync(this LedgerApiFactory factory, IntegrationEvent integrationEvent, IntegrationEventRegistry registry, string? correlationId = null)
    {
        ArgumentNullException.ThrowIfNull(factory);
        var envelope = new JsonEventSerializer(registry).Wrap(integrationEvent, correlationId);
        await factory.PublishEnvelopeAsync(envelope);
    }

    public static async Task PublishEnvelopeAsync(this LedgerApiFactory factory, EventEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(factory);
        using var publisher = new KafkaEventPublisher(
            new KafkaOptions { BootstrapServers = factory.KafkaBootstrapServers, ClientId = "ledger-tests" },
            NullLogger<KafkaEventPublisher>.Instance);
        await publisher.PublishAsync(envelope, LedgerTestKit.Cancel);
    }

    /// <summary>Sends a raw message, for example one with no headers at all.</summary>
    public static async Task ProduceRawAsync(this LedgerApiFactory factory, string topic, string key, string value)
    {
        ArgumentNullException.ThrowIfNull(factory);
        using var producer = new ProducerBuilder<string, string>(new ProducerConfig { BootstrapServers = factory.KafkaBootstrapServers }).Build();
        await producer.ProduceAsync(topic, new Message<string, string> { Key = key, Value = value }, LedgerTestKit.Cancel);
    }

    /// <summary>
    /// Reads <paramref name="topic"/> from the beginning (a fresh consumer group) until
    /// <paramref name="done"/> is satisfied by what was read, or fails after <paramref name="timeout"/>.
    /// </summary>
    public static List<ReadMessage> ReadUntil(this LedgerApiFactory factory, string topic, Func<IReadOnlyList<ReadMessage>, bool> done, TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(done);

        using var consumer = new ConsumerBuilder<string, string>(new ConsumerConfig
        {
            BootstrapServers = factory.KafkaBootstrapServers,
            GroupId = $"test-reader-{Guid.NewGuid():N}",
            AutoOffsetReset = AutoOffsetReset.Earliest,
            EnableAutoCommit = false,
        }).Build();
        consumer.Subscribe(topic);

        var read = new List<ReadMessage>();
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(30));
        try
        {
            while (DateTime.UtcNow < deadline)
            {
                var result = consumer.Consume(TimeSpan.FromMilliseconds(250));
                if (result?.Message is null)
                {
                    if (done(read))
                    {
                        return read;
                    }

                    continue;
                }

                if (KafkaMessages.TryFromMessage(result.Topic, result.Message, out var envelope, out _))
                {
                    read.Add(new ReadMessage(
                        result.Topic,
                        result.Partition.Value,
                        result.Message.Key,
                        envelope!.EventType,
                        envelope.EventId,
                        JsonDocument.Parse(envelope.Payload).RootElement.Clone(),
                        envelope));
                }

                if (done(read))
                {
                    return read;
                }
            }
        }
        finally
        {
            consumer.Close();
        }

        throw new TimeoutException($"Did not find the expected messages on {topic}; read {read.Count}.");
    }

    /// <summary>Retries <paramref name="check"/> until it returns true or the time runs out.</summary>
    public static async Task EventuallyAsync(Func<Task<bool>> check, TimeSpan? timeout = null, string? because = null)
    {
        ArgumentNullException.ThrowIfNull(check);
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(30));
        while (DateTime.UtcNow < deadline)
        {
            if (await check())
            {
                return;
            }

            await Task.Delay(100, LedgerTestKit.Cancel);
        }

        throw new TimeoutException(because ?? "The condition did not become true in time.");
    }
}
