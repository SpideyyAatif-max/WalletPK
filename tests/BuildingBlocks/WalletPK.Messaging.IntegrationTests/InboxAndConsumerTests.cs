using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Confluent.Kafka;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using WalletPK.Common.Correlation;
using WalletPK.Messaging.Abstractions;
using WalletPK.Messaging.IntegrationTests.Infrastructure;
using WalletPK.Messaging.Kafka;
using WalletPK.Messaging.Serialization;
using WalletPK.Persistence.Inbox;

namespace WalletPK.Messaging.IntegrationTests;

/// <summary>M4.3: the inbox (exactly-once processing) and the Kafka consumer host (order, retry, commits).</summary>
[Collection(PipelineCollection.Name)]
public class InboxAndConsumerTests(PipelineFixture fixture)
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    private async Task ProcessAsync(ServiceProvider services, EventEnvelope envelope, string consumer = "test")
    {
        await using var scope = services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IEnvelopeProcessor>().ProcessAsync(envelope, consumer, Cancel);
    }

    private async Task<(int Notes, int InboxRows)> CountAsync(string key, Guid eventId)
    {
        await using var db = fixture.NewDbContext();
        return (
            await db.Notes.CountAsync(note => note.Text.StartsWith(key + ":"), Cancel),
            await db.Set<InboxMessage>().CountAsync(row => row.EventId == eventId, Cancel));
    }

    [Fact]
    public async Task An_event_is_processed_once_however_often_it_is_delivered()
    {
        using var services = fixture.InboxServices();
        var key = Guid.NewGuid().ToString("N");
        var envelope = fixture.Serializer.Wrap(new SomethingHappened(key, 1));

        await ProcessAsync(services, envelope);
        await ProcessAsync(services, envelope);
        await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => ProcessAsync(services, envelope)));

        (await CountAsync(key, envelope.EventId)).ShouldBe((1, 1));
    }

    [Fact]
    public async Task Each_consumer_has_its_own_inbox()
    {
        using var services = fixture.InboxServices();
        var key = Guid.NewGuid().ToString("N");
        var envelope = fixture.Serializer.Wrap(new SomethingHappened(key, 1));

        await ProcessAsync(services, envelope, consumer: "ledger");
        await ProcessAsync(services, envelope, consumer: "notifications");

        (await CountAsync(key, envelope.EventId)).ShouldBe((2, 2));
    }

    [Fact]
    public async Task When_a_handler_fails_nothing_is_kept_and_the_event_can_be_processed_again()
    {
        using var services = fixture.InboxServices();
        var key = Guid.NewGuid().ToString("N");
        var failing = fixture.Serializer.Wrap(new SomethingHappened(key, -1));

        await Should.ThrowAsync<InvalidOperationException>(() => ProcessAsync(services, failing));

        // Neither the handler's note nor the inbox row survived, so a retry is not mistaken for a duplicate.
        (await CountAsync(key, failing.EventId)).ShouldBe((0, 0));
    }

    [Fact]
    public async Task Events_this_service_does_not_know_are_ignored()
    {
        using var services = fixture.InboxServices();
        var unknown = new EventEnvelope(Guid.NewGuid(), "payments.something_else", 1, KafkaTopics.PaymentsEvents, "k", DateTimeOffset.UtcNow, null, null, "{}");

        await ProcessAsync(services, unknown);

        await using var db = fixture.NewDbContext();
        (await db.Set<InboxMessage>().CountAsync(row => row.EventId == unknown.EventId, Cancel)).ShouldBe(0);
    }

    /// <summary>Fails the first attempts at chosen events, and records what it processed, in order.</summary>
    private sealed class FlakyProcessor(ConcurrentDictionary<Guid, int> attempts, ConcurrentQueue<(Guid EventId, string? CorrelationId)> processed, int failuresPerEvent)
        : IEnvelopeProcessor
    {
        public Task ProcessAsync(EventEnvelope envelope, string consumer, CancellationToken cancellationToken)
        {
            var attempt = attempts.AddOrUpdate(envelope.EventId, 1, (_, count) => count + 1);
            if (attempt <= failuresPerEvent)
            {
                throw new InvalidOperationException($"Transient failure {attempt} (test)");
            }

            processed.Enqueue((envelope.EventId, CorrelationContext.Current));
            return Task.CompletedTask;
        }
    }

    private async Task<(KafkaConsumerService Consumer, ServiceProvider Services)> StartConsumerAsync(
        string groupId, string topic, IEnvelopeProcessor processor)
    {
        var services = new ServiceCollection()
            .AddMetrics()
            .AddScoped(_ => processor)
            .BuildServiceProvider();
        var consumer = new KafkaConsumerService(
            new KafkaConsumerDefinition(groupId, [topic]),
            new KafkaOptions { BootstrapServers = fixture.KafkaBootstrapServers },
            services.GetRequiredService<IServiceScopeFactory>(),
            services.GetRequiredService<IMeterFactory>(),
            NullLogger<KafkaConsumerService>.Instance);
        await consumer.StartAsync(Cancel);
        return (consumer, services);
    }

    private async Task<List<EventEnvelope>> PublishAsync(string topic, string key, int count, string? correlationId = null)
    {
        await new KafkaTopicAdmin(new KafkaOptions { BootstrapServers = fixture.KafkaBootstrapServers }, NullLogger<KafkaTopicAdmin>.Instance)
            .EnsureTopicsAsync([topic], Cancel);
        using var publisher = new KafkaEventPublisher(new KafkaOptions { BootstrapServers = fixture.KafkaBootstrapServers }, NullLogger<KafkaEventPublisher>.Instance);
        var envelopes = Enumerable.Range(1, count)
            .Select(number => fixture.Serializer.Wrap(new SomethingHappened(key, number), correlationId) with { Topic = topic })
            .ToList();
        foreach (var envelope in envelopes)
        {
            await publisher.PublishAsync(envelope, Cancel);
        }

        return envelopes;
    }

    private static async Task WaitForAsync(Func<bool> condition, int seconds = 30)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("The consumer did not get there in time.");
            }

            await Task.Delay(100, Cancel);
        }
    }

    [Fact]
    public async Task The_consumer_retries_a_failing_event_without_skipping_ahead_and_restores_its_correlation_id()
    {
        var topic = KafkaTopics.BillsEvents;
        var envelopes = await PublishAsync(topic, key: "same-partition", count: 3, correlationId: "user-action-42");
        var attempts = new ConcurrentDictionary<Guid, int>();
        var processed = new ConcurrentQueue<(Guid EventId, string? CorrelationId)>();

        var (consumer, services) = await StartConsumerAsync($"retry-{Guid.NewGuid():N}", topic, new FlakyProcessor(attempts, processed, failuresPerEvent: 2));
        try
        {
            await WaitForAsync(() => processed.Count == 3);
        }
        finally
        {
            await consumer.StopAsync(CancellationToken.None);
            await services.DisposeAsync();
        }

        // In order, each one only after its own retries succeeded: event 2 never overtook event 1.
        processed.Select(item => item.EventId).ShouldBe(envelopes.Select(envelope => envelope.EventId));
        attempts.Values.ShouldAllBe(count => count == 3);
        processed.ShouldAllBe(item => item.CorrelationId == "user-action-42");
    }

    [Fact]
    public async Task After_a_restart_the_consumer_continues_where_it_left_off()
    {
        var topic = KafkaTopics.TopupEvents;
        var groupId = $"restart-{Guid.NewGuid():N}";
        var first = await PublishAsync(topic, key: "k", count: 3);
        var processed = new ConcurrentQueue<(Guid EventId, string? CorrelationId)>();

        var (consumer, services) = await StartConsumerAsync(groupId, topic, new FlakyProcessor(new(), processed, failuresPerEvent: 0));
        await WaitForAsync(() => processed.Count == 3);
        await consumer.StopAsync(CancellationToken.None);   // commits the offsets it stored
        await services.DisposeAsync();

        var second = await PublishAsync(topic, key: "k", count: 2);
        var afterRestart = new ConcurrentQueue<(Guid EventId, string? CorrelationId)>();
        (consumer, services) = await StartConsumerAsync(groupId, topic, new FlakyProcessor(new(), afterRestart, failuresPerEvent: 0));
        try
        {
            await WaitForAsync(() => afterRestart.Count == 2);
            await Task.Delay(1_000, Cancel); // nothing else should trickle in
        }
        finally
        {
            await consumer.StopAsync(CancellationToken.None);
            await services.DisposeAsync();
        }

        processed.Select(item => item.EventId).ShouldBe(first.Select(envelope => envelope.EventId));
        afterRestart.Select(item => item.EventId).ShouldBe(second.Select(envelope => envelope.EventId));
    }

    [Fact]
    public void Envelopes_survive_the_trip_through_kafka_headers()
    {
        var envelope = new EventEnvelope(Guid.NewGuid(), "ledger.wallet_entry_posted", 2, KafkaTopics.LedgerEvents, "wallet-1",
            new DateTimeOffset(2026, 11, 10, 9, 30, 15, 123, TimeSpan.Zero), "corr-1", "cause-1", "{\"a\":1}");

        var message = KafkaMessages.ToMessage(envelope);
        KafkaMessages.TryFromMessage(KafkaTopics.LedgerEvents, message, out var back, out var problem).ShouldBeTrue(problem);

        back.ShouldBe(envelope);
        message.Key.ShouldBe("wallet-1");
    }

    [Fact]
    public void A_message_without_the_required_headers_is_reported_not_thrown()
    {
        var message = new Message<string, string> { Key = "k", Value = "{}" };

        KafkaMessages.TryFromMessage(KafkaTopics.LedgerEvents, message, out var envelope, out var problem).ShouldBeFalse();

        envelope.ShouldBeNull();
        problem.ShouldBe("missing or invalid event-id header");
    }
}
