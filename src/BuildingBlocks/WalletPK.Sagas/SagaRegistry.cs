namespace WalletPK.Sagas;

/// <summary>The Saga definitions a service runs, by name (saga_instances.saga_type).</summary>
public sealed class SagaRegistry
{
    private readonly Dictionary<string, ISagaDefinition> _definitions = new(StringComparer.Ordinal);

    public SagaRegistry(IEnumerable<ISagaDefinition> definitions)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        foreach (var definition in definitions)
        {
            if (!_definitions.TryAdd(definition.Name, definition))
            {
                throw new InvalidOperationException($"Two Saga definitions are named '{definition.Name}'.");
            }
        }
    }

    public IReadOnlyCollection<string> Names => _definitions.Keys;

    internal ISagaRunner Get(string sagaType) =>
        _definitions.TryGetValue(sagaType, out var definition)
            ? (ISagaRunner)definition
            : throw new InvalidOperationException($"No Saga definition is registered for '{sagaType}'.");
}

/// <summary>The engine's untyped view of a definition.</summary>
internal interface ISagaRunner
{
    ISagaChanges? Handle(SagaInstance row, object message, DateTimeOffset now);

    Task<ISagaChanges> WakeAsync(SagaInstance row, IServiceProvider services, DateTimeOffset now, CancellationToken cancellationToken);
}
