using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using WalletPK.Sagas.Tests.Infrastructure;

namespace WalletPK.Sagas.Tests;

/// <summary>M14.B5: the Saga explorer searches a service's Sagas and shows each with its data and traced timeline.</summary>
[Collection(SagaCollection.Name)]
public class SagaExplorerTests(SagaFixture fixture) : IAsyncLifetime
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => await fixture.ResetAsync(Cancel);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private async Task StartAsync(Guid id, Guid userId, string correlationId)
    {
        await using var scope = fixture.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<ISagaEngine>().StartAsync(
            fixture.Services.GetRequiredService<TestTransferSaga>(), id, new TestTransferData { UserId = userId, Amount = 100 }, correlationId, Cancel);
    }

    private async Task SendAsync(Guid id, object message)
    {
        await using var scope = fixture.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<ISagaEngine>().HandleAsync(id, message, causationId: null, Cancel);
    }

    private async Task<T> ExploreAsync<T>(Func<ISagaExplorer, Task<T>> query)
    {
        await using var scope = fixture.Services.CreateAsyncScope();
        return await query(scope.ServiceProvider.GetRequiredService<ISagaExplorer>());
    }

    [Fact]
    public async Task Sagas_are_found_by_type_state_user_correlation_and_how_long_they_have_been_stuck()
    {
        var alice = Guid.NewGuid();
        var bob = Guid.NewGuid();
        Guid old = Guid.NewGuid(), finished = Guid.NewGuid(), recent = Guid.NewGuid();
        await StartAsync(old, alice, "corr-old");
        fixture.Clock.Advance(TimeSpan.FromMinutes(1));
        await StartAsync(finished, alice, "corr-finished");
        await SendAsync(finished, new MoneyArrived(finished));
        fixture.Clock.Advance(TimeSpan.FromMinutes(30));
        await StartAsync(recent, bob, "corr-recent");

        (await ExploreAsync(explorer => explorer.SearchAsync(new SagaSearch(), Cancel))).Select(saga => saga.Id).ShouldBe([recent, finished, old]);
        (await ExploreAsync(explorer => explorer.SearchAsync(new SagaSearch(UserId: alice), Cancel))).Select(saga => saga.Id).ShouldBe([finished, old]);
        (await ExploreAsync(explorer => explorer.SearchAsync(new SagaSearch(UserId: Guid.NewGuid()), Cancel))).ShouldBeEmpty();
        (await ExploreAsync(explorer => explorer.SearchAsync(new SagaSearch(Active: true), Cancel))).Select(saga => saga.Id).ShouldBe([recent, old]);
        (await ExploreAsync(explorer => explorer.SearchAsync(new SagaSearch(State: "Completed"), Cancel))).ShouldHaveSingleItem().Id.ShouldBe(finished);
        (await ExploreAsync(explorer => explorer.SearchAsync(new SagaSearch(StuckForMinutes: 20), Cancel))).ShouldHaveSingleItem().Id.ShouldBe(old);
        (await ExploreAsync(explorer => explorer.SearchAsync(new SagaSearch(CorrelationId: "corr-recent"), Cancel))).ShouldHaveSingleItem().Id.ShouldBe(recent);
        (await ExploreAsync(explorer => explorer.SearchAsync(new SagaSearch(Type: "another-type"), Cancel))).ShouldBeEmpty();

        // Paging: the next page starts before the last one shown.
        var first = await ExploreAsync(explorer => explorer.SearchAsync(new SagaSearch(Limit: 1), Cancel));
        var next = await ExploreAsync(explorer => explorer.SearchAsync(new SagaSearch(Before: first[0].UpdatedAt, Limit: 5), Cancel));
        next.Select(saga => saga.Id).ShouldBe([finished, old]);

        var counts = await ExploreAsync(explorer => explorer.CountsAsync(TimeSpan.FromHours(1), Cancel));
        counts.ShouldContain(count => count.Type == "test-transfer" && count.State == "Sending" && !count.IsCompleted && count.Count == 2);
        counts.ShouldContain(count => count.Type == "test-transfer" && count.State == "Completed" && count.IsCompleted && count.Count == 1);
    }

    [Fact]
    public async Task A_saga_opens_with_its_data_and_each_step_carries_the_trace_it_ran_in()
    {
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "explorer-test",
            Sample = (ref _) => ActivitySamplingResult.AllDataAndRecorded,
        };
        ActivitySource.AddActivityListener(listener);
        using var source = new ActivitySource("explorer-test");

        var id = Guid.NewGuid();
        var user = Guid.NewGuid();
        string startTrace, arriveTrace;
        using (var request = source.StartActivity("request"))
        {
            startTrace = request!.TraceId.ToHexString();
            await StartAsync(id, user, "corr-1");
        }

        using (var consumer = source.StartActivity("consume"))
        {
            arriveTrace = consumer!.TraceId.ToHexString();
            await SendAsync(id, new MoneyArrived(id));
        }

        var saga = await ExploreAsync(explorer => explorer.GetAsync(id, Cancel));

        saga.ShouldNotBeNull();
        saga.Saga.State.ShouldBe("Completed");
        saga.Saga.IsCompleted.ShouldBeTrue();
        saga.Saga.Steps.ShouldBe(2);
        saga.Data.GetProperty("userId").GetGuid().ShouldBe(user);
        saga.Data.GetProperty("amount").GetInt64().ShouldBe(100);
        saga.Timeline.Select(step => (step.State, step.Trigger, step.TraceId)).ShouldBe([("Sending", "start", startTrace), ("Completed", "MoneyArrived", arriveTrace)]);
        (await ExploreAsync(explorer => explorer.GetAsync(Guid.NewGuid(), Cancel))).ShouldBeNull();
    }
}
