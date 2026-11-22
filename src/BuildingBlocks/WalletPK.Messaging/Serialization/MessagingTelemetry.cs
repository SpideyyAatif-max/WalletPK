using System.Diagnostics;

namespace WalletPK.Messaging.Serialization;

/// <summary>
/// Distributed tracing across Kafka (M15.1): the ActivitySource "WalletPK.Messaging" has a producer span for
/// every publish and a consumer span for every event processed, linked through the W3C traceparent carried
/// in the envelope (and the Kafka header of the same name). A transfer is one trace from the HTTP request
/// through the outbox, Kafka, every consumer and the Sagas they drive.
/// </summary>
public static class MessagingTelemetry
{
    public const string SourceName = "WalletPK.Messaging";

    public static ActivitySource Source { get; } = new(SourceName);

    /// <summary>The current span as a W3C traceparent, or null when nothing is being traced.</summary>
    public static string? CurrentTraceParent() =>
        Activity.Current is { IdFormat: ActivityIdFormat.W3C } current ? current.Id : null;

    /// <summary>The trace context in a traceparent, or default when there is none or it is malformed.</summary>
    public static ActivityContext ParentOf(string? traceParent) =>
        traceParent is not null && ActivityContext.TryParse(traceParent, null, out var context) ? context : default;

    /// <summary>Starts a span for publishing <paramref name="envelope"/>, as a child of the work that wrote it.</summary>
    public static Activity? StartPublish(EventEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        var activity = Source.StartActivity($"{envelope.Topic} publish", ActivityKind.Producer, ParentOf(envelope.TraceParent));
        Tag(activity, envelope, "publish");
        return activity;
    }

    /// <summary>Starts a span for processing <paramref name="envelope"/>, as a child of its producer span.</summary>
    public static Activity? StartProcess(EventEnvelope envelope, string consumerGroup)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        var activity = Source.StartActivity($"{envelope.Topic} process", ActivityKind.Consumer, ParentOf(envelope.TraceParent));
        Tag(activity, envelope, "process");
        activity?.SetTag("messaging.consumer.group.name", consumerGroup);
        return activity;
    }

    private static void Tag(Activity? activity, EventEnvelope envelope, string operation)
    {
        if (activity is null)
        {
            return;
        }

        activity.SetTag("messaging.system", "kafka");
        activity.SetTag("messaging.operation.type", operation);
        activity.SetTag("messaging.destination.name", envelope.Topic);
        activity.SetTag("messaging.message.id", envelope.EventId.ToString());
        activity.SetTag("messaging.kafka.message.key", envelope.PartitionKey);
        activity.SetTag("walletpk.event.type", envelope.EventType);
        if (envelope.CorrelationId is not null)
        {
            activity.SetTag("walletpk.correlation_id", envelope.CorrelationId);
        }
    }
}
