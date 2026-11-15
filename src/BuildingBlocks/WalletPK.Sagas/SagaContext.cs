using System.Text.Json;
using System.Text.Json.Serialization;
using WalletPK.Messaging.Events;

namespace WalletPK.Sagas;

/// <summary>How Saga data is stored: web JSON, enums as names.</summary>
internal static class SagaJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };
}

/// <summary>States every Saga shares.</summary>
public static class SagaStates
{
    /// <summary>Before its first step.</summary>
    public const string New = "New";
}

/// <summary>What a step changed, applied to the row by the engine.</summary>
internal interface ISagaChanges
{
    string State { get; }

    bool IsCompleted { get; }

    string SerializedData { get; }

    IReadOnlyList<(string State, string? PreviousState, string Detail)> Steps { get; }

    IReadOnlyList<IntegrationEvent> Outgoing { get; }

    /// <summary>Null when the step did not touch the timer.</summary>
    (DateTimeOffset? At, string? Reason)? Wake { get; }
}

/// <summary>
/// A Saga as one step sees it. A step reads the state and data, changes them, and says what should
/// happen next: messages to send (they go through the outbox, committed with the new state), and when to
/// wake up again. Nothing is saved unless the whole step succeeds.
/// </summary>
/// <typeparam name="TData">The definition's data (a mutable class).</typeparam>
public sealed class SagaContext<TData> : ISagaChanges
    where TData : class
{
    private readonly List<(string State, string? PreviousState, string Detail)> _steps = [];
    private readonly List<IntegrationEvent> _outgoing = [];

    internal SagaContext(Guid id, string state, TData data, DateTimeOffset now, string? wakeReason, bool isCompleted)
    {
        Id = id;
        State = state;
        Data = data;
        Now = now;
        WakeReason = wakeReason;
        IsCompleted = isCompleted;
    }

    public Guid Id { get; }

    public string State { get; private set; }

    public TData Data { get; }

    /// <summary>The time this step runs at (the engine's clock).</summary>
    public DateTimeOffset Now { get; }

    /// <summary>Why this timer step runs (null for messages).</summary>
    public string? WakeReason { get; }

    public bool IsCompleted { get; private set; }

    string ISagaChanges.SerializedData => JsonSerializer.Serialize(Data, SagaJson.Options);

    IReadOnlyList<(string State, string? PreviousState, string Detail)> ISagaChanges.Steps => _steps;

    IReadOnlyList<IntegrationEvent> ISagaChanges.Outgoing => _outgoing;

    (DateTimeOffset? At, string? Reason)? ISagaChanges.Wake => _wake;

    private (DateTimeOffset? At, string? Reason)? _wake;

    /// <summary>Moves to <paramref name="state"/> and records why on the timeline.</summary>
    public void TransitionTo(string state, string detail)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(state);
        EnsureRunning();
        _steps.Add((state, State, detail));
        State = state;
    }

    /// <summary>Records something on the timeline without changing state (a retry, a status answer ...).</summary>
    public void Note(string detail) => _steps.Add((State, State, detail));

    /// <summary>Ends the Saga in <paramref name="state"/>: it receives no more messages and its timer is cancelled.</summary>
    public void Complete(string state, string detail)
    {
        TransitionTo(state, detail);
        IsCompleted = true;
        _wake = (null, null);
    }

    /// <summary>Sends a message (command or event) once this step commits.</summary>
    public void Send(IntegrationEvent message)
    {
        ArgumentNullException.ThrowIfNull(message);
        _outgoing.Add(message);
    }

    /// <summary>Wakes the Saga at <paramref name="at"/> (replaces any earlier timer).</summary>
    public void WakeAt(DateTimeOffset at, string reason)
    {
        EnsureRunning();
        _wake = (at, reason);
    }

    public void WakeIn(TimeSpan delay, string reason) => WakeAt(Now + delay, reason);

    /// <summary>Wakes as soon as possible, for work that needs I/O (a step that handles a message may not do I/O itself).</summary>
    public void WakeNow(string reason) => WakeAt(Now, reason);

    public void CancelWake() => _wake = (null, null);

    private void EnsureRunning()
    {
        if (IsCompleted)
        {
            throw new InvalidOperationException($"Saga {Id} is already complete ({State}).");
        }
    }
}
