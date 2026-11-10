using WalletPK.Messaging.Serialization;

namespace WalletPK.Messaging.Abstractions;

/// <summary>
/// Sends envelopes to the message broker. Only the outbox relay (WalletPK.Persistence) calls this:
/// application code never publishes directly. Its aggregates raise domain events, which the outbox
/// interceptor saves in the same transaction as the change, so publishing stays transactional.
/// Implemented by KafkaEventPublisher (WalletPK.Messaging.Kafka).
/// </summary>
public interface IEventPublisher
{
    Task PublishAsync(EventEnvelope envelope, CancellationToken cancellationToken = default);
}
