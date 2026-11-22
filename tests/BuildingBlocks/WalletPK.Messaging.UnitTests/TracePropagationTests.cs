using System.Diagnostics;
using WalletPK.Common.Identifiers;
using WalletPK.Common.Primitives;
using WalletPK.Messaging.Kafka;
using WalletPK.Messaging.Serialization;
using WalletPK.Messaging.UnitTests.Events;

namespace WalletPK.Messaging.UnitTests;

/// <summary>M15.1: the trace context travels with an event, through the outbox row and the Kafka headers.</summary>
public class TracePropagationTests
{
    private readonly JsonEventSerializer _serializer = new(new IntegrationEventRegistry().Register<MoneyTransferred>());

    private static MoneyTransferred Transfer() =>
        new(TransactionId.New(), WalletId.New(), WalletId.New(), Money.FromRupees(100m), TransferChannel.App);

    [Fact]
    public void An_event_written_while_tracing_carries_the_trace_and_one_written_outside_carries_none()
    {
        Activity.Current = null;
        _serializer.Wrap(Transfer()).TraceParent.ShouldBeNull();

        using var activity = new Activity("POST /api/wallets/transfers").SetIdFormat(ActivityIdFormat.W3C).Start();
        var envelope = _serializer.Wrap(Transfer());

        envelope.TraceParent.ShouldBe(activity.Id);
        MessagingTelemetry.ParentOf(envelope.TraceParent).TraceId.ShouldBe(activity.TraceId);
    }

    [Fact]
    public void The_trace_context_survives_the_trip_through_Kafka_headers()
    {
        using var activity = new Activity("work").SetIdFormat(ActivityIdFormat.W3C).Start();
        var envelope = _serializer.Wrap(Transfer());

        var message = KafkaMessages.ToMessage(envelope);
        KafkaMessages.TryFromMessage(envelope.Topic, message, out var back, out _).ShouldBeTrue();

        back!.TraceParent.ShouldBe(envelope.TraceParent);
        message.Headers.TryGetLastBytes(KafkaMessages.TraceParentHeader, out _).ShouldBeTrue();
    }

    [Fact]
    public void A_missing_or_broken_traceparent_starts_nothing_odd()
    {
        MessagingTelemetry.ParentOf(null).ShouldBe(default);
        MessagingTelemetry.ParentOf("not-a-traceparent").ShouldBe(default);
    }
}
