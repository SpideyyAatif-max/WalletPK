using System.Text;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;
using WalletPK.Ledger.IntegrationTests.Infrastructure;
using WalletPK.Web.Idempotency;

namespace WalletPK.Ledger.IntegrationTests;

/// <summary>The Ledger's idempotency store against real PostgreSQL (durable) and Redis (fast path).</summary>
[Collection(LedgerCollection.Name)]
public class IdempotencyStoreTests(LedgerApiFactory factory)
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    private static string NewKey() => $"user-1:POST:/API/WALLETS/TRANSFERS:{Guid.NewGuid():N}";

    private async Task<T> WithStoreAsync<T>(Func<IIdempotencyStore, Task<T>> action)
    {
        using var client = factory.CreateClient();
        await using var scope = factory.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<IIdempotencyStore>());
    }

    private static StoredResponse Created(string body) =>
        new(201, "application/json; charset=utf-8", "/api/wallets/transfers/42", Encoding.UTF8.GetBytes(body));

    [Fact]
    public async Task The_first_request_starts_and_an_identical_one_waits_while_it_runs()
    {
        var key = NewKey();

        var first = await WithStoreAsync(store => store.TryBeginAsync(key, "hash-a", Cancel));
        var second = await WithStoreAsync(store => store.TryBeginAsync(key, "hash-a", Cancel));
        var different = await WithStoreAsync(store => store.TryBeginAsync(key, "hash-b", Cancel));

        first.Status.ShouldBe(IdempotencyStatus.Started);
        second.Status.ShouldBe(IdempotencyStatus.InProgress);
        different.Status.ShouldBe(IdempotencyStatus.Mismatch);
    }

    [Fact]
    public async Task Of_twenty_identical_requests_arriving_together_exactly_one_starts()
    {
        var key = NewKey();

        var outcomes = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ =>
            Task.Run(() => WithStoreAsync(store => store.TryBeginAsync(key, "same", Cancel)), Cancel)));

        outcomes.Count(outcome => outcome.Status == IdempotencyStatus.Started).ShouldBe(1);
        outcomes.Count(outcome => outcome.Status == IdempotencyStatus.InProgress).ShouldBe(19);
    }

    [Fact]
    public async Task A_completed_response_is_replayed_from_redis_and_still_from_postgres_without_redis()
    {
        var key = NewKey();
        await WithStoreAsync(store => store.TryBeginAsync(key, "hash-a", Cancel));
        await WithStoreAsync(async store =>
        {
            await store.CompleteAsync(key, Created("{\"ok\":true}"), Cancel);
            return 0;
        });

        var fromRedis = await WithStoreAsync(store => store.TryBeginAsync(key, "hash-a", Cancel));

        // Simulate Redis losing its data: the durable copy in PostgreSQL still answers.
        await factory.Services.GetRequiredService<IConnectionMultiplexer>().GetDatabase().KeyDeleteAsync($"ledger:idempotency:{key}");
        var fromPostgres = await WithStoreAsync(store => store.TryBeginAsync(key, "hash-a", Cancel));

        foreach (var replay in new[] { fromRedis, fromPostgres })
        {
            replay.Status.ShouldBe(IdempotencyStatus.Completed);
            replay.Response!.StatusCode.ShouldBe(201);
            replay.Response.Location.ShouldBe("/api/wallets/transfers/42");
            Encoding.UTF8.GetString(replay.Response.Body).ShouldBe("{\"ok\":true}");
        }
    }

    [Fact]
    public async Task An_abandoned_request_can_be_retried_for_real()
    {
        var key = NewKey();
        await WithStoreAsync(store => store.TryBeginAsync(key, "hash-a", Cancel));

        await WithStoreAsync(async store =>
        {
            await store.AbandonAsync(key, Cancel);
            return 0;
        });

        (await WithStoreAsync(store => store.TryBeginAsync(key, "hash-a", Cancel))).Status.ShouldBe(IdempotencyStatus.Started);
    }

    [Fact]
    public async Task A_request_whose_server_crashed_is_taken_over_after_the_timeout()
    {
        var key = NewKey();
        await WithStoreAsync(store => store.TryBeginAsync(key, "hash-a", Cancel));

        var stillRunning = await WithStoreAsync(store => store.TryBeginAsync(key, "hash-a", Cancel));
        factory.Clock.Advance(TimeSpan.FromSeconds(61));
        var takenOver = await WithStoreAsync(store => store.TryBeginAsync(key, "hash-a", Cancel));

        stillRunning.Status.ShouldBe(IdempotencyStatus.InProgress);
        takenOver.Status.ShouldBe(IdempotencyStatus.Started);
    }
}
