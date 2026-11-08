using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;
using WalletPK.Messaging.Events;

namespace WalletPK.Messaging.Serialization;

/// <summary>
/// Converts integration events to and from <see cref="EventEnvelope"/>s using System.Text.Json
/// (camelCase, enums as strings, money as exact minor units).
/// </summary>
public sealed class JsonEventSerializer(IntegrationEventRegistry registry)
{
    /// <summary>The JSON settings every WalletPK event payload uses.</summary>
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    /// <summary>Wraps an event for the outbox / Kafka.</summary>
    public EventEnvelope Wrap(IntegrationEvent integrationEvent, string? correlationId = null, string? causationId = null)
    {
        ArgumentNullException.ThrowIfNull(integrationEvent);

        var eventType = integrationEvent.GetType();
        var metadata = registry.GetMetadata(eventType);

        return new EventEnvelope(
            integrationEvent.EventId,
            metadata.Name,
            metadata.Version,
            metadata.Topic,
            integrationEvent.GetPartitionKey(),
            integrationEvent.OccurredAt,
            correlationId,
            causationId,
            JsonSerializer.Serialize(integrationEvent, eventType, Options));
    }

    /// <summary>
    /// Restores the event from an envelope. Returns false for event types this service does not know
    /// (a consumer simply skips events it is not interested in, which keeps services independent).
    /// </summary>
    public bool TryUnwrap(EventEnvelope envelope, [NotNullWhen(true)] out IntegrationEvent? integrationEvent)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        if (!registry.TryGetType(envelope.EventType, envelope.EventVersion, out var eventType))
        {
            integrationEvent = null;
            return false;
        }

        integrationEvent = JsonSerializer.Deserialize(envelope.Payload, eventType, Options) as IntegrationEvent
            ?? throw new JsonException($"Payload of event {envelope.EventId} ({envelope.EventType}) is empty.");
        return true;
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}
