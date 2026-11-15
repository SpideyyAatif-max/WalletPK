using System.Text.Json;

namespace WalletPK.Sagas;

/// <summary>A Saga definition, as the engine sees it.</summary>
public interface ISagaDefinition
{
    /// <summary>Stored in saga_instances.saga_type; never rename a definition with running Sagas.</summary>
    string Name { get; }
}

/// <summary>
/// A Saga's state machine. A definition is stateless (registered once); each Saga's state and data
/// live in its saga_instances row.
/// <list type="bullet">
/// <item><b>Messages</b> (replies from other services, callbacks) are handled by the handlers registered
/// with <see cref="On{TMessage}"/>. They run inside a database transaction with the Saga row locked, so
/// they must not do I/O: to call an outside system, they schedule a timer step (<see cref="SagaContext{TData}.WakeNow"/>).</item>
/// <item><b>Timer steps</b> (<see cref="OnWakeAsync"/>) may do I/O: they run outside any transaction, and
/// their outcome is saved afterwards only if the Saga did not change meanwhile. A crash in between simply
/// repeats the step, so the calls they make must be idempotent (WalletPK's providers are, by request ID).</item>
/// </list>
/// </summary>
/// <typeparam name="TData">The Saga's data, stored as JSON.</typeparam>
public abstract class SagaDefinition<TData> : ISagaDefinition, ISagaRunner
    where TData : class
{
    private readonly Dictionary<Type, Action<SagaContext<TData>, object>> _handlers = [];

    public abstract string Name { get; }

    /// <summary>Registers how a message moves the Saga on. Messages without a handler are ignored.</summary>
    protected void On<TMessage>(Action<SagaContext<TData>, TMessage> handler)
        where TMessage : class
    {
        ArgumentNullException.ThrowIfNull(handler);
        _handlers[typeof(TMessage)] = (saga, message) => handler(saga, (TMessage)message);
    }

    /// <summary>
    /// The first step of a new Saga (state "New"): move to the first state, send the first messages,
    /// set the first timer. Runs in the transaction that inserts the Saga, so it must not do I/O.
    /// </summary>
    protected abstract void OnStart(SagaContext<TData> saga);

    /// <summary>
    /// A timer step; <paramref name="services"/> is a fresh scope for this step. A timer fires once: if
    /// the step sets no new timer, the Saga sleeps until a message arrives. A step that has nothing to do
    /// in the current state (a late or repeated timer) should simply return.
    /// </summary>
    protected virtual Task OnWakeAsync(SagaContext<TData> saga, IServiceProvider services, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    internal bool Handles(Type messageType) => _handlers.ContainsKey(messageType);

    internal SagaContext<TData> Load(SagaInstance row, DateTimeOffset now, bool forTimer) =>
        new(row.Id, row.State, JsonSerializer.Deserialize<TData>(row.Data, SagaJson.Options)!, now, forTimer ? row.WakeReason : null, row.IsCompleted);

    ISagaChanges? ISagaRunner.Handle(SagaInstance row, object message, DateTimeOffset now)
    {
        if (!_handlers.TryGetValue(message.GetType(), out var handler))
        {
            return null;
        }

        var saga = Load(row, now, forTimer: false);
        handler(saga, message);
        return saga;
    }

    async Task<ISagaChanges> ISagaRunner.WakeAsync(SagaInstance row, IServiceProvider services, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var saga = Load(row, now, forTimer: true);
        await OnWakeAsync(saga, services, cancellationToken);
        return saga;
    }

    internal SagaContext<TData> Begin(Guid id, TData data, DateTimeOffset now)
    {
        var saga = new SagaContext<TData>(id, SagaStates.New, data, now, null, isCompleted: false);
        OnStart(saga);
        return saga;
    }
}
