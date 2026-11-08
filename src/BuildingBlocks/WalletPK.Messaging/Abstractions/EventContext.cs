namespace WalletPK.Messaging.Abstractions;

/// <summary>Metadata about the message a handler is processing.</summary>
/// <param name="EventId">The event's unique ID.</param>
/// <param name="EventType">Stable event name.</param>
/// <param name="Topic">The Kafka topic it came from.</param>
/// <param name="CorrelationId">The user action it belongs to (restored into the logging context).</param>
/// <param name="OccurredAt">When the fact happened.</param>
public sealed record EventContext(
    Guid EventId,
    string EventType,
    string Topic,
    string? CorrelationId,
    DateTimeOffset OccurredAt);
