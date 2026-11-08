namespace WalletPK.Messaging.Events;

/// <summary>
/// A fact one service publishes for other services, for example "MoneyTransferred" or "BillPaid".
/// <para>
/// Unlike a domain event (internal to one service), an integration event is a public contract:
/// once published, its fields may only be added, never renamed or removed. A breaking change is a
/// new version (see <see cref="IntegrationEventAttribute.Version"/>).
/// </para>
/// Every concrete event must carry an <see cref="IntegrationEventAttribute"/>.
/// </summary>
public abstract record IntegrationEvent
{
    /// <summary>Unique, time-ordered ID. Consumers use it to process each event exactly once.</summary>
    public Guid EventId { get; init; } = Guid.CreateVersion7();

    /// <summary>When the fact happened (UTC).</summary>
    public DateTimeOffset OccurredAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// The Kafka message key. Events with the same key go to the same partition and are consumed
    /// in order, so events of one wallet must share a key (override it to return the wallet ID).
    /// A method rather than a property, so it is never serialised into the payload.
    /// </summary>
    public virtual string GetPartitionKey() => EventId.ToString();
}
