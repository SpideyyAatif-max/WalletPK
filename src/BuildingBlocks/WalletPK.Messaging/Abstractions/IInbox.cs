namespace WalletPK.Messaging.Abstractions;

/// <summary>
/// Inbox: remembers which events a consumer has already processed. Kafka delivers at least once,
/// so the same event can arrive twice; recording its ID in the same transaction as the handler's
/// changes makes processing effectively exactly once.
/// </summary>
/// <remarks>Implemented with EF Core in M4.3.</remarks>
public interface IInbox
{
    /// <summary>
    /// Records that <paramref name="consumer"/> processed <paramref name="eventId"/>.
    /// Returns false if it was already recorded, in which case the handler must skip the event.
    /// </summary>
    Task<bool> TryMarkProcessedAsync(Guid eventId, string consumer, CancellationToken cancellationToken = default);
}
