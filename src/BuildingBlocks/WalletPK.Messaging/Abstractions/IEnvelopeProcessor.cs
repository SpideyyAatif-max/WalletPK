using WalletPK.Messaging.Serialization;

namespace WalletPK.Messaging.Abstractions;

/// <summary>
/// Handles one message taken from Kafka. The consumer host calls it once per message, in a fresh DI
/// scope, and moves past the message only when it returns. If it throws, the host waits and calls it
/// again with the same message (the partition does not advance), so it must be idempotent: the
/// inbox processor in WalletPK.Persistence makes it so.
/// </summary>
public interface IEnvelopeProcessor
{
    /// <param name="envelope">The message, with its metadata restored from the Kafka headers.</param>
    /// <param name="consumer">The consumer group name, for example "ledger" (one inbox per consumer).</param>
    /// <param name="cancellationToken">Cancelled when the service stops.</param>
    Task ProcessAsync(EventEnvelope envelope, string consumer, CancellationToken cancellationToken);
}
