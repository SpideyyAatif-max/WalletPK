using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using WalletPK.Messaging;
using WalletPK.Messaging.Events;

namespace WalletPK.Sagas.Tests.Infrastructure;

/// <summary>A command the test Saga sends when it starts.</summary>
[IntegrationEvent("test.hold_money", KafkaTopics.LedgerCommands)]
public sealed record HoldMoney(Guid TransferId, long Amount) : IntegrationEvent
{
    public override string GetPartitionKey() => TransferId.ToString();
}

/// <summary>The event it sends when it completes.</summary>
[IntegrationEvent("test.transfer_settled", KafkaTopics.LedgerCommands)]
public sealed record TransferSettled(Guid TransferId) : IntegrationEvent
{
    public override string GetPartitionKey() => TransferId.ToString();
}

public sealed record MoneyArrived(Guid TransferId);

public sealed record Increment(Guid TransferId);

public sealed record Unrelated(Guid TransferId);

public sealed class TestTransferData
{
    public Guid UserId { get; set; }

    public long Amount { get; set; }

    public int Count { get; set; }

    public int SendAttempts { get; set; }

    public string? BankReference { get; set; }
}

/// <summary>
/// A bank transfer in miniature: start (Sending, hold command, wake now) → a timer step calls the bank
/// (I/O) → AwaitingBank with a status-check timer → the bank's message completes it.
/// </summary>
public sealed class TestTransferSaga : SagaDefinition<TestTransferData>
{
    public TestTransferSaga()
    {
        On<MoneyArrived>((saga, _) =>
        {
            saga.Send(new TransferSettled(saga.Id));
            saga.Complete("Completed", "The bank confirmed it");
        });
        On<Increment>((saga, _) =>
        {
            saga.Data.Count++;
            saga.Note($"Count is {saga.Data.Count}");
        });
    }

    public override string Name => "test-transfer";

    protected override void OnStart(SagaContext<TestTransferData> saga)
    {
        saga.TransitionTo("Sending", $"Accepted {saga.Data.Amount}");
        saga.Send(new HoldMoney(saga.Id, saga.Data.Amount));
        saga.WakeNow("send");
    }

    protected override async Task OnWakeAsync(SagaContext<TestTransferData> saga, IServiceProvider services, CancellationToken cancellationToken)
    {
        switch (saga.WakeReason)
        {
            case "send" when saga.State == "Sending":
                saga.Data.SendAttempts++;
                saga.Data.BankReference = await services.GetRequiredService<FakeBank>().SendAsync(saga.Id, cancellationToken);
                saga.TransitionTo("AwaitingBank", $"Sent; bank reference {saga.Data.BankReference}");
                saga.WakeIn(TimeSpan.FromMinutes(5), "status-check");
                break;
            case "status-check":
                saga.Note("Still waiting for the bank");
                saga.WakeIn(TimeSpan.FromMinutes(5), "status-check");
                break;
            default:
                // A late or repeated timer: nothing to do.
                break;
        }
    }
}

/// <summary>An outside system: counts calls, can be held (to overtake a step) or made to fail.</summary>
public sealed class FakeBank
{
    private readonly ConcurrentDictionary<Guid, int> _calls = new();

    public TaskCompletionSource? Gate { get; set; }

    public TaskCompletionSource Entered { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public bool Fail { get; set; }

    public int CallsFor(Guid id) => _calls.GetValueOrDefault(id);

    public int TotalCalls => _calls.Values.Sum();

    public async Task<string> SendAsync(Guid id, CancellationToken cancellationToken)
    {
        _calls.AddOrUpdate(id, 1, (_, count) => count + 1);
        Entered.TrySetResult();
        if (Gate is { } gate)
        {
            await gate.Task.WaitAsync(cancellationToken);
        }

        if (Fail)
        {
            throw new HttpRequestException("Bank unavailable (test)");
        }

        await Task.Yield();
        return $"BNK-{id.ToString()[..8]}";
    }

    public void Reset()
    {
        _calls.Clear();
        Gate = null;
        Fail = false;
        Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
