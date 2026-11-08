using WalletPK.Messaging.Events;

namespace WalletPK.Messaging.Abstractions;

/// <summary>
/// Transactional Outbox: saves an event in the <b>same database transaction</b> as the business
/// change that produced it. If the transaction commits, the event is guaranteed to be published
/// (by a background relay, at least once); if it rolls back, the event never existed.
/// This removes the "saved to the database but crashed before telling Kafka" failure.
/// </summary>
/// <remarks>Implemented with EF Core and a relay to Kafka in M4.1.</remarks>
public interface IOutbox
{
    /// <summary>Adds the event to the current unit of work; it is written when the transaction commits.</summary>
    Task EnqueueAsync(IntegrationEvent integrationEvent, CancellationToken cancellationToken = default);
}
