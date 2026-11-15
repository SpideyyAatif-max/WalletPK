using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WalletPK.Persistence.Outbox;
using WalletPK.Sagas.Tests.Infrastructure;

namespace WalletPK.Sagas.Tests;

/// <summary>M6.3: the Saga engine against real PostgreSQL.</summary>
[Collection(SagaCollection.Name)]
public class SagaEngineTests(SagaFixture fixture) : IAsyncLifetime
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => await fixture.ResetAsync(Cancel);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private TestTransferSaga Definition => fixture.Services.GetRequiredService<TestTransferSaga>();

    private SagaTimerRunner<TestDbContext> Timers => fixture.Services.GetRequiredService<SagaTimerRunner<TestDbContext>>();

    private async Task<bool> StartAsync(Guid id, long amount = 500)
    {
        await using var scope = fixture.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<SagaEngine<TestDbContext>>()
            .StartAsync(Definition, id, new TestTransferData { Amount = amount }, correlationId: "corr-1", Cancel);
    }

    private async Task<SagaHandleOutcome> SendAsync(Guid id, object message)
    {
        await using var scope = fixture.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<SagaEngine<TestDbContext>>().HandleAsync(id, message, causationId: null, Cancel);
    }

    private async Task<SagaSnapshot<TestTransferData>> ReadAsync(Guid id)
    {
        await using var scope = fixture.Services.CreateAsyncScope();
        var saga = await scope.ServiceProvider.GetRequiredService<SagaReader<TestDbContext>>().FindAsync<TestTransferData>(id, Cancel);
        saga.ShouldNotBeNull();
        return saga;
    }

    private async Task<List<OutboxMessage>> OutboxAsync()
    {
        await using var db = fixture.NewDbContext();
        return await db.Set<OutboxMessage>().AsNoTracking().OrderBy(row => row.CreatedAt).ToListAsync(Cancel);
    }

    [Fact]
    public async Task Starting_saves_the_first_step_its_messages_and_its_timer_and_a_second_start_does_nothing()
    {
        var id = Guid.NewGuid();

        (await StartAsync(id)).ShouldBeTrue();
        (await StartAsync(id, amount: 999)).ShouldBeFalse();

        var saga = await ReadAsync(id);
        saga.SagaType.ShouldBe("test-transfer");
        saga.State.ShouldBe("Sending");
        saga.Data.Amount.ShouldBe(500);
        saga.Version.ShouldBe(0);
        saga.CorrelationId.ShouldBe("corr-1");
        saga.WakeReason.ShouldBe("send");
        saga.WakeAt.ShouldNotBeNull();
        saga.Steps.Count.ShouldBe(1);
        saga.Steps[0].ShouldSatisfyAllConditions(
            step => step.Sequence.ShouldBe(1),
            step => step.PreviousState.ShouldBe(SagaStates.New),
            step => step.State.ShouldBe("Sending"),
            step => step.Trigger.ShouldBe("start"),
            step => step.Detail.ShouldBe("Accepted 500"));

        var outbox = await OutboxAsync();
        outbox.Count.ShouldBe(1);
        outbox[0].EventType.ShouldBe("test.hold_money");
        outbox[0].CorrelationId.ShouldBe("corr-1");
    }

    [Fact]
    public async Task A_timer_step_does_its_io_outside_the_transaction_then_moves_the_saga_on()
    {
        var id = Guid.NewGuid();
        await StartAsync(id);

        (await Timers.RunDueAsync(Cancel)).ShouldBe(1);

        fixture.Bank.CallsFor(id).ShouldBe(1);
        var saga = await ReadAsync(id);
        saga.State.ShouldBe("AwaitingBank");
        saga.Data.BankReference.ShouldBe($"BNK-{id.ToString()[..8]}");
        saga.Version.ShouldBe(1);
        saga.WakeReason.ShouldBe("status-check");
        saga.WakeAt.ShouldNotBeNull().ShouldBe(fixture.Clock.GetUtcNow().AddMinutes(5), TimeSpan.FromMilliseconds(1));
        saga.Steps.Select(step => step.Trigger).ShouldBe(["start", "timer:send"]);

        // The next timer is not due yet; once it is, it fires (and only then).
        (await Timers.RunDueAsync(Cancel)).ShouldBe(0);
        fixture.Clock.Advance(TimeSpan.FromMinutes(5));
        (await Timers.RunDueAsync(Cancel)).ShouldBe(1);
        (await ReadAsync(id)).Steps[^1].Detail.ShouldBe("Still waiting for the bank");
        fixture.Bank.CallsFor(id).ShouldBe(1);
    }

    [Fact]
    public async Task A_message_moves_the_saga_on_and_a_completed_saga_ignores_later_messages()
    {
        var id = Guid.NewGuid();
        await StartAsync(id);
        await Timers.RunDueAsync(Cancel);

        (await SendAsync(id, new MoneyArrived(id))).ShouldBe(SagaHandleOutcome.Handled);
        (await SendAsync(id, new MoneyArrived(id))).ShouldBe(SagaHandleOutcome.Ignored);
        (await SendAsync(id, new Increment(id))).ShouldBe(SagaHandleOutcome.Ignored);

        var saga = await ReadAsync(id);
        saga.State.ShouldBe("Completed");
        saga.IsCompleted.ShouldBeTrue();
        saga.CompletedAt.ShouldNotBeNull();
        saga.WakeAt.ShouldBeNull();
        saga.Steps[^1].ShouldSatisfyAllConditions(
            step => step.Trigger.ShouldBe(nameof(MoneyArrived)),
            step => step.PreviousState.ShouldBe("AwaitingBank"),
            step => step.State.ShouldBe("Completed"));
        (await OutboxAsync()).Select(row => row.EventType).ShouldBe(["test.hold_money", "test.transfer_settled"]);

        // A completed Saga's timer never fires again.
        fixture.Clock.Advance(TimeSpan.FromHours(1));
        (await Timers.RunDueAsync(Cancel)).ShouldBe(0);
    }

    [Fact]
    public async Task Unknown_sagas_and_unhandled_messages_change_nothing()
    {
        var id = Guid.NewGuid();
        await StartAsync(id);

        (await SendAsync(Guid.NewGuid(), new MoneyArrived(id))).ShouldBe(SagaHandleOutcome.NotFound);
        (await SendAsync(id, new Unrelated(id))).ShouldBe(SagaHandleOutcome.Ignored);

        var saga = await ReadAsync(id);
        saga.Version.ShouldBe(0);
        saga.Steps.Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_timer_step_overtaken_by_a_message_is_thrown_away()
    {
        var id = Guid.NewGuid();
        await StartAsync(id);
        fixture.Bank.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // The step is waiting on the bank (no row lock held) when the bank's confirmation arrives.
        var timer = Timers.RunDueAsync(Cancel);
        await fixture.Bank.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Cancel);
        (await SendAsync(id, new MoneyArrived(id))).ShouldBe(SagaHandleOutcome.Handled);
        fixture.Bank.Gate.SetResult();
        await timer;

        var saga = await ReadAsync(id);
        saga.State.ShouldBe("Completed");
        saga.Data.BankReference.ShouldBeNull();
        saga.Steps.Select(step => step.Trigger).ShouldBe(["start", nameof(MoneyArrived)]);
    }

    [Fact]
    public async Task Messages_for_one_saga_never_interleave()
    {
        var id = Guid.NewGuid();
        await StartAsync(id);

        await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Task.Run(() => SendAsync(id, new Increment(id)), Cancel)));

        var saga = await ReadAsync(id);
        saga.Data.Count.ShouldBe(20);
        saga.Version.ShouldBe(20);
        saga.Steps.Select(step => step.Sequence).ShouldBe(Enumerable.Range(1, 21));
        saga.Steps.Skip(1).Select(step => step.Detail).ShouldBe(Enumerable.Range(1, 20).Select(count => $"Count is {count}"));
    }

    [Fact]
    public async Task A_claimed_saga_is_leased_and_is_taken_again_only_when_the_lease_runs_out()
    {
        var id = Guid.NewGuid();
        await StartAsync(id);

        // Claim it as a timer loop that then dies before running the step.
        await using (var scope = fixture.Services.CreateAsyncScope())
        {
            (await scope.ServiceProvider.GetRequiredService<SagaEngine<TestDbContext>>().ClaimDueAsync(10, Cancel)).Count.ShouldBe(1);
        }

        (await Timers.RunDueAsync(Cancel)).ShouldBe(0);
        fixture.Clock.Advance(TimeSpan.FromSeconds(SagaFixture.LeaseSeconds - 1));
        (await Timers.RunDueAsync(Cancel)).ShouldBe(0);
        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        (await Timers.RunDueAsync(Cancel)).ShouldBe(1);

        (await ReadAsync(id)).State.ShouldBe("AwaitingBank");
        fixture.Bank.CallsFor(id).ShouldBe(1);
    }

    [Fact]
    public async Task A_step_that_throws_changes_nothing_and_is_retried_after_the_retry_delay()
    {
        var id = Guid.NewGuid();
        await StartAsync(id);
        fixture.Bank.Fail = true;

        (await Timers.RunDueAsync(Cancel)).ShouldBe(1);

        var saga = await ReadAsync(id);
        saga.State.ShouldBe("Sending");
        saga.Version.ShouldBe(0);
        saga.WakeAt.ShouldNotBeNull().ShouldBe(fixture.Clock.GetUtcNow().AddSeconds(SagaFixture.RetryDelaySeconds), TimeSpan.FromMilliseconds(1));

        fixture.Bank.Fail = false;
        fixture.Clock.Advance(TimeSpan.FromSeconds(SagaFixture.RetryDelaySeconds));
        (await Timers.RunDueAsync(Cancel)).ShouldBe(1);
        (await ReadAsync(id)).State.ShouldBe("AwaitingBank");
        fixture.Bank.CallsFor(id).ShouldBe(2);
    }

    [Fact]
    public async Task Timer_loops_running_side_by_side_run_each_step_once()
    {
        var ids = Enumerable.Range(0, 30).Select(_ => Guid.NewGuid()).ToList();
        foreach (var id in ids)
        {
            await StartAsync(id);
        }

        // Three loops (three service instances), each taking batches of 10, until nothing is due.
        await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => Task.Run(
            async () =>
            {
                while (await Timers.RunDueAsync(Cancel) > 0)
                {
                }
            },
            Cancel)));

        fixture.Bank.TotalCalls.ShouldBe(30);
        ids.ShouldAllBe(id => fixture.Bank.CallsFor(id) == 1);
        await using var db = fixture.NewDbContext();
        (await db.Set<SagaInstance>().CountAsync(saga => saga.State == "AwaitingBank", Cancel)).ShouldBe(30);
    }

    [Fact]
    public async Task Inside_the_callers_transaction_a_step_commits_or_rolls_back_with_it()
    {
        var id = Guid.NewGuid();
        await StartAsync(id);

        // As the inbox does: the caller owns the transaction. Here it rolls back.
        await using (var scope = fixture.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TestDbContext>();
            await using var transaction = await db.Database.BeginTransactionAsync(Cancel);
            (await scope.ServiceProvider.GetRequiredService<SagaEngine<TestDbContext>>().HandleAsync(id, new MoneyArrived(id), "event-1", Cancel))
                .ShouldBe(SagaHandleOutcome.Handled);
            await transaction.RollbackAsync(Cancel);
        }

        (await ReadAsync(id)).State.ShouldBe("Sending");
        (await OutboxAsync()).Count.ShouldBe(1);

        // And here it commits: the causation ID is the consumed event's.
        await using (var scope = fixture.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TestDbContext>();
            await using var transaction = await db.Database.BeginTransactionAsync(Cancel);
            await scope.ServiceProvider.GetRequiredService<SagaEngine<TestDbContext>>().HandleAsync(id, new MoneyArrived(id), "event-2", Cancel);
            await transaction.CommitAsync(Cancel);
        }

        (await ReadAsync(id)).State.ShouldBe("Completed");
        var settled = (await OutboxAsync()).Single(row => row.EventType == "test.transfer_settled");
        settled.CausationId.ShouldBe("event-2");
        settled.CorrelationId.ShouldBe("corr-1");
    }
}
