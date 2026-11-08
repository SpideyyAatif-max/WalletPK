namespace WalletPK.Messaging.Serialization;

/// <summary>
/// What actually travels through the outbox table and Kafka: routing metadata plus the event as JSON.
/// The outbox stores one envelope per row; the Kafka producer turns the metadata into message
/// headers and the key, and the payload into the message value.
/// </summary>
/// <param name="EventId">The event's unique ID (used for de-duplication).</param>
/// <param name="EventType">Stable event name, for example "ledger.money_transferred".</param>
/// <param name="EventVersion">Schema version of the payload.</param>
/// <param name="Topic">Destination Kafka topic.</param>
/// <param name="PartitionKey">Kafka message key (keeps one wallet's events in order).</param>
/// <param name="OccurredAt">When the fact happened (UTC).</param>
/// <param name="CorrelationId">The user action this event belongs to.</param>
/// <param name="CausationId">The ID of the event or command that caused this one, if any.</param>
/// <param name="Payload">The event serialised as JSON.</param>
public sealed record EventEnvelope(
    Guid EventId,
    string EventType,
    int EventVersion,
    string Topic,
    string PartitionKey,
    DateTimeOffset OccurredAt,
    string? CorrelationId,
    string? CausationId,
    string Payload);
