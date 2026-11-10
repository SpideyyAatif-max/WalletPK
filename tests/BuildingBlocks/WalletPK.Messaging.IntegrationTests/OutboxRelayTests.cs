using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using WalletPK.Messaging.IntegrationTests.Infrastructure;
using WalletPK.Persistence.Outbox;

namespace WalletPK.Messaging.IntegrationTests;

/// <summary>M4.1: the outbox relay against real PostgreSQL (row locks, SKIP LOCKED, back-off, cleanup).</summary>
[Collection(PipelineCollection.Name)]
public class OutboxRelayTests(PipelineFixture fixture) : IAsyncLifetime
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    // Each test starts with an empty outbox.
    public async ValueTask InitializeAsync()
    {
        await using var db = fixture.NewDbContext();
        await db.Set<OutboxMessage>().ExecuteDeleteAsync(Cancel);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private async Task<List<OutboxMessage>> RowsAsync()
    {
        await using var db = fixture.NewDbContext();
        return await db.Set<OutboxMessage>().AsNoTracking().OrderBy(row => row.CreatedAt).ToListAsync(Cancel);
    }

    [Fact]
    public async Task Publishes_pending_rows_oldest_first_and_marks_them_published()
    {
        var written = await fixture.AddToOutboxAsync(5);
        var publisher = new RecordingPublisher();

        await using var db = fixture.NewDbContext();
        var first = await fixture.Relay(db, publisher).PublishBatchAsync(Cancel);
        var second = await fixture.Relay(db, publisher).PublishBatchAsync(Cancel);

        first.ShouldBe(new OutboxRelayResult(5, 0));
        second.ShouldBe(new OutboxRelayResult(0, 0));
        publisher.Published.Select(envelope => envelope.EventId).ShouldBe(written.Select(envelope => envelope.EventId));
        // Every field survives the trip through the table. (jsonb re-spaces the JSON and PostgreSQL keeps
        // microseconds, so those two are compared by meaning.)
        var sent = publisher.Published.First();
        sent.ShouldBe(written[0] with { Payload = sent.Payload, OccurredAt = sent.OccurredAt });
        JsonNode.DeepEquals(JsonNode.Parse(sent.Payload), JsonNode.Parse(written[0].Payload)).ShouldBeTrue();
        sent.OccurredAt.ShouldBe(written[0].OccurredAt, TimeSpan.FromMicroseconds(1));
        (await RowsAsync()).ShouldAllBe(row => row.PublishedAt != null && row.PublishAttempts == 1 && row.LastError == null);
    }

    [Fact]
    public async Task A_row_that_fails_waits_out_its_back_off_then_is_retried()
    {
        var written = await fixture.AddToOutboxAsync(3);
        var bad = written[1].EventId;
        var publisher = new RecordingPublisher { ShouldFail = envelope => envelope.EventId == bad };

        await using (var db = fixture.NewDbContext())
        {
            (await fixture.Relay(db, publisher).PublishBatchAsync(Cancel)).ShouldBe(new OutboxRelayResult(2, 1));
        }

        var failed = (await RowsAsync()).Single(row => row.Id == bad);
        failed.PublishedAt.ShouldBeNull();
        failed.PublishAttempts.ShouldBe(1);
        failed.LastError.ShouldBe("Broker unavailable (test)");
        failed.NextAttemptAt.ShouldNotBeNull().ShouldBe(fixture.Clock.GetUtcNow().AddSeconds(1), TimeSpan.FromMilliseconds(1));

        // During the back-off the row is left alone, even though the broker is fine again.
        publisher.ShouldFail = _ => false;
        await using (var db = fixture.NewDbContext())
        {
            (await fixture.Relay(db, publisher).PublishBatchAsync(Cancel)).ShouldBe(new OutboxRelayResult(0, 0));
        }

        fixture.Clock.Advance(TimeSpan.FromSeconds(2));
        await using (var db = fixture.NewDbContext())
        {
            (await fixture.Relay(db, publisher).PublishBatchAsync(Cancel)).ShouldBe(new OutboxRelayResult(1, 0));
        }

        var recovered = (await RowsAsync()).Single(row => row.Id == bad);
        recovered.PublishedAt.ShouldNotBeNull();
        recovered.PublishAttempts.ShouldBe(2);
        recovered.LastError.ShouldBeNull();
    }

    [Fact]
    public async Task Back_off_doubles_up_to_the_maximum()
    {
        await using var db = fixture.NewDbContext();
        var relay = fixture.Relay(db, new RecordingPublisher());

        Enumerable.Range(1, 8).Select(attempt => relay.Backoff(attempt).TotalSeconds).ShouldBe([1, 2, 4, 8, 16, 32, 60, 60]);
    }

    [Fact]
    public async Task Parallel_relays_publish_every_row_exactly_once()
    {
        // Several service instances, each with its own relay, racing over the same outbox.
        var written = await fixture.AddToOutboxAsync(300);
        var publisher = new RecordingPublisher();

        async Task DrainAsync()
        {
            while (true)
            {
                await using var db = fixture.NewDbContext();
                if ((await fixture.Relay(db, publisher, batchSize: 20).PublishBatchAsync(Cancel)).Total == 0)
                {
                    return;
                }
            }
        }

        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => DrainAsync()));

        publisher.Published.Count.ShouldBe(300);
        publisher.Published.Select(envelope => envelope.EventId).ShouldBe(written.Select(envelope => envelope.EventId), ignoreOrder: true);
        (await RowsAsync()).ShouldAllBe(row => row.PublishedAt != null && row.PublishAttempts == 1);
    }

    [Fact]
    public async Task Old_published_rows_are_cleared_but_recent_and_pending_ones_stay()
    {
        await fixture.AddToOutboxAsync(2);
        await using (var db = fixture.NewDbContext())
        {
            await fixture.Relay(db, new RecordingPublisher()).PublishBatchAsync(Cancel);
        }

        fixture.Clock.Advance(TimeSpan.FromHours(73));
        await fixture.AddToOutboxAsync(1);                     // pending
        await using (var db = fixture.NewDbContext())
        {
            (await fixture.Relay(db, new RecordingPublisher { ShouldFail = _ => true }).PublishBatchAsync(Cancel)).Failed.ShouldBe(1);
            (await fixture.Relay(db, new RecordingPublisher()).DeleteOldPublishedAsync(Cancel)).ShouldBe(2);
        }

        (await RowsAsync()).ShouldHaveSingleItem().PublishedAt.ShouldBeNull();
    }
}
