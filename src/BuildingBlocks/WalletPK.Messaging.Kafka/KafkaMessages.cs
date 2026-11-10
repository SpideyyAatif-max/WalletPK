using System.Globalization;
using System.Text;
using Confluent.Kafka;
using WalletPK.Messaging.Serialization;

namespace WalletPK.Messaging.Kafka;

/// <summary>
/// How an <see cref="EventEnvelope"/> travels as a Kafka message: the partition key as the message
/// key, the JSON payload as the value, and the metadata as headers, so consumers can route and
/// de-duplicate without parsing the payload.
/// </summary>
public static class KafkaMessages
{
    public const string EventIdHeader = "event-id";
    public const string EventTypeHeader = "event-type";
    public const string EventVersionHeader = "event-version";
    public const string OccurredAtHeader = "occurred-at";
    public const string CorrelationIdHeader = "correlation-id";
    public const string CausationIdHeader = "causation-id";

    public static Message<string, string> ToMessage(EventEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        var headers = new Headers
        {
            { EventIdHeader, Encode(envelope.EventId.ToString()) },
            { EventTypeHeader, Encode(envelope.EventType) },
            { EventVersionHeader, Encode(envelope.EventVersion.ToString(CultureInfo.InvariantCulture)) },
            { OccurredAtHeader, Encode(envelope.OccurredAt.ToString("O", CultureInfo.InvariantCulture)) },
        };
        if (envelope.CorrelationId is not null)
        {
            headers.Add(CorrelationIdHeader, Encode(envelope.CorrelationId));
        }

        if (envelope.CausationId is not null)
        {
            headers.Add(CausationIdHeader, Encode(envelope.CausationId));
        }

        return new Message<string, string>
        {
            Key = envelope.PartitionKey,
            Value = envelope.Payload,
            Headers = headers,
            Timestamp = new Timestamp(envelope.OccurredAt.UtcDateTime, TimestampType.CreateTime),
        };
    }

    /// <summary>Rebuilds the envelope; false (and a reason) when a required header is missing or malformed.</summary>
    public static bool TryFromMessage(string topic, Message<string, string> message, out EventEnvelope? envelope, out string? problem)
    {
        ArgumentNullException.ThrowIfNull(message);
        envelope = null;

        var eventId = Read(message.Headers, EventIdHeader);
        var eventType = Read(message.Headers, EventTypeHeader);
        var version = Read(message.Headers, EventVersionHeader);
        var occurredAt = Read(message.Headers, OccurredAtHeader);

        if (!Guid.TryParse(eventId, out var id))
        {
            problem = "missing or invalid event-id header";
            return false;
        }

        if (string.IsNullOrWhiteSpace(eventType))
        {
            problem = "missing event-type header";
            return false;
        }

        if (!int.TryParse(version, NumberStyles.None, CultureInfo.InvariantCulture, out var eventVersion))
        {
            problem = "missing or invalid event-version header";
            return false;
        }

        if (!DateTimeOffset.TryParse(occurredAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var occurred))
        {
            occurred = new DateTimeOffset(message.Timestamp.UtcDateTime, TimeSpan.Zero);
        }

        if (string.IsNullOrEmpty(message.Value))
        {
            problem = "empty payload";
            return false;
        }

        envelope = new EventEnvelope(
            id,
            eventType,
            eventVersion,
            topic,
            message.Key ?? string.Empty,
            occurred,
            Read(message.Headers, CorrelationIdHeader),
            Read(message.Headers, CausationIdHeader),
            message.Value);
        problem = null;
        return true;
    }

    private static byte[] Encode(string value) => Encoding.UTF8.GetBytes(value);

    private static string? Read(Headers? headers, string name) =>
        headers is not null && headers.TryGetLastBytes(name, out var bytes) ? Encoding.UTF8.GetString(bytes) : null;
}
