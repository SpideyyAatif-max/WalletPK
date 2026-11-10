using WalletPK.Messaging.Serialization;

namespace WalletPK.Persistence.Outbox;

/// <summary>
/// One integration event waiting to be published, stored in the same database and the same
/// transaction as the change that produced it (Transactional Outbox). The <see cref="OutboxRelay{TDbContext}"/>
/// sends these rows to Kafka and fills <see cref="PublishedAt"/>.
/// </summary>
public sealed class OutboxMessage
{
    private OutboxMessage()
    {
    }

    /// <summary>The event ID (consumers de-duplicate on it).</summary>
    public Guid Id { get; private set; }

    public string EventType { get; private set; } = null!;

    public int EventVersion { get; private set; }

    public string Topic { get; private set; } = null!;

    public string PartitionKey { get; private set; } = null!;

    /// <summary>The event as JSON (stored as jsonb, so it can be queried while debugging).</summary>
    public string Payload { get; private set; } = null!;

    public string? CorrelationId { get; private set; }

    public string? CausationId { get; private set; }

    public DateTimeOffset OccurredAt { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>Null until the relay has delivered the event to Kafka.</summary>
    public DateTimeOffset? PublishedAt { get; private set; }

    public int PublishAttempts { get; private set; }

    public string? LastError { get; private set; }

    /// <summary>After a failed attempt, the relay leaves the row alone until this time (back-off).</summary>
    public DateTimeOffset? NextAttemptAt { get; private set; }

    public static OutboxMessage From(EventEnvelope envelope, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        return new OutboxMessage
        {
            Id = envelope.EventId,
            EventType = envelope.EventType,
            EventVersion = envelope.EventVersion,
            Topic = envelope.Topic,
            PartitionKey = envelope.PartitionKey,
            Payload = envelope.Payload,
            CorrelationId = envelope.CorrelationId,
            CausationId = envelope.CausationId,
            OccurredAt = envelope.OccurredAt,
            CreatedAt = now,
        };
    }

    internal void MarkPublished(DateTimeOffset now)
    {
        PublishedAt = now;
        PublishAttempts++;
        LastError = null;
        NextAttemptAt = null;
    }

    internal void RecordFailure(string error, DateTimeOffset nextAttemptAt)
    {
        PublishAttempts++;
        LastError = error.Length > 2000 ? error[..2000] : error;
        NextAttemptAt = nextAttemptAt;
    }

    /// <summary>Back to the envelope form the publisher sends.</summary>
    public EventEnvelope ToEnvelope() =>
        new(Id, EventType, EventVersion, Topic, PartitionKey, OccurredAt, CorrelationId, CausationId, Payload);
}
