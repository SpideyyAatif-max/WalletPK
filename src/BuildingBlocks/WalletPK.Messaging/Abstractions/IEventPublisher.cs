using WalletPK.Messaging.Serialization;

namespace WalletPK.Messaging.Abstractions;

/// <summary>
/// Sends envelopes to the message broker. Only the outbox relay calls this; application code
/// uses <see cref="IOutbox"/> so publishing stays transactional.
/// </summary>
/// <remarks>Implemented with Kafka in M4.1.</remarks>
public interface IEventPublisher
{
    Task PublishAsync(EventEnvelope envelope, CancellationToken cancellationToken = default);
}
